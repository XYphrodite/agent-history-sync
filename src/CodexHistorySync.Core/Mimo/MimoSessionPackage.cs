using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Mimo;

public static class MimoSessionPackage
{
    public const int SchemaVersion = 1;
    public const string LogicalIdPrefix = "mi-";

    private static readonly Regex RealPattern = new(
        @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsMimoLogicalId(string value) => TrySplitLogicalId(value, out _, out _);

    public static string ToLogicalId(string sessionId)
    {
        if (!MimoPaths.IsSessionId(sessionId))
            throw new ArgumentException("MiMo session id is invalid.", nameof(sessionId));
        return LogicalIdPrefix + sessionId;
    }

    public static bool TrySplitLogicalId(string value, out string sessionId)
    {
        sessionId = string.Empty;
        if (string.IsNullOrEmpty(value) || !value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal)) return false;
        var body = value[LogicalIdPrefix.Length..];
        if (!MimoPaths.IsSessionId(body)) return false;
        sessionId = body;
        return true;
    }

    public static bool TrySplitLogicalId(string value, out string profile, out string sessionId)
    {
        // Compatibility shim for callers expecting Hermes style: profile is always "default"
        profile = "default";
        return TrySplitLogicalId(value, out sessionId);
    }

    public static IReadOnlyList<(string SessionId, double LastActiveUnix)> ReadActivity(MimoPaths paths) =>
        MimoSessionDatabase.ReadAll(paths).Snapshots
            .Select(snapshot => (snapshot.SessionId, snapshot.LastActiveUnix))
            .ToArray();

    public static byte[]? TryBuild(MimoPaths paths, string sessionId)
    {
        var snapshot = MimoSessionDatabase.ReadOne(paths, sessionId);
        return snapshot is null ? null : Serialize(snapshot);
    }

    public static ContentHash HashPackage(byte[] package) =>
        new(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());

