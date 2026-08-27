using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class SessionContentReaderTests
{
    [Theory]
    [InlineData(ManagedAgent.Codex, "codex")]
    [InlineData(ManagedAgent.Grok, "grok")]
    [InlineData(ManagedAgent.Claude, "claude")]
    public async Task ReadAsync_UsesTheReaderThatOwnsTheAgent(ManagedAgent agent, string expected)
    {
        var reader = new SessionContentReader(
            new StubReader("codex"), new StubReader("grok"), new StubReader("claude"));

        var conversation = await reader.ReadAsync(Session(agent), CancellationToken.None);

        Assert.Equal(expected, conversation.Title);
    }

    [Fact]
    public async Task ReadAsync_PassesTheNativePathThrough()
    {
        var codex = new StubReader("codex");
        var reader = new SessionContentReader(codex, new StubReader("grok"), new StubReader("claude"));

        await reader.ReadAsync(Session(ManagedAgent.Codex) with { NativePath = @"C:\sessions\one.jsonl" },
            CancellationToken.None);

        Assert.Equal(@"C:\sessions\one.jsonl", codex.LastPath);
    }

    [Fact]
    public async Task ReadAsync_RefusesAnUnreadableSessionWithoutTouchingTheDisk()
    {
        // The catalog already knows this row is broken; reporting it here keeps the reason the
        // same one the copy path gives instead of a format-specific parse error.
        var codex = new StubReader("codex");
        var reader = new SessionContentReader(codex, new StubReader("grok"), new StubReader("claude"));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAsync(Session(ManagedAgent.Codex) with { CanRead = false }, CancellationToken.None));

        Assert.Equal("The session is not readable.", failure.Message);
        Assert.Null(codex.LastPath);
    }

    [Fact]
    public async Task ReadAsync_RefusesASessionWithoutANativePath()
    {
        var reader = new SessionContentReader(new StubReader("codex"), new StubReader("grok"), new StubReader("claude"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAsync(Session(ManagedAgent.Codex) with { NativePath = "  " }, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_RefusesAnAgentItDoesNotKnow()
    {
        var reader = new SessionContentReader(new StubReader("codex"), new StubReader("grok"), new StubReader("claude"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAsync(Session((ManagedAgent)42), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_PropagatesCancellation()
    {
        var reader = new SessionContentReader(new StubReader("codex"), new StubReader("grok"), new StubReader("claude"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(Session(ManagedAgent.Codex), cancelled.Token));
    }

    [Fact]
    public async Task DefaultConstructor_WiresEachAgentToItsOwnRealReader()
    {
        // The stubs above prove the dispatch; this proves the default wiring reaches a reader
        // that can actually parse that agent's format, which a swapped pair would not.
        var root = Path.Combine(Path.GetTempPath(), "chs-content-" + Guid.NewGuid().ToString("N"));
        var sessionId = "90000000-0000-0000-0000-000000000009";
        var directory = Path.Combine(root, "projects", "c--Repos-Demo");
        Directory.CreateDirectory(directory);
        var transcript = Path.Combine(directory, sessionId + ".jsonl");
        await File.WriteAllTextAsync(transcript,
            "{\"type\":\"user\",\"isSidechain\":false,\"sessionId\":\"" + sessionId + "\"," +
            "\"cwd\":\"C:\\\\Repos\\\\Demo\",\"timestamp\":\"2026-08-25T10:00:00Z\"," +
            "\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"real question\"}]}}\n" +
            "{\"type\":\"ai-title\",\"aiTitle\":\"Real Claude title\",\"sessionId\":\"" + sessionId + "\"}\n",
            new System.Text.UTF8Encoding(false));

        try
        {
            var session = new ManagedSession(
                ManagedAgent.Claude, sessionId, transcript, "ignored", DateTimeOffset.UnixEpoch, false, true);

            var conversation = await new SessionContentReader().ReadAsync(session, CancellationToken.None);

            Assert.Equal(ConversationAgent.Claude, conversation.SourceAgent);
            Assert.Equal("Real Claude title", conversation.Title);
            Assert.Equal([new PortableTurn(ConversationRole.User, "real question")], conversation.Turns);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ManagedSession Session(ManagedAgent agent) =>
        new(agent, "session-id", @"C:\native\path", "title", DateTimeOffset.UnixEpoch, IsActive: false, CanRead: true);

    private sealed class StubReader(string title) : IConversationReader
    {
        public string? LastPath { get; private set; }

        public Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
        {
            LastPath = nativePath;
            return Task.FromResult(new PortableConversation(
                ConversationAgent.Codex,
                "source-id",
                title,
                @"C:\Repos\Demo",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                [new PortableTurn(ConversationRole.User, "question")]));
        }
    }
}
