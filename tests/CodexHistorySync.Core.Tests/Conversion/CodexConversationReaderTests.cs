using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Conversion;

public sealed class CodexConversationReaderTests
{
    [Fact]
    public async Task ReadAsyncExtractsOnlyOrderedUserAndAssistantText()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("session.jsonl", """
            {"type":"session_meta","payload":{"id":"original-id","timestamp":"2026-08-09T10:00:00+03:00","cwd":"C:\\Repos\\Demo","title":"Original title"}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"question"}]}}
            {"type":"response_item","payload":{"type":"reasoning","content":"private reasoning"}}
            {"type":"response_item","payload":{"type":"function_call","name":"read_file","arguments":"{}"}}
            {"type":"response_item","payload":{"type":"function_call_output","output":"private tool output"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}}
            {"type":"response_item","payload":{"type":"message","role":"system","content":[{"type":"input_text","text":"system prompt"}]}}
            """);

        var result = await new CodexConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal(ConversationAgent.Codex, result.SourceAgent);
        Assert.Equal("original-id", result.SourceSessionId);
        Assert.Equal("Original title", result.Title);
        Assert.Equal(@"C:\Repos\Demo", result.WorkingDirectory);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(3)), result.CreatedAt);
        Assert.Equal(result.CreatedAt, result.LastModifiedAt);
        Assert.Collection(result.Turns,
            turn => Assert.Equal(new PortableTurn(ConversationRole.User, "question"), turn),
            turn => Assert.Equal(new PortableTurn(ConversationRole.Assistant, "answer"), turn));
    }

    [Fact]
    public async Task ReadAsyncUsesFirstUserPreviewWhenTitleIsAbsent()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var userText = new string('u', 100);
        var path = await fixture.WriteFileAsync("session.jsonl",
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"preview-id\",\"timestamp\":\"2026-08-09T10:00:00Z\"}}\n" +
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" + userText + "\"}]}}\n");

        var result = await new CodexConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal(new string('u', 80), result.Title);
    }

    [Fact]
    public async Task ReadAsyncSkipsTechnicalUserWrappersAndKeepsTheVisibleQuestion()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("session.jsonl", """
            {"type":"session_meta","payload":{"id":"test-id","cwd":"C:\\Repos\\Demo","title":"тест"}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\n  <cwd>C:\\Repos\\Demo</cwd>\n</environment_context>"}]}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"\n# Files mentioned by the user:\n\n## TODO.md: C:\\\\Repos\\\\Demo\\\\TODO.md\n"}]}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"тест\n"}]}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ответ"}]}}
            """);

        var result = await new CodexConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal("тест", result.Title);
        Assert.Collection(result.Turns,
            turn => Assert.Equal(new PortableTurn(ConversationRole.User, "тест\n"), turn),
            turn => Assert.Equal(new PortableTurn(ConversationRole.Assistant, "ответ"), turn));
    }

    [Fact]
    public async Task ReadAsyncAcceptsRepeatedSessionMetaWhenTheIdMatches()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("session.jsonl", """
            {"type":"session_meta","payload":{"id":"same-id","timestamp":"2026-08-09T10:00:00Z","cwd":"C:\\Repos\\Demo"}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"question"}]}}
            {"type":"session_meta","payload":{"id":"same-id","timestamp":"2026-08-09T10:05:00Z","cwd":"C:\\Repos\\Demo"}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}}
            """);

        var result = await new CodexConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal("same-id", result.SourceSessionId);
        Assert.Collection(result.Turns,
            turn => Assert.Equal(new PortableTurn(ConversationRole.User, "question"), turn),
            turn => Assert.Equal(new PortableTurn(ConversationRole.Assistant, "answer"), turn));
    }

    [Fact]
    public async Task ReadAsyncExcludesReasoningAndToolBlocksNestedInMessages()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("session.jsonl", """
            {"type":"session_meta","payload":{"id":"safe-id"}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"question"},{"type":"reasoning","text":"private reasoning"},{"type":"tool_call","text":"private tool call"}]}}
            {"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"},{"type":"tool_result","text":"private tool result"}]}}
            """);

        var result = await new CodexConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Collection(result.Turns,
            turn => Assert.Equal(new PortableTurn(ConversationRole.User, "question"), turn),
            turn => Assert.Equal(new PortableTurn(ConversationRole.Assistant, "answer"), turn));
    }

    [Theory]
    [InlineData("{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"question\"}]}}\n", "missing session metadata")]
    [InlineData("{\"type\":\"session_meta\",\"payload\":{\"id\":\"first\"}}\n{\"type\":\"session_meta\",\"payload\":{\"id\":\"second\"}}\n", "conflicting session IDs")]
    [InlineData("{\"type\":\"session_meta\",\"payload\":{\"id\":\"safe-id\"}}\n{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\" \"}]}}\n", "empty readable conversation")]
    public async Task ReadAsyncRejectsInvalidConversationBoundaries(string content, string _)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("session.jsonl", content);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CodexConversationReader().ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsyncRejectsInvalidUtf8AndJson()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var invalidUtf8 = Path.Combine(fixture.Root, "invalid-utf8.jsonl");
        await File.WriteAllBytesAsync(invalidUtf8, [0xc3, 0x28]);
        var invalidJson = await fixture.WriteFileAsync("invalid-json.jsonl", "{not-json}\n");

        await Assert.ThrowsAsync<InvalidDataException>(() => new CodexConversationReader().ReadAsync(invalidUtf8, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new CodexConversationReader().ReadAsync(invalidJson, CancellationToken.None));
    }

    [Theory]
    [InlineData("unsafe:id")]
    [InlineData("unsafe*id")]
    [InlineData("unsafe\u001fid")]
    public async Task ReadAsyncRejectsReservedAndControlCharacterSessionIds(string sessionId)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("session.jsonl",
            "{\"type\":\"session_meta\",\"payload\":{\"id\":" + JsonSerializer.Serialize(sessionId) + "}}\n" +
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"question\"}]}}\n");

        await Assert.ThrowsAsync<InvalidDataException>(() => new CodexConversationReader().ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsyncRejectsPathsThatAreNotJsonlFiles()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var path = await fixture.WriteFileAsync("session.json", "{}");

        await Assert.ThrowsAsync<ArgumentException>(() => new CodexConversationReader().ReadAsync(path, CancellationToken.None));
    }

    private sealed class ConversationFixture : IAsyncDisposable
    {
        private ConversationFixture(string root) => Root = root;

        public string Root { get; }

        public static Task<ConversationFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"codex-conversation-reader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return Task.FromResult(new ConversationFixture(root));
        }

        public async Task<string> WriteFileAsync(string name, string content)
        {
            var path = Path.Combine(Root, name);
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
            return path;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
