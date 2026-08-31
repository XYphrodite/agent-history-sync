using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Continue;

/// <summary>
/// Compact portable representation of a Continue session for encrypted sync.
///
/// Unlike the other three agents, a Continue session is not self-contained on disk: the session
/// file holds the conversation and the shared <c>sessions.json</c> decides whether Continue can
/// see it. One without the other is useless — restore only the file and the session is invisible,
/// restore only the entry and opening it throws — so the package carries both (design C1).
/// </summary>
public static class ContinueSessionPackage
{
    public const int SchemaVersion = 1;
    public const string LogicalIdPrefix = "co-";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex SessionIdPattern = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static bool IsContinueLogicalId(string value) =>
        value.StartsWith(LogicalIdPrefix, StringComparison.Ordinal) &&
        SessionIdPattern.IsMatch(value[LogicalIdPrefix.Length..]);

    public static string ToLogicalId(string sessionId)
    {
        if (!SessionIdPattern.IsMatch(sessionId))
            throw new ArgumentException("Continue session id must be a UUID.", nameof(sessionId));
        return LogicalIdPrefix + sessionId.ToLowerInvariant();
    }

    public static string SessionIdFromLogicalId(string logicalId)
    {
        if (!IsContinueLogicalId(logicalId))
            throw new ArgumentException("Not a Continue logical object id.", nameof(logicalId));
        return logicalId[LogicalIdPrefix.Length..];
    }

    /// <summary>
    /// Builds a package from one session file. <paramref name="indexContent"/> is the text of the
    /// shared index, read once per scan by the caller; a session it does not list gets a
    /// synthesized entry rather than being refused (design C2).
    /// </summary>
    public static byte[] BuildFromFile(string sessionFilePath, string? indexContent)
    {
        if (!File.Exists(sessionFilePath))
            throw new FileNotFoundException("Continue session file is missing.", sessionFilePath);
        if (ContinuePaths.IsIndexFile(sessionFilePath))
            throw new InvalidDataException("The Continue session index is not a session.");

        var sessionId = Path.GetFileNameWithoutExtension(sessionFilePath);
        if (!SessionIdPattern.IsMatch(sessionId))
            throw new InvalidDataException("Continue session file name is not a UUID.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(sessionFilePath), ".json"))
            throw new InvalidDataException("Continue session file must use the .json extension.");

        var session = NormalizeNewlines(File.ReadAllText(sessionFilePath, Utf8));
        var document = ParseSession(session, sessionId);
        var entry = ResolveEntry(indexContent, sessionId, document, sessionFilePath);

        var package = new PackageDto(SchemaVersion, sessionId.ToLowerInvariant(),
            entry.ToJsonString(JsonOptions), session);
        return JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
    }

    public static ContentHash HashPackage(byte[] package) =>
        new(Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant());

    public static PackageInfo Parse(byte[] package)
    {
        PackageDto? dto;
        try { dto = JsonSerializer.Deserialize<PackageDto>(package, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("Continue session package is malformed.", exception); }
        if (dto is null || dto.V != SchemaVersion) throw new InvalidDataException("Continue session package schema is unsupported.");
        if (!SessionIdPattern.IsMatch(dto.Id)) throw new InvalidDataException("Continue session package id is invalid.");
        if (string.IsNullOrWhiteSpace(dto.Entry) || string.IsNullOrWhiteSpace(dto.Session))
            throw new InvalidDataException("Continue session package is incomplete.");

        JsonNode? entryNode;
        try { entryNode = JsonNode.Parse(dto.Entry); }
        catch (JsonException exception) { throw new InvalidDataException("Continue session package entry is malformed.", exception); }
        if (entryNode is not JsonObject entry) throw new InvalidDataException("Continue session package entry is not an object.");

        var sessionId = dto.Id.ToLowerInvariant();
        // The entry decides which session the index will claim this is, so it may not disagree
        // with the object being imported.
        if (!StringComparer.OrdinalIgnoreCase.Equals(ContinueSessionIndex.SessionIdOf(entry), sessionId))
            throw new InvalidDataException("Continue session package entry names a different session.");

        var session = NormalizeNewlines(dto.Session);
        ParseSession(session, sessionId);
        return new PackageInfo(sessionId, entry, Utf8.GetBytes(session));
    }

    /// <summary>
    /// Writes the session and merges its entry into the shared index. Both are replaced through a
    /// same-directory move, so a crash leaves either the old file or the new one and never a
    /// half-written index that would stop Continue from creating sessions at all.
    /// </summary>
    public static void Materialize(PackageInfo package, ContinuePaths paths)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(paths);

        var destination = paths.SessionFilePath(package.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ReplaceAtomically(destination, package.Session);

        var indexPath = paths.IndexFilePath;
        var current = File.Exists(indexPath) ? File.ReadAllText(indexPath, Utf8) : null;
        var merged = ContinueSessionIndex.Merge(current, package.Entry);
        if (string.Equals(current, merged, StringComparison.Ordinal)) return;
        ReplaceAtomically(indexPath, Utf8.GetBytes(merged));
    }

    private static void ReplaceAtomically(string destination, byte[] content)
    {
        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static JsonObject ParseSession(string session, string sessionId)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(session); }
        catch (JsonException exception) { throw new InvalidDataException("Continue session file is malformed.", exception); }
        if (node is not JsonObject document) throw new InvalidDataException("Continue session file is not an object.");

        var declared = document.TryGetPropertyValue("sessionId", out var value) && value is JsonValue id &&
                       id.TryGetValue<string>(out var text)
            ? text
            : null;
        if (declared is null)
            throw new InvalidDataException("Continue session file carries no session id.");
        if (!StringComparer.OrdinalIgnoreCase.Equals(declared, sessionId))
            throw new InvalidDataException("Continue session file disagrees with the session file name.");

        return document;
    }

    private static JsonObject ResolveEntry(string? indexContent, string sessionId, JsonObject session, string path)
    {
        List<JsonObject> entries;
        try { entries = ContinueSessionIndex.Parse(indexContent); }
        catch (InvalidDataException)
        {
            // A broken index must not stop a session from being backed up: it is exactly the state
            // in which the user most wants a copy elsewhere.
            entries = [];
        }

        var existing = ContinueSessionIndex.Find(entries, sessionId);
        if (existing is not null) return (JsonObject)existing.DeepClone();

        return ContinueSessionIndex.Synthesize(
            sessionId,
            ReadString(session, "title"),
            ReadString(session, "workspaceDirectory"),
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            CountMessages(session));
    }

    private static string? ReadString(JsonObject document, string property) =>
        document.TryGetPropertyValue(property, out var value) && value is JsonValue text &&
        text.TryGetValue<string>(out var result)
            ? result
            : null;

    /// <summary>
    /// Approximate message count for a synthesized entry only. Continue computes its own on every
    /// save, so this value survives exactly until the session is next written in Continue.
    /// </summary>
    private static int CountMessages(JsonObject session)
    {
        if (!session.TryGetPropertyValue("history", out var value) || value is not JsonArray history) return 0;
        var count = 0;
        foreach (var element in history)
        {
            if (element is not JsonObject entry ||
                !entry.TryGetPropertyValue("message", out var message) || message is not JsonObject record)
                continue;
            var role = ReadString(record, "role");
            if (role is "user" or "assistant") count++;
        }

        return count;
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed record PackageDto(int V, string Id, string Entry, string Session);

    public sealed record PackageInfo(string SessionId, JsonObject Entry, byte[] Session);
}
