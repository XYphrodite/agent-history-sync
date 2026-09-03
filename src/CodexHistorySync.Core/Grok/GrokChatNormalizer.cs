using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexHistorySync.Core.Grok;

/// <summary>
/// Shrinks Grok CLI chat_history.jsonl for sync: drop huge system prompts and truncate long content.
/// </summary>
public static class GrokChatNormalizer
{
    public const int MaximumContentChars = 4_096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private static readonly HashSet<string> DroppedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "tool",
        "function",
        "tool_result",
        "tool_call"
    };

    public static byte[] Normalize(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0) return source;

        using var input = new MemoryStream(source, writable: false);
        using var reader = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: false);
        using var output = new MemoryStream(Math.Min(source.Length, 256 * 1024));
        var wrote = false;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException)
            {
                continue;
            }

            if (node is not JsonObject obj) continue;
            if (obj.TryGetPropertyValue("type", out var typeNode) &&
                typeNode is JsonValue typeValue &&
                typeValue.TryGetValue<string>(out var type) &&
                type is not null &&
                DroppedTypes.Contains(type))
            {
                continue;
            }

            if (obj.TryGetPropertyValue("content", out var contentNode) &&
                contentNode is JsonValue contentValue &&
                contentValue.TryGetValue<string>(out var content) &&
                content is not null &&
                content.Length > MaximumContentChars)
            {
                obj["content"] = Truncate(content);
            }

            var encoded = Utf8.GetBytes(obj.ToJsonString(CompactJson));
            output.Write(encoded, 0, encoded.Length);
            output.WriteByte((byte)'\n');
            wrote = true;
        }

        return wrote ? output.ToArray() : Array.Empty<byte>();
    }

    /// <summary>
    /// Truncation has to be a fixed point, for the same reason it does in
    /// <see cref="CodexHistorySync.Core.Codex.SessionJsonlNormalizer"/>. Here the round trip is
    /// tighter still: <c>GrokSessionPackage.Materialize</c> writes the normalized chat to disk and
    /// the import reads it straight back through <c>BuildFromDirectory</c>, which normalizes it
    /// again. With the marker appended past the limit that second pass cut the marker and rewrote
    /// the count, so the read-back could never match the hash the object was authenticated
    /// against, and the session was reported as a conflict instead of arriving at all.
    /// </summary>
    private static string Truncate(string content)
    {
        var marker = $"[...truncated {content.Length - MaximumContentChars} chars]";
        var keep = Math.Max(0, MaximumContentChars - marker.Length);
        return content[..keep] + marker;
    }
}
