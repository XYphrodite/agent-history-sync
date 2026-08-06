using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexHistorySync.Core.Codex;

/// <summary>
/// Deterministic reduction of Codex rollout JSONL before hash/sync.
/// Keeps enough structure for session rediscovery (meta + user/assistant text)
/// while dropping bulk agent runtime payloads that dominate disk size.
/// Local Codex files on disk are never modified.
/// </summary>
public static class SessionJsonlNormalizer
{
    // Tool outputs and patches are the bulk of agent sessions; keep only short stubs.
    public const int MaximumRetainedStringChars = 2_048;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false
    };

    private static readonly HashSet<string> DroppedRecordTypes = new(StringComparer.Ordinal)
    {
        "compacted",
        "world_state",
        "inter_agent_communication",
        "inter_agent_communication_metadata",
        "turn_context",
        // High-volume telemetry; not required to list/reopen chats after import.
        "event_msg"
    };

    /// <summary>response_item.payload.type values that are almost pure bulk tool I/O.</summary>
    private static readonly HashSet<string> DroppedPayloadTypes = new(StringComparer.Ordinal)
    {
        "function_call_output",
        "custom_tool_call_output",
        "patch_apply_end",
        "patch_apply_begin",
        "reasoning",
        "token_count",
        "web_search_call",
        "web_search_call_output",
        "file_change",
        "mcp_tool_call",
        "mcp_tool_call_output"
    };

    private static readonly HashSet<string> ImageBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "input_image",
        "output_image",
        "image",
        "image_url"
    };

    private static readonly HashSet<string> IdentityKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "type",
        "role",
        "name",
        "call_id",
        "tool_call_id"
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

        string? recordType = null;
        if (root.TryGetPropertyValue("type", out var typeNode) &&
            typeNode is JsonValue typeValue &&
            typeValue.TryGetValue(out recordType) &&
            recordType is not null &&
            DroppedRecordTypes.Contains(recordType))
        {
            return false;
        }

        if (string.Equals(recordType, "response_item", StringComparison.Ordinal) &&
            root["payload"] is JsonObject payload &&
            payload.TryGetPropertyValue("type", out var payloadTypeNode) &&
            payloadTypeNode is JsonValue payloadTypeValue &&
            payloadTypeValue.TryGetValue<string>(out var payloadType) &&
            payloadType is not null &&
            DroppedPayloadTypes.Contains(payloadType))
        {
            return false;
        }

        ScrubNode(root);
        normalized = root.ToJsonString(CompactJson);
        return true;
    }

    private static void ScrubNode(JsonNode? node)
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

                foreach (var property in obj.ToList())
                {
                    var key = property.Key;
                    if (property.Value is JsonValue scalar &&
                        scalar.TryGetValue<string>(out var text) &&
                        text is not null)
                    {
                        if (IsImageData(text))
                        {
                            obj[key] = "[image omitted]";
                            continue;
                        }

                        if (!IdentityKeys.Contains(key) && text.Length > MaximumRetainedStringChars)
                        {
                            obj[key] = Truncate(text);
                            continue;
                        }
                    }

                    if (property.Value is JsonObject nested &&
                        (key.Equals("image_url", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("image", StringComparison.OrdinalIgnoreCase)))
                    {
                        nested.Clear();
                        nested["url"] = "[image omitted]";
                        continue;
                    }

                    ScrubNode(property.Value);
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
                    ScrubNode(child);
                }
                break;
        }
    }

    private static string Truncate(string text)
    {
        var keep = Math.Min(MaximumRetainedStringChars, text.Length);
        return text[..keep] + $"[...truncated {text.Length - keep} chars]";
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
        text.StartsWith("iVBORw0KGgo", StringComparison.Ordinal) ||
        text.StartsWith("/9j/", StringComparison.Ordinal);
}
