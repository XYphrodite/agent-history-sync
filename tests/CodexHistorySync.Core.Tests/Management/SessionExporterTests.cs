using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class SessionExporterTests
{
    [Fact]
    public async Task ExportAsync_WritesTheHeadingMetadataAndEveryTurn()
    {
        await using var fixture = new ExportFixture();

        var path = await new SessionExporter(fixture.Root).ExportAsync(
            Session(ManagedAgent.Claude, "50000000-0000-0000-0000-000000000005"),
            Conversation(),
            CancellationToken.None);

        var text = await File.ReadAllTextAsync(path);
        Assert.Equal(
            Path.Combine(fixture.Root, "claude-50000000-0000-0000-0000-000000000005.md"),
            path);
        Assert.StartsWith("# Session title", text, StringComparison.Ordinal);
        Assert.Contains("- Agent: Claude", text, StringComparison.Ordinal);
        Assert.Contains("- Session: 50000000-0000-0000-0000-000000000005", text, StringComparison.Ordinal);
        Assert.Contains(@"- Working directory: C:\Repos\Demo", text, StringComparison.Ordinal);
        Assert.Contains("## User\r\n\r\nquestion", text.ReplaceLineEndings("\r\n"), StringComparison.Ordinal);
        Assert.Contains("## Assistant\r\n\r\nanswer", text.ReplaceLineEndings("\r\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_CreatesTheExportDirectory()
    {
        await using var fixture = new ExportFixture(createRoot: false);
        Assert.False(Directory.Exists(fixture.Root));

        var path = await new SessionExporter(fixture.Root).ExportAsync(
            Session(ManagedAgent.Codex, "codex-one"), Conversation(), CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ExportAsync_OverwritesAPreviousExportAndLeavesNoTemporaryFile()
    {
        await using var fixture = new ExportFixture();
        var exporter = new SessionExporter(fixture.Root);
        var session = Session(ManagedAgent.Grok, "grok-one");
        await exporter.ExportAsync(session, Conversation(), CancellationToken.None);

        var path = await exporter.ExportAsync(
            session, Conversation("second question", "second answer"), CancellationToken.None);

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("second question", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nquestion", text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData(@"..\..\escape")]
    [InlineData("with/slash")]
    [InlineData("..")]
    [InlineData("")]
    public async Task ExportAsync_RefusesASessionIdThatIsNotASafeFileName(string sessionId)
    {
        // A row's id steers the write; anything that could leave the export directory is refused.
        await using var fixture = new ExportFixture();

        await Assert.ThrowsAsync<ArgumentException>(
            () => new SessionExporter(fixture.Root).ExportAsync(
                Session(ManagedAgent.Codex, sessionId), Conversation(), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task ExportAsync_AcceptsAConversationWithoutAWorkingDirectory()
    {
        await using var fixture = new ExportFixture();

        var path = await new SessionExporter(fixture.Root).ExportAsync(
            Session(ManagedAgent.Codex, "codex-one"),
            Conversation() with { WorkingDirectory = null },
            CancellationToken.None);

        Assert.DoesNotContain("Working directory", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultRoot_LivesUnderTheUsersDocuments()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "agent-sync"),
            SessionExporter.DefaultRoot());
    }

    private static ManagedSession Session(ManagedAgent agent, string sessionId) =>
        new(agent, sessionId, @"C:\native\path", "row title", DateTimeOffset.UnixEpoch, IsActive: false, CanRead: true);

    private static PortableConversation Conversation(string user = "question", string assistant = "answer") =>
        new(ConversationAgent.Claude, "source-id", "Session title", @"C:\Repos\Demo",
            new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero),
            [
                new PortableTurn(ConversationRole.User, user),
                new PortableTurn(ConversationRole.Assistant, assistant)
            ]);

    private sealed class ExportFixture : IAsyncDisposable
    {
        public ExportFixture(bool createRoot = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "chs-export-" + Guid.NewGuid().ToString("N"));
            if (createRoot) Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
