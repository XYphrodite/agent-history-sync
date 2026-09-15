using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Kimi;

/// <summary>
/// Reads the portable conversation out of one <c>wire.jsonl</c> file. The text travels in three
/// event shapes, collected in file order: <c>agent.message.appended</c> and
/// <c>context.append_message</c> carry OpenAI-style chat messages (content blocks of <c>text</c>
/// and <c>think</c> plus tool calls), and <c>context.append_loop_event</c> carries assistant prose
/// as <c>content.part</c> text blocks. A live session writes assistant text only into loop events,
/// so reading <c>agent.message.appended</c> alone loses every in-flight reply. Only user and
/// assistant text turns are portable, per the cross-agent copy rules of this repository; thinking
/// blocks and tool traffic stay behind, the way they do for every other agent. Identical text is
/// collected once: the streams overlap for completed messages.
/// </summary>
public static class KimiWireConversation
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static IReadOnlyList<PortableTurn> ReadTurns(string wirePath)
    {
        var turns = new List<PortableTurn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(wirePath, Utf8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (node is not JsonObject record) continue;

            var turn = ReadString(record, "type") switch
            {
                "agent.message.appended" => ReadAppendedTurn(record),
                "context.append_message" => ReadContextTurn(record),
                "context.append_loop_event" => ReadLoopTextTurn(record),
                _ => null
            };
            if (turn is null) continue;
            if (!seen.Add(turn.Text.Trim())) continue;
            turns.Add(turn);
        }

        return turns;
    }

    public static int CountTextTurns(string wirePath) => ReadTurns(wirePath).Count;

    private static PortableTurn? ReadAppendedTurn(JsonObject record)
    {
        if (record.TryGetPropertyValue("message", out var envelopeValue) && envelopeValue is JsonObject envelope &&
            envelope.TryGetPropertyValue("message", out var messageValue) && messageValue is JsonObject message)
            return ToPortableTurn(message);
        return null;
    }

    private static PortableTurn? ReadContextTurn(JsonObject record) =>
        record.TryGetPropertyValue("message", out var messageValue) && messageValue is JsonObject message
            ? ToPortableTurn(message)
            : null;

    private static PortableTurn? ReadLoopTextTurn(JsonObject record)
    {
        if (record.TryGetPropertyValue("event", out var eventValue) && eventValue is JsonObject loopEvent &&
            loopEvent.TryGetPropertyValue("part", out var partValue) && partValue is JsonObject part &&
            StringComparer.Ordinal.Equals(ReadString(part, "type"), "text"))
        {
            var text = ReadString(part, "text");
            if (!string.IsNullOrWhiteSpace(text)) return new PortableTurn(ConversationRole.Assistant, text);
        }
        return null;
    }

    private static PortableTurn? ToPortableTurn(JsonObject message)
    {
        var role = ReadString(message, "role");
        var text = ExtractText(message);
        if (text is null) return null;
        if (StringComparer.Ordinal.Equals(role, "user")) return new PortableTurn(ConversationRole.User, text);
        if (StringComparer.Ordinal.Equals(role, "assistant")) return new PortableTurn(ConversationRole.Assistant, text);
        return null;
    }

    private static string? ExtractText(JsonObject message)
    {
        if (!message.TryGetPropertyValue("content", out var content)) return null;

        if (content is JsonValue scalar && scalar.TryGetValue<string>(out var plain))
            return string.IsNullOrWhiteSpace(plain) ? null : plain;

        if (content is not JsonArray blocks) return null;
        var parts = new List<string>();
        foreach (var block in blocks)
        {
            if (block is not JsonObject item) continue;
            if (!StringComparer.Ordinal.Equals(ReadString(item, "type"), "text")) continue;
            var text = ReadString(item, "text");
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }

        if (parts.Count == 0) return null;
        return string.Join("\n\n", parts);
    }

    private static string? ReadString(JsonObject record, string property) =>
        record.TryGetPropertyValue(property, out var value) && value is JsonValue text &&
        text.TryGetValue<string>(out var result)
            ? result
            : null;
}
