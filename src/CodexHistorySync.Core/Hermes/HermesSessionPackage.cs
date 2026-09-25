using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Hermes;

/// <summary>
/// One Hermes session as a canonical JSON document: the <c>sessions</c> row and its <c>messages</c>
/// rows. SQLite row ids are local and are not part of the document, so two machines hash the same
/// conversation the same way. Null cells are omitted; a column the other machine does not have
/// therefore does not change the hash.
/// </summary>
public static class HermesSessionPackage
{
    public const int SchemaVersion = 1;
    public const string LogicalIdPrefix = "he-";

    private static readonly Regex RealPattern = new(
        @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsHermesLogicalId(string value) => TrySplitLogicalId(value, out _, out _);

    public static string ToLogicalId(string profile, string sessionId)
    {
        if (!HermesPaths.IsProfileName(profile))
            throw new ArgumentException("Hermes profile name is invalid.", nameof(profile));
        if (!HermesPaths.IsSessionId(sessionId))
            throw new ArgumentException("Hermes session id is invalid.", nameof(sessionId));
        return LogicalIdPrefix + profile + "~" + sessionId;
    }

    public static bool TrySplitLogicalId(string value, out string profile, out string sessionId)
    {
        profile = string.Empty;
        sessionId = string.Empty;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal)) return false;
        var body = value[LogicalIdPrefix.Length..];
        var separator = body.IndexOf('~');
        if (separator <= 0 || separator >= body.Length - 1) return false;
        profile = body[..separator];
        sessionId = body[(separator + 1)..];
        return HermesPaths.IsProfileName(profile) && HermesPaths.IsSessionId(sessionId);
    }

    public static IReadOnlyList<(string SessionId, double LastActiveUnix)> ReadActivity(HermesPaths paths) =>
        HermesSessionDatabase.ReadProfiles(paths).Snapshots
            .Select(snapshot => (snapshot.SessionId, snapshot.LastActiveUnix))
            .ToArray();

    public static byte[]? TryBuild(HermesPaths paths, string profile, string sessionId)
    {
        var snapshot = HermesSessionDatabase.ReadOne(paths, profile, sessionId);
        return snapshot is null ? null : Serialize(snapshot);
    }

    public static ContentHash HashPackage(byte[] package) =>
        new(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());

    public static string LogicalIdOf(byte[] package)
    {
        var snapshot = Parse(package);
        return ToLogicalId(snapshot.Profile, snapshot.SessionId);
    }

    public static byte[] Fingerprint(string anchorPath)
    {
        var package = BuildFromAnchor(anchorPath)
            ?? throw new InvalidDataException("The selected session could not be validated.");
        return SHA256.HashData(package);
    }

    public static ContentHash? HashAnchor(string anchorPath)
    {
        try
        {
            var package = BuildFromAnchor(anchorPath);
            return package is null ? null : HashPackage(package);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or JsonException or SqliteException or DecoderFallbackException)
        {
            return null;
        }
    }

    public static void Materialize(HermesPaths paths, byte[] package)
    {
        ArgumentNullException.ThrowIfNull(paths);
        HermesSessionDatabase.Write(paths, Parse(package));
    }

    public static void MaterializeAnchor(string anchorPath)
    {
        if (!HermesPaths.TryParseAnchor(anchorPath, out var home, out var profile, out var sessionId))
            throw new InvalidDataException("Hermes anchor path is invalid.");
        var package = File.ReadAllBytes(anchorPath);
        var snapshot = Parse(package);
        if (!string.Equals(snapshot.Profile, profile, StringComparison.Ordinal) ||
            !string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidDataException("Hermes anchor path does not match the package.");
        HermesSessionDatabase.Write(new HermesPaths(home), snapshot);
    }

    public static void WriteAnchorSnapshot(string anchorPath)
    {
        var package = BuildFromAnchor(anchorPath)
            ?? throw new InvalidDataException("Hermes session is not in the state database.");
        var directory = Path.GetDirectoryName(anchorPath)
            ?? throw new InvalidDataException("Hermes anchor path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = anchorPath + ".tmp";
        File.WriteAllBytes(temporary, package);
        File.Move(temporary, anchorPath, overwrite: true);
    }

    public static void DeleteAnchor(string anchorPath)
    {
        if (!HermesPaths.TryParseAnchor(anchorPath, out var home, out var profile, out var sessionId))
            throw new InvalidDataException("Hermes anchor path is invalid.");
        HermesSessionDatabase.Delete(new HermesPaths(home), profile, sessionId);
    }

    private static byte[]? BuildFromAnchor(string anchorPath)
    {
        if (!HermesPaths.TryParseAnchor(anchorPath, out var home, out var profile, out var sessionId))
            return null;
        return TryBuild(new HermesPaths(home), profile, sessionId);
    }

    private static byte[] Serialize(HermesSnapshot snapshot)
    {
        var document = new PackageDto(
            SchemaVersion,
            snapshot.Profile,
            snapshot.SessionId,
            snapshot.Session.Select(CellDto.From).ToArray(),
            snapshot.Messages.Select(message => message.Select(CellDto.From).ToArray()).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private static HermesSnapshot Parse(byte[] package)
    {
        PackageDto? dto;
        try { dto = JsonSerializer.Deserialize<PackageDto>(package, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("Hermes session package is malformed.", exception); }
        if (dto is null || dto.V != SchemaVersion) throw new InvalidDataException("Hermes session package schema is unsupported.");
        if (!HermesPaths.IsProfileName(dto.Profile) || !HermesPaths.IsSessionId(dto.Id))
            throw new InvalidDataException("Hermes session package id is invalid.");

        var session = ParseCells(dto.Session, "session");
        RequireText(session, "id", dto.Id);
        if (Find(session, "source") is not { Type: "text" })
            throw new InvalidDataException("Hermes session package has no source.");
        if (Find(session, "started_at") is null)
            throw new InvalidDataException("Hermes session package has no started_at.");

        var messages = new List<IReadOnlyList<HermesCell>>(dto.Messages.Length);
        foreach (var message in dto.Messages)
        {
            var cells = ParseCells(message, "message");
            RequireText(cells, "session_id", dto.Id);
            if (Find(cells, "role") is not { Type: "text" })
                throw new InvalidDataException("Hermes message has no role.");
            if (Find(cells, "timestamp") is null)
                throw new InvalidDataException("Hermes message has no timestamp.");
            messages.Add(cells);
        }

        return new HermesSnapshot(dto.Profile, dto.Id, session, messages, 0);
    }

    private static List<HermesCell> ParseCells(CellDto[] cells, string label)
    {
        if (cells is null || cells.Length == 0) throw new InvalidDataException($"Hermes {label} is empty.");
        var parsed = new List<HermesCell>(cells.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (cell.N is null || !ColumnName.IsMatch(cell.N) || !names.Add(cell.N))
                throw new InvalidDataException($"Hermes {label} column is invalid.");
            if (string.Equals(cell.N, "id", StringComparison.Ordinal) && label == "message")
                throw new InvalidDataException("Hermes message packages do not carry local row ids.");
            parsed.Add(ParseCell(cell));
        }

        parsed.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return parsed;
    }

    private static HermesCell ParseCell(CellDto cell) => cell.T switch
    {
        "integer" when cell.I is not null && cell.S is null && cell.B is null =>
            new HermesCell(cell.N!, "integer", null, cell.I, null),
        "real" when cell.S is not null && cell.S.Length <= 64 && RealPattern.IsMatch(cell.S) && cell.I is null && cell.B is null =>
            new HermesCell(cell.N!, "real", cell.S, null, null),
        "text" when cell.S is not null && cell.I is null && cell.B is null =>
            new HermesCell(cell.N!, "text", cell.S, null, null),
        "blob" when cell.B is not null && cell.S is null && cell.I is null && IsBase64(cell.B) =>
            new HermesCell(cell.N!, "blob", null, null, cell.B),
        _ => throw new InvalidDataException("Hermes session package value is invalid.")
    };

    private static void RequireText(IReadOnlyList<HermesCell> cells, string name, string expected)
    {
        if (Find(cells, name) is not { Type: "text", Text: var text } || !string.Equals(text, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Hermes session package {name} does not match the session.");
    }

    private static HermesCell? Find(IReadOnlyList<HermesCell> cells, string name) =>
        cells.FirstOrDefault(cell => string.Equals(cell.Name, name, StringComparison.Ordinal));

    private static bool IsBase64(string value)
    {
        try
        {
            _ = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static readonly Regex ColumnName = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record PackageDto(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("profile")] string Profile,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("session")] CellDto[] Session,
        [property: JsonPropertyName("messages")] CellDto[][] Messages);

    private sealed record CellDto(
        [property: JsonPropertyName("n")] string? N,
        [property: JsonPropertyName("t")] string? T,
        [property: JsonPropertyName("s")] string? S,
        [property: JsonPropertyName("i")] long? I,
        [property: JsonPropertyName("b")] string? B)
    {
        public static CellDto From(HermesCell cell) => cell.Type switch
        {
            "integer" => new CellDto(cell.Name, "integer", null, cell.Integer, null),
            "real" => new CellDto(cell.Name, "real", cell.Text, null, null),
            "text" => new CellDto(cell.Name, "text", cell.Text, null, null),
            "blob" => new CellDto(cell.Name, "blob", null, null, cell.BlobBase64),
            _ => throw new InvalidDataException("Hermes value type is unsupported.")
        };
    }
}
