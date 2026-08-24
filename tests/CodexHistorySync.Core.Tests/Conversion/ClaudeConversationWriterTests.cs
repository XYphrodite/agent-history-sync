using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Conversion;

public sealed class ClaudeConversationWriterTests
{
    private static readonly Guid FreshId = Guid.Parse("60000000-0000-0000-0000-000000000006");
    private static readonly DateTimeOffset CopyTime = new(2026, 8, 24, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WriteAsync_PublishesUnderTheEncodedProjectSegment()
    {
        await using var fixture = new WriterFixture();

        var result = await fixture.Writer().WriteAsync(fixture.Conversation(), CancellationToken.None);

        var expected = Path.Combine(
            fixture.Paths.Projects,
            ClaudePaths.EncodeProjectSegment(fixture.WorkingDirectory),
            FreshId + ".jsonl");
        Assert.Equal(expected, result.NativePath);
        Assert.Equal(FreshId.ToString(), result.SessionId);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsThroughTheClaudeReader()
    {
        await using var fixture = new WriterFixture();
        var source = fixture.Conversation();

        var result = await fixture.Writer().WriteAsync(source, CancellationToken.None);
        var reread = await new ClaudeConversationReader().ReadAsync(result.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Claude, reread.SourceAgent);
        Assert.Equal(FreshId.ToString(), reread.SourceSessionId);
        Assert.Equal(source.Title, reread.Title);
        Assert.Equal(fixture.WorkingDirectory, reread.WorkingDirectory);
        Assert.Equal(source.CreatedAt, reread.CreatedAt);
        // The writer stamps its own copy time; that is what the reader must find.
        Assert.Equal(CopyTime, reread.LastModifiedAt);
        Assert.Equal(source.Turns, reread.Turns);
    }

    [Fact]
    public async Task WriteAsync_ChainsParentUuidsAndEmitsTheTitleRecord()
    {
        await using var fixture = new WriterFixture();

        var result = await fixture.Writer().WriteAsync(fixture.Conversation(), CancellationToken.None);

        var records = File.ReadAllLines(result.NativePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToArray();
        Assert.Equal(3, records.Length);
        Assert.Equal(JsonValueKind.Null, records[0].GetProperty("parentUuid").ValueKind);
        Assert.Equal(records[0].GetProperty("uuid").GetString(), records[1].GetProperty("parentUuid").GetString());
        Assert.Equal("ai-title", records[2].GetProperty("type").GetString());
        Assert.Equal("Cross-agent title", records[2].GetProperty("aiTitle").GetString());
        Assert.All(records.Take(2), record => Assert.False(record.GetProperty("isSidechain").GetBoolean()));
    }

    [Fact]
    public async Task WriteAsync_NeverReusesTheSourceSessionId()
    {
        await using var fixture = new WriterFixture();
        var source = fixture.Conversation() with { SourceSessionId = FreshId.ToString() };
        var offered = new Queue<Guid>([FreshId, FreshId, Guid.Parse("61000000-0000-0000-0000-000000000007")]);

        var result = await fixture.Writer(() => offered.Dequeue()).WriteAsync(source, CancellationToken.None);

        Assert.NotEqual(FreshId.ToString(), result.SessionId);
        Assert.Equal("61000000-0000-0000-0000-000000000007", result.SessionId);
    }

    [Fact]
    public async Task WriteAsync_LeavesNoStagingDirectoryBehind()
    {
        await using var fixture = new WriterFixture();

        var result = await fixture.Writer().WriteAsync(fixture.Conversation(), CancellationToken.None);

        var project = Path.GetDirectoryName(result.NativePath)!;
        Assert.Empty(Directory.EnumerateDirectories(project));
        Assert.Single(Directory.EnumerateFiles(project));
    }

    [Fact]
    public async Task WriteAsync_RejectsAConversationWithoutAWorkingDirectory()
    {
        await using var fixture = new WriterFixture();
        var source = fixture.Conversation() with { WorkingDirectory = null };

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Writer().WriteAsync(source, CancellationToken.None));
    }

    private sealed class WriterFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"chs-cl-w-{Guid.NewGuid():N}"[..24]);

        public WriterFixture()
        {
            var home = Path.Combine(root, "claude");
            var projects = Path.Combine(home, "projects");
            Directory.CreateDirectory(projects);
            WorkingDirectory = Path.Combine(root, "project");
            Directory.CreateDirectory(WorkingDirectory);
            Paths = new ClaudePaths(home, projects);
        }

        public ClaudePaths Paths { get; }
        public string WorkingDirectory { get; }

        public ClaudeConversationWriter Writer(Func<Guid>? idGenerator = null) =>
            new(Paths, idGenerator ?? (() => FreshId), () => CopyTime);

        public PortableConversation Conversation() => new(
            ConversationAgent.Codex,
            "codex-source",
            "Cross-agent title",
            WorkingDirectory,
            new DateTimeOffset(2026, 8, 9, 10, 11, 12, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 8, 9, 10, 14, 15, TimeSpan.FromHours(3)),
            [
                new PortableTurn(ConversationRole.User, "question"),
                new PortableTurn(ConversationRole.Assistant, "answer")
            ]);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }
}
