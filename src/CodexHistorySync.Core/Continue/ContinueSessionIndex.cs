using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexHistorySync.Core.Continue;

/// <summary>
/// Reads and merges <c>sessions/sessions.json</c>, the shared list that decides which sessions
/// Continue can see.
///
/// Everything here exists because the file is shared. It holds sessions this repository has never
/// heard of, Continue rewrites it on every save, and it refuses to create a session at all when
/// the file does not parse — so an import merges one entry into what is already there and never
/// writes its own idea of the whole list. Formatting matches what the extension itself writes,
/// so an import that changes nothing leaves the bytes alone.
/// </summary>
public static class ContinueSessionIndex
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        // Continue writes this file from JavaScript, which never varies by platform. .NET indents
        // with Environment.NewLine, so on Windows every import would rewrite all of it as CRLF.
        NewLine = "\n",
        // JSON.stringify also leaves non-ASCII, '<', '>', and '&' as they are; the default .NET
        // encoder escapes them, which would rewrite every entry with a non-English title.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Parses the index. A missing or empty file is an empty list — Continue creates it lazily —
    /// but anything that is not a JSON array throws, because the caller must refuse to write
    /// rather than replace a file it cannot read.
    /// </summary>
    public static List<JsonObject> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];

        JsonNode? node;
        try { node = JsonNode.Parse(content); }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Continue session index is not valid JSON.", exception);
        }

        if (node is not JsonArray array)
            throw new InvalidDataException("The Continue session index is not a JSON array.");

        var entries = new List<JsonObject>(array.Count);
        foreach (var element in array)
        {
            // Continue's own list() skips legacy entries instead of failing; a non-object entry is
            // carried through untouched rather than dropped, so a merge never loses a line.
            if (element is JsonObject entry) entries.Add(entry);
            else if (element is not null) entries.Add(new JsonObject { ["__unmodelled"] = element.DeepClone() });
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
    /// Merges one entry into the index text and returns the text to write. Every other entry is
    /// left exactly as it was — that is the preservation that matters, because most of them
    /// describe sessions this repository has never seen.
    ///
    /// The entry for this session is replaced outright rather than merged member by member.
    /// Keeping members the incoming entry does not carry would sound safer and is not: the object
    /// hash is computed over the session together with this entry, so an entry that differs
    /// between two machines makes each of them see the other's copy as changed, and one session
    /// would be republished back and forth forever.
    ///
    /// A new entry is appended, which is where the extension appends and therefore where its
    /// reversed listing shows it first.
    /// </summary>
    public static string Merge(string? content, JsonObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sessionId = SessionIdOf(entry)
            ?? throw new InvalidDataException("The Continue index entry carries no session id.");

        var entries = Parse(content);
        var index = entries.FindIndex(candidate =>
            StringComparer.OrdinalIgnoreCase.Equals(SessionIdOf(candidate), sessionId));
        if (index < 0) entries.Add((JsonObject)entry.DeepClone());
        else entries[index] = (JsonObject)entry.DeepClone();

        return Serialize(entries);
    }

    /// <summary>Removes one session's entry and returns the text to write.</summary>
    public static string Remove(string? content, string sessionId)
    {
        var entries = Parse(content);
        entries.RemoveAll(entry => StringComparer.OrdinalIgnoreCase.Equals(SessionIdOf(entry), sessionId));
        return Serialize(entries);
    }

    public static string Serialize(IEnumerable<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            // A carried-through non-object element goes back exactly as it arrived.
            if (entry.Count == 1 && entry.TryGetPropertyValue("__unmodelled", out var original) && original is not null)
                array.Add(original.DeepClone());
            else array.Add(entry.DeepClone());
        }

        return array.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Builds the entry for a session the index does not list. Continue tolerates that state — the
    /// session simply does not appear — but a package without an entry could not restore one, so
    /// the session file and its write time stand in for what the index would have said.
    /// </summary>
    public static JsonObject Synthesize(
        string sessionId,
        string? title,
        string? workspaceDirectory,
        DateTimeOffset created,
        int messageCount) =>
        new()
        {
            ["sessionId"] = sessionId,
            ["title"] = string.IsNullOrWhiteSpace(title) ? sessionId : title,
            ["dateCreated"] = created.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["workspaceDirectory"] = workspaceDirectory ?? string.Empty,
            ["messageCount"] = messageCount
        };
}
