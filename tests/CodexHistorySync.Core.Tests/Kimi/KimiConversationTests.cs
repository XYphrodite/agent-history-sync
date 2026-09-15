using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Tests.Kimi;

public sealed class KimiConversationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-kimi-conv-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReaderExtractsPortableConversationFromASessionDirectory()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession(
            title: "conversation title",
            turns: [("user", "first question"), ("assistant", "first answer"), ("user", "second question")]);
        var reader = new KimiConversationReader();

        var conversation = await reader.ReadAsync(sessionDirectory, CancellationToken.None);

        Assert.Equal(ConversationAgent.Kimi, conversation.SourceAgent);
        Assert.Equal(KimiHomeFixture.MainSessionId, conversation.SourceSessionId);
        Assert.Equal("conversation title", conversation.Title);
        Assert.Equal(fixture.WorkDir, conversation.WorkingDirectory);
        Assert.Equal(
            [new PortableTurn(ConversationRole.User, "first question"),
             new PortableTurn(ConversationRole.Assistant, "first answer"),
             new PortableTurn(ConversationRole.User, "second question")],
            conversation.Turns);
    }

    [Fact]
    public async Task ReaderRefusesADirectoryWithoutSynchronizableContent()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession();
        File.Delete(Path.Combine(sessionDirectory, "agents", "main", KimiSessionPackage.WireFileName));
        var reader = new KimiConversationReader();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadAsync(sessionDirectory, CancellationToken.None));
    }

    [Fact]
    public async Task WriterCreatesAValidSessionRegisteredInTheIndex()
    {
        var paths = CreatePaths();
        var writer = new KimiConversationWriter(paths);
        var conversation = new PortableConversation(
            ConversationAgent.Grok,
            "source-session-id",
            "copied title",
            Path.Combine(root, "work", "target"),
            new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 12, 30, 0, TimeSpan.Zero),
            [new PortableTurn(ConversationRole.User, "copied question"),
             new PortableTurn(ConversationRole.Assistant, "copied answer")]);

        var result = await writer.WriteAsync(conversation, CancellationToken.None);

        Assert.True(Directory.Exists(result.NativePath));
        var package = KimiSessionPackage.BuildFromDirectory(
            result.NativePath, KimiPaths.ComputeWorkDirKey(conversation.WorkingDirectory!));
        var info = KimiSessionPackage.Parse(package);
        Assert.Equal(result.SessionId, info.SessionId);
        var entries = KimiSessionIndex.Parse(File.ReadAllText(paths.IndexFilePath));
        var entry = Assert.Single(entries);
        Assert.Equal(KimiPaths.SessionIdPrefix + result.SessionId, KimiSessionIndex.SessionIdOf(entry));

        var reader = new KimiConversationReader();
        var roundTrip = await reader.ReadAsync(result.NativePath, CancellationToken.None);
        Assert.Equal(conversation.Turns, roundTrip.Turns);
        Assert.Equal("copied title", roundTrip.Title);
    }

    [Fact]
    public async Task WriterPreservesExistingIndexLines()
    {
        var paths = CreatePaths();
        File.WriteAllText(paths.IndexFilePath,
            "{\"sessionId\":\"session_20000000-0000-0000-0000-000000000002\",\"sessionDir\":\"C:/keep\",\"workDir\":\"C:/keep\"}");
        var writer = new KimiConversationWriter(paths);
        var conversation = new PortableConversation(
            ConversationAgent.Codex,
            "source",
            "new",
            Path.Combine(root, "work", "target"),
            new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 12, 1, 0, TimeSpan.Zero),
            [new PortableTurn(ConversationRole.User, "hello")]);

        await writer.WriteAsync(conversation, CancellationToken.None);

        var entries = KimiSessionIndex.Parse(File.ReadAllText(paths.IndexFilePath));
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry =>
            string.Equals(KimiSessionIndex.SessionIdOf(entry), "session_20000000-0000-0000-0000-000000000002",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriterRequiresAWorkingDirectory()
    {
        var writer = new KimiConversationWriter(CreatePaths());
        var conversation = new PortableConversation(
            ConversationAgent.Codex, "source", "title", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new PortableTurn(ConversationRole.User, "hello")]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.WriteAsync(conversation, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private KimiPaths CreatePaths()
    {
        var home = Path.Combine(root, ".kimi-code");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        return new KimiPaths(home, sessions);
    }
}
