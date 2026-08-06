using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexHistorySync.Core.Codex;

/// <summary>
/// Deterministic reduction of Codex rollout JSONL before hash/sync.
/// Drops bulk runtime snapshots and strips embedded images so long sessions
/// stay under GitHub's per-blob size limit without rewriting local Codex files.
/// </summary>
public static class SessionJsonlNormalizer
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false
    };

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

    private static readonly HashSet<string> ImageBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "input_image",
        "output_image",
        "image",
        "image_url"
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
            if (!TryNormalizeLine(line, out var normalized) || normalized is null) continue;
            var encoded = Utf8.GetBytes(normalized);
            output.Write(encoded, 0, encoded.Length);
            output.WriteByte((byte)'\n');
            wrote = true;
        }

        return wrote ? output.ToArray() : Array.Empty<byte>();
    }

    private static bool TryNormalizeLine(string line, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            normalized = line;
            return true;
        }

        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException)
        {
            normalized = line;
            return true;
        }

        if (node is not JsonObject root) return true;

        if (root.TryGetPropertyValue("type", out var typeNode) &&
            typeNode is JsonValue typeValue &&
            typeValue.TryGetValue<string>(out var recordType) &&
            recordType is not null &&
            DroppedRecordTypes.Contains(recordType))
        {
            return false;
        }

        ScrubImages(root);
        normalized = root.ToJsonString(CompactJson);
        return true;
    }

    private static void ScrubImages(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (IsImageBlock(obj))
                {
                    obj.Clear();
                    obj["type"] = "input_text";
                    obj["text"] = "[image omitted]";
                    return;
                }

                // Common nested shapes: { "image_url": "data:image/..." } or { "url": "data:image/..." }.
                foreach (var property in obj.ToList())
                {
                    if (property.Value is JsonValue scalar &&
                        scalar.TryGetValue<string>(out var text) &&
                        text is not null &&
                        IsImageData(text))
                    {
                        obj[property.Key] = "[image omitted]";
                        continue;
                    }

                    if (property.Value is JsonObject nested &&
                        (property.Key.Equals("image_url", StringComparison.OrdinalIgnoreCase) ||
                         property.Key.Equals("image", StringComparison.OrdinalIgnoreCase)))
                    {
                        nested.Clear();
                        nested["url"] = "[image omitted]";
                        continue;
                    }

                    ScrubImages(property.Value);
                }
                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var child = array[index];
                    if (child is JsonObject childObject && IsImageBlock(childObject))
                    {
                        array[index] = new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = "[image omitted]"
                        };
                        continue;
                    }
                    ScrubImages(child);
                }
                break;
        }
    }

    private static bool IsImageBlock(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("type", out var typeNode) || typeNode is not JsonValue typeValue)
            return false;
        if (!typeValue.TryGetValue<string>(out var type) || type is null)
            return false;
        return ImageBlockTypes.Contains(type);
    }

    private static bool IsImageData(string text) =>
        text.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("iVBORw0KGgo", StringComparison.Ordinal) || // PNG
        text.StartsWith("/9j/", StringComparison.Ordinal); // JPEG
}
