using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Codex;

public sealed class CodexConversationVisibilityTests
{
    [Theory]
    [InlineData("correct", true)]
    [InlineData("missing", false)]
    [InlineData("changed", false)]
    [InlineData("reordered", false)]
    [InlineData("duplicated", false)]
    public void RequiresExactUserAndAssistantMessages(string scenario, bool compatible)
    {
        var conversation = new PortableConversation(ConversationAgent.Grok, "synthetic", "Title", "C:/test",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new PortableTurn(ConversationRole.User, "Вопрос"), new PortableTurn(ConversationRole.Assistant, "Ответ\n\ntext")]);
        var items = JsonNode.Parse("""
            [{"type":"userMessage","content":[{"type":"text","text":"Вопрос"}]},
             {"type":"agentMessage","text":"Ответ\n\ntext"}]
            """)!.AsArray();
        if (scenario == "missing") items.RemoveAt(1);
        if (scenario == "changed") items[1]!["text"] = "Different answer";
        if (scenario == "reordered") { var first = items[0]; items.RemoveAt(0); items.Add(first); }
        if (scenario == "duplicated") items.Add(items[1]!.DeepClone());
        using var thread = JsonDocument.Parse(new JsonObject { ["turns"] = new JsonArray(
            new JsonObject { ["items"] = items }) }.ToJsonString());

        if (compatible) CodexConversationVisibility.EnsureMatches(conversation, thread.RootElement);
        else Assert.Throws<InvalidDataException>(() => CodexConversationVisibility.EnsureMatches(conversation, thread.RootElement));
    }
}
