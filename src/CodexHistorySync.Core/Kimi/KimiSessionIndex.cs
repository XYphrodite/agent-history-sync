using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexHistorySync.Core.Kimi;

/// <summary>
/// Reads and merges <c>session_index.jsonl</c>, the shared list that decides which sessions Kimi
/// Code CLI shows for resumption.
///
/// Everything here exists because the file is shared: it holds sessions this repository has never
/// heard of and Kimi rewrites it on every session change. A merge therefore replaces exactly one
/// line and carries every other line through byte-for-byte, and a line that does not parse is kept
/// verbatim rather than dropped — an import must never destroy an index it cannot fully read.
/// </summary>
public static class KimiSessionIndex
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        // Kimi writes one compact JSON object per line, with non-ASCII titles kept as UTF-8.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Parses the index into entries. A missing or empty file is an empty list, but a line that is
    /// not a JSON object throws, because the caller must refuse to write rather than replace a file
    /// it cannot read. Blank lines are skipped.
    /// </summary>
    public static List<JsonObject> Parse(string? content)
    {
        var entries = new List<JsonObject>();
        if (string.IsNullOrWhiteSpace(content)) return entries;

        foreach (var line in SplitLines(content))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The Kimi session index contains an invalid line.", exception);
            }
            if (node is not JsonObject entry)
                throw new InvalidDataException("The Kimi session index line is not an object.");
            entries.Add(entry);
        }

        return entries;
    }

    public static JsonObject? Find(IEnumerable<JsonObject> entries, string sessionId) =>
        entries.FirstOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(SessionIdOf(entry), sessionId));

    public static string? SessionIdOf(JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.TryGetPropertyValue("sessionId", out var value) && value is JsonValue id &&
               id.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    /// <summary>
    /// Merges one entry into the index text and returns the text to write. Every other line is kept
    /// exactly as it was — most of them describe sessions this repository has never seen. The entry
    /// for this session replaces its whole line outright: the entry is not part of the object hash,
    /// but a stale <c>sessionDir</c> on another machine would point Kimi at a path that does not
    /// exist, so the freshly written absolute path always wins.
    /// </summary>
    public static string Merge(string? content, JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sessionId = SessionIdOf(entry)
            ?? throw new InvalidDataException("The Kimi index entry carries no session id.");

        var lines = SplitLines(content);
        var merged = new List<string>(lines.Count + 1);
        var replaced = false;
        foreach (var line in lines)
        {
            if (!replaced && TryReadSessionId(line, out var lineSessionId) &&
                StringComparer.OrdinalIgnoreCase.Equals(lineSessionId, sessionId))
            {
                merged.Add(SerializeEntry(entry));
                replaced = true;
            }
            else
            {
                merged.Add(line);
            }
        }

        if (!replaced) merged.Add(SerializeEntry(entry));
        return string.Join("\n", merged);
    }

    /// <summary>Removes one session's line and returns the text to write; unparseable lines stay.</summary>
    public static string Remove(string? content, string sessionId)
    {
        var lines = SplitLines(content);
        var kept = lines.Where(line =>
            !TryReadSessionId(line, out var lineSessionId) ||
            !StringComparer.OrdinalIgnoreCase.Equals(lineSessionId, sessionId));
        return string.Join("\n", kept);
    }

    /// <summary>
    /// Builds the entry for a session. Unlike Continue's synthesized entry this carries no derived
    /// metadata: Kimi's own records are exactly these three fields, and the absolute sessionDir must
    /// always describe the machine the index is written on.
    /// </summary>
    public static JsonObject CreateEntry(string sessionId, string sessionDir, string workDir) =>
        new()
        {
            ["sessionId"] = sessionId,
            ["sessionDir"] = sessionDir,
            ["workDir"] = workDir
        };

    public static string SerializeEntry(JsonObject entry) => entry.ToJsonString(WriteOptions);

    private static bool TryReadSessionId(string line, out string? sessionId)
    {
        sessionId = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return false; }
        if (node is not JsonObject entry) return false;
        sessionId = SessionIdOf(entry);
        return sessionId is not null;
    }

    private static List<string> SplitLines(string? content) =>
        string.IsNullOrEmpty(content) ? [] : content.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None).ToList();
}
