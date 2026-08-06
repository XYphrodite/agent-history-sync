using System.Text;
using System.Text.Json;

namespace CodexHistorySync.Core.Codex;

/// <summary>
/// Deterministic reduction of Codex rollout JSONL before hash/sync.
/// Drops bulk runtime snapshots that are not required to rediscover chats, so
/// long agent sessions stay under GitHub's per-blob size limit.
/// </summary>
public static class SessionJsonlNormalizer
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> DroppedRecordTypes = new(StringComparer.Ordinal)
    {
        // Compaction windows often hold multi-megabyte replacement_history snapshots.
        "compacted",
        // Ephemeral runtime state; not needed for session rediscovery after import.
        "world_state",
        "inter_agent_communication",
        "inter_agent_communication_metadata",
        "turn_context"
    };

    public static byte[] Normalize(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0) return source;

        using var input = new MemoryStream(source, writable: false);
        using var reader = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: false);
        using var output = new MemoryStream(Math.Min(source.Length, 1_048_576));
        var wrote = false;
        while (reader.ReadLine() is { } line)
        {
            if (ShouldDrop(line)) continue;
            var encoded = Utf8.GetBytes(line);
            output.Write(encoded, 0, encoded.Length);
            output.WriteByte((byte)'\n');
            wrote = true;
        }

        return wrote ? output.ToArray() : Array.Empty<byte>();
    }

    private static bool ShouldDrop(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                return false;
            var name = type.GetString();
            return name is not null && DroppedRecordTypes.Contains(name);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
