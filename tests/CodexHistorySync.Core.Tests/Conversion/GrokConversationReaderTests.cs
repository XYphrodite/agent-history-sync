using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Conversion;

public sealed class GrokConversationReaderTests
{
    private const string SessionId = "019fd29d-8f07-7eb3-8fcd-cadaf33d2de6";

    [Fact]
    public async Task ReadAsyncExtractsOrderedTextAndMetadataFromNativePackage()
    {
        await using var fixture = await GrokFixture.CreateAsync();
        var expectedCreated = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(3));
        var expectedModified = new DateTimeOffset(2026, 8, 9, 11, 0, 0, TimeSpan.FromHours(3));
        var directory = await fixture.WritePackageAsync(SessionId,
            """
            {"type":"system","content":"system prompt"}
            {"type":"user","content":"question"}
            {"type":"reasoning","content":"private reasoning"}
            {"type":"tool_call","content":"private tool call"}
            {"type":"tool_result","content":"private tool result"}
            {"type":"assistant","content":"answer"}
            """,
            "{\"info\":{\"id\":\"" + SessionId + "\",\"cwd\":\"C:\\\\Repos\\\\Demo\",\"title\":\"Grok title\",\"created_at\":\"2026-08-09T10:00:00+03:00\",\"updated_at\":\"2026-08-09T11:00:00+03:00\"}}");

        var result = await new GrokConversationReader().ReadAsync(directory, CancellationToken.None);