    public static string LogicalIdOf(byte[] package)
    {
        var snapshot = Parse(package);
        return ToLogicalId(snapshot.SessionId);
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

    public static void Materialize(MimoPaths paths, byte[] package)
    {
        ArgumentNullException.ThrowIfNull(paths);
        MimoSessionDatabase.Write(paths, Parse(package));
    }

    public static void MaterializeAnchor(string anchorPath)
    {
        if (!MimoPaths.TryParseAnchor(anchorPath, out var home, out var sessionId))
            throw new InvalidDataException("MiMo anchor path is invalid.");
        var package = File.ReadAllBytes(anchorPath);
        var snapshot = Parse(package);
        if (!string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidDataException("MiMo anchor path does not match the package.");
        MimoSessionDatabase.Write(new MimoPaths(home), snapshot);
    }

    public static void WriteAnchorSnapshot(string anchorPath)
    {
        var package = BuildFromAnchor(anchorPath)
            ?? throw new InvalidDataException("MiMo session is not in the state database.");
        var directory = Path.GetDirectoryName(anchorPath)
            ?? throw new InvalidDataException("MiMo anchor path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = anchorPath + ".tmp";
        File.WriteAllBytes(temporary, package);
        File.Move(temporary, anchorPath, overwrite: true);
    }

    public static void DeleteAnchor(string anchorPath)
    {
        if (!MimoPaths.TryParseAnchor(anchorPath, out var home, out var sessionId))
            throw new InvalidDataException("MiMo anchor path is invalid.");
        MimoSessionDatabase.Delete(new MimoPaths(home), sessionId);
    }

    private static byte[]? BuildFromAnchor(string anchorPath)
    {
        if (!MimoPaths.TryParseAnchor(anchorPath, out var home, out var sessionId))
            return null;
        return TryBuild(new MimoPaths(home), sessionId);
    }

    private static byte[] Serialize(MimoSnapshot snapshot)
    {
        var document = new PackageDto(
            SchemaVersion,
            snapshot.SessionId,
            snapshot.Session.Select(CellDto.From).ToArray(),
            snapshot.Messages.Select(message => message.Select(CellDto.From).ToArray()).ToArray(),
            snapshot.Parts.Select(msgParts => msgParts.Select(part => part.Cells.Select(CellDto.From).ToArray()).ToArray()).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private static MimoSnapshot Parse(byte[] package)
    {
        PackageDto? dto;
        try { dto = JsonSerializer.Deserialize<PackageDto>(package, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("MiMo session package is malformed.", exception); }
        if (dto is null || dto.V != SchemaVersion) throw new InvalidDataException("MiMo session package schema is unsupported.");
        if (!MimoPaths.IsSessionId(dto.Id))
            throw new InvalidDataException("MiMo session package id is invalid.");

        var session = ParseCells(dto.Session, "session");
        RequireText(session, "id", dto.Id);

        var messages = new List<IReadOnlyList<MimoCell>>(dto.Messages.Length);
        for (var i = 0; i < dto.Messages.Length; i++)
        {
            var cells = ParseCells(dto.Messages[i], "message");
            RequireText(cells, "session_id", dto.Id);
            if (Find(cells, "id") is not { Type: "text" })
                throw new InvalidDataException("MiMo message has no id.");
            messages.Add(cells);
        }

        var parts = new List<IReadOnlyList<MimoPart>>(dto.Parts.Length);
        for (var i = 0; i < dto.Parts.Length; i++)
        {
            var msgParts = new List<MimoPart>(dto.Parts[i].Length);
            foreach (var partCellsDto in dto.Parts[i])
            {
                var cells = ParseCells(partCellsDto, "part");
                // part must have id and message_id
                if (Find(cells, "id") is not { Type: "text" })
                    throw new InvalidDataException("MiMo part has no id.");
                msgParts.Add(new MimoPart(cells));
            }
            parts.Add(msgParts);
        }

        // Ensure parts count matches messages count
        while (parts.Count < messages.Count) parts.Add([]);
        while (parts.Count > messages.Count) parts.RemoveAt(parts.Count - 1);

        return new MimoSnapshot(dto.Id, session, messages, parts, 0);
    }

    private static List<MimoCell> ParseCells(CellDto[] cells, string label)
    {
        if (cells is null || cells.Length == 0) throw new InvalidDataException($"MiMo {label} is empty.");
        var parsed = new List<MimoCell>(cells.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            if (cell.N is null || !ColumnName.IsMatch(cell.N) || !names.Add(cell.N))
                throw new InvalidDataException($"MiMo {label} column is invalid.");
            parsed.Add(ParseCell(cell));
        }

        parsed.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return parsed;
    }

    private static MimoCell ParseCell(CellDto cell) => cell.T switch
    {
        "integer" when cell.I is not null && cell.S is null && cell.B is null =>
            new MimoCell(cell.N!, "integer", null, cell.I, null),
        "real" when cell.S is not null && cell.S.Length <= 64 && RealPattern.IsMatch(cell.S) && cell.I is null && cell.B is null =>
            new MimoCell(cell.N!, "real", cell.S, null, null),
        "text" when cell.S is not null && cell.I is null && cell.B is null =>
            new MimoCell(cell.N!, "text", cell.S, null, null),
        "blob" when cell.B is not null && cell.S is null && cell.I is null && IsBase64(cell.B) =>
            new MimoCell(cell.N!, "blob", null, null, cell.B),
        _ => throw new InvalidDataException("MiMo session package value is invalid.")
    };

    private static void RequireText(IReadOnlyList<MimoCell> cells, string name, string expected)
    {
        if (Find(cells, name) is not { Type: "text", Text: var text } || !string.Equals(text, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"MiMo session package {name} does not match the session.");
    }

    private static MimoCell? Find(IReadOnlyList<MimoCell> cells, string name) =>
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
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("session")] CellDto[] Session,
        [property: JsonPropertyName("messages")] CellDto[][] Messages,
        [property: JsonPropertyName("parts")] CellDto[][][] Parts);

    private sealed record CellDto(
        [property: JsonPropertyName("n")] string? N,
        [property: JsonPropertyName("t")] string? T,
        [property: JsonPropertyName("s")] string? S,
        [property: JsonPropertyName("i")] long? I,
        [property: JsonPropertyName("b")] string? B)
    {
        public static CellDto From(MimoCell cell) => cell.Type switch
        {
            "integer" => new CellDto(cell.Name, "integer", null, cell.Integer, null),
            "real" => new CellDto(cell.Name, "real", cell.Text, null, null),
            "text" => new CellDto(cell.Name, "text", cell.Text, null, null),
            "blob" => new CellDto(cell.Name, "blob", null, null, cell.BlobBase64),
            _ => throw new InvalidDataException("MiMo value type is unsupported.")
        };
    }
}
