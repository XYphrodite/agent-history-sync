using System.Text.Json;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests;

public sealed class MuseConversationFormatTests
{
    [Fact]
    public async Task ReadsRetainedAndDirectRunMessagesWithoutLeakingReasoningOrDuplicatingEvents()
    {
        var container = Path.Combine(Path.GetTempPath(), "muse-format-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(container, Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var user = Record("user", "started", "Question", null);
            var answer = Record("answer", "assistant_message_committed", null, "Answer");
            var privateRecord = Record("private", "reasoning_committed", null, "Private reasoning");
            var diagnostic = Record("diagnostic", "context_block_diagnostic", null, "Internal diagnostic");
            var frame = JsonSerializer.Serialize(new
            {
                retained_frame = "transaction", children = new[]
                {
                    new { record_json = user }, new { record_json = privateRecord },
                    new { record_json = diagnostic }, new { record_json = "{bad" }, new { record_json = answer }
                }
            });
            await File.WriteAllTextAsync(Path.Combine(directory, "session.jsonl"), frame + "\n" + answer + "\n" +
                "{\"role\":\"assistant\",\"text\":\"Legacy answer\"}\n" +
                "{\"role\":\"system\",\"text\":\"System instructions\"}\n");
            var result = await new MuseConversationReader().ReadAsync(directory, CancellationToken.None);
            Assert.Equal(["Question", "Answer", "Legacy answer"], result.Turns.Select(turn => turn.Text));
            Assert.Equal([ConversationRole.User, ConversationRole.Assistant, ConversationRole.Assistant], result.Turns.Select(turn => turn.Role));
        }
        finally { Directory.Delete(container, true); }
    }

    private static string Record(string id, string kind, string? prompt, string? text) =>
        JsonSerializer.Serialize(new
        {
            id, payload_type = "runtime.session",
            payload = new { kind = "run", @event = new { kind, prompt, text } }
        });
}