        Assert.Equal(ConversationAgent.Grok, result.SourceAgent);
        Assert.Equal(SessionId, result.SourceSessionId);
        Assert.Equal("Grok title", result.Title);
        Assert.Equal(Path.GetFullPath(@"C:\Repos\Demo"), result.WorkingDirectory);
        Assert.Equal(expectedCreated, result.CreatedAt);
        Assert.Equal(expectedModified, result.LastModifiedAt);
        Assert.Collection(result.Turns,
            turn => Assert.Equal(new PortableTurn(ConversationRole.User, "question"), turn),
            turn => Assert.Equal(new PortableTurn(ConversationRole.Assistant, "answer"), turn));
    }

    [Fact]
    public async Task ReadAsyncUsesFirstUserPreviewWhenTitleIsAbsent()
    {
        await using var fixture = await GrokFixture.CreateAsync();
        var directory = await fixture.WritePackageAsync(SessionId,
            "{\"type\":\"user\",\"content\":\"question\"}\n",
            "{\"info\":{\"id\":\"" + SessionId + "\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}");

        var result = await new GrokConversationReader().ReadAsync(directory, CancellationToken.None);

        Assert.Equal("question", result.Title);
    }

    [Fact]
    public async Task ReadAsyncExtractsNativeTextBlocksUsedByGrokCli()
    {
        await using var fixture = await GrokFixture.CreateAsync();
        var directory = await fixture.WritePackageAsync(SessionId,
            """
            {"type":"user","content":[{"type":"text","text":"question"}]}
            {"type":"assistant","content":"answer"}
            """,
            "{\"info\":{\"id\":\"" + SessionId + "\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}");

        var result = await new GrokConversationReader().ReadAsync(directory, CancellationToken.None);

        Assert.Collection(result.Turns,
            turn => Assert.Equal(new PortableTurn(ConversationRole.User, "question"), turn),
            turn => Assert.Equal(new PortableTurn(ConversationRole.Assistant, "answer"), turn));
    }

    [Fact]
    public async Task ReadAsyncExcludesReasoningAndToolBlocksNestedInMessages()
    {
        await using var fixture = await GrokFixture.CreateAsync();
        var directory = await fixture.WritePackageAsync(SessionId,
            """
            {"type":"user","content":[{"type":"input_text","text":"question"},{"type":"reasoning","text":"private reasoning"},{"type":"tool_call","text":"private tool call"}]}
            {"type":"assistant","content":[{"type":"output_text","text":"answer"},{"type":"tool_result","text":"private tool result"}]}
            """,
            "{\"info\":{\"id\":\"" + SessionId + "\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}");

        var result = await new GrokConversationReader().ReadAsync(directory, CancellationToken.None);

        Assert.Collection(result.Turns,
            turn => Assert.Equal(new PortableTurn(ConversationRole.User, "question"), turn),
            turn => Assert.Equal(new PortableTurn(ConversationRole.Assistant, "answer"), turn));
    }

    [Fact]
    public async Task ReadAsyncPreservesNativeTurnTextLongerThanTheSyncNormalizerLimit()
    {
        // Routing conversion through the sync package normalizer truncates otherwise valid native text.
        await using var fixture = await GrokFixture.CreateAsync();
        var expectedText = new string('x', 4_321);
        var chat = JsonSerializer.Serialize(new { type = "user", content = expectedText }) + "\n";
        var directory = await fixture.WritePackageAsync(SessionId, chat,
            "{\"info\":{\"id\":\"" + SessionId + "\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}");

        var result = await new GrokConversationReader().ReadAsync(directory, CancellationToken.None);

        var turn = Assert.Single(result.Turns);
        Assert.Equal(ConversationRole.User, turn.Role);
        Assert.Equal(expectedText, turn.Text);
    }

    [Fact]
    public async Task ReadAsyncRejectsMalformedNativeRecordBetweenValidTurns()
    {
        // Letting the sync normalizer discard one malformed record would return a misleading partial conversation.
        await using var fixture = await GrokFixture.CreateAsync();
        var directory = await fixture.WritePackageAsync(SessionId,
            """
            {"type":"user","content":"question"}
            {not-json}
            {"type":"assistant","content":"answer"}
            """,
            "{\"info\":{\"id\":\"" + SessionId + "\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GrokConversationReader().ReadAsync(directory, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsyncRejectsMismatchedDirectoryAndMetadataIds()
    {
        await using var fixture = await GrokFixture.CreateAsync();
        var directory = await fixture.WritePackageAsync(SessionId,
            "{\"type\":\"user\",\"content\":\"question\"}\n",
            "{\"info\":{\"id\":\"019fd29d-8f07-7eb3-8fcd-cadaf33d2de7\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}");

        await Assert.ThrowsAsync<InvalidDataException>(() => new GrokConversationReader().ReadAsync(directory, CancellationToken.None));
    }

    [Theory]
    [InlineData("not-a-uuid", "{\"type\":\"user\",\"content\":\"question\"}\n", "{\"info\":{\"id\":\"not-a-uuid\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}")]
    [InlineData(SessionId, "{not-json}\n", "{\"info\":{\"id\":\"019fd29d-8f07-7eb3-8fcd-cadaf33d2de6\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}")]
    [InlineData(SessionId, "{\"type\":\"user\",\"content\":\" \"}\n", "{\"info\":{\"id\":\"019fd29d-8f07-7eb3-8fcd-cadaf33d2de6\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}}")]
    public async Task ReadAsyncRejectsUnsafeMalformedOrEmptyPackages(string sessionId, string chat, string summary)
    {
        await using var fixture = await GrokFixture.CreateAsync();
        var directory = await fixture.WritePackageAsync(sessionId, chat, summary);

        await Assert.ThrowsAsync<InvalidDataException>(() => new GrokConversationReader().ReadAsync(directory, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsyncRejectsMissingRequiredPackageFiles()
    {
        await using var fixture = await GrokFixture.CreateAsync();
        var directory = Path.Combine(fixture.Root, SessionId);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "chat_history.jsonl"), "{\"type\":\"user\",\"content\":\"question\"}\n", new UTF8Encoding(false));

        await Assert.ThrowsAsync<InvalidDataException>(() => new GrokConversationReader().ReadAsync(directory, CancellationToken.None));
    }

    private sealed class GrokFixture : IAsyncDisposable
    {
        private GrokFixture(string root) => Root = root;

        public string Root { get; }

        public static Task<GrokFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"grok-conversation-reader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return Task.FromResult(new GrokFixture(root));
        }

        public async Task<string> WritePackageAsync(string sessionId, string chat, string summary)
        {
            var directory = Path.Combine(Root, sessionId);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "chat_history.jsonl"), chat, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), summary, new UTF8Encoding(false));
            return directory;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
