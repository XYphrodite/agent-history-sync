using System.Text.Json;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Codex;

internal static class CodexConversationVisibility
{
    // A listed thread can still contain only user bubbles. Verify the actual UI transcript,
    // including order and full text, independently of our JSONL round-trip reader.
    internal static void EnsureMatches(PortableConversation expected, JsonElement thread)
    {
        if (!thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
            throw Mismatch();
        var visible = new List<PortableTurn>();
        foreach (var turn in turns.EnumerateArray())
        {
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw Mismatch();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type)) throw Mismatch();
                if (type.GetString() == "agentMessage")
                {
                    if (!item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                        throw Mismatch();
                    visible.Add(new PortableTurn(ConversationRole.Assistant, text.GetString()!));
                }
                else if (type.GetString() == "userMessage")
                {
                    if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                        throw Mismatch();
                    var text = string.Concat(content.EnumerateArray()
                        .Where(block => block.TryGetProperty("type", out var kind) && kind.GetString() == "text")
                        .Select(block => block.GetProperty("text").GetString()));
                    visible.Add(new PortableTurn(ConversationRole.User, text));
                }
            }
        }
        if (!expected.Turns.SequenceEqual(visible)) throw Mismatch();
    }

    private static InvalidDataException Mismatch() =>
        new("Codex did not preserve all visible conversation messages.");
}
