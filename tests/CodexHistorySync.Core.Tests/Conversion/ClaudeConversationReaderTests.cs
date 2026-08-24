using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Conversion;

public sealed class ClaudeConversationReaderTests
{
    private const string SessionId = "50000000-0000-0000-0000-000000000005";

    [Fact]
    public async Task ReadAsync_KeepsTextTurnsAndDropsEverythingElse()
    {
        await using var fixture = new TranscriptFixture();
        var path = fixture.Write(
            Turn("user", "question", timestamp: "2026-08-24T10:00:00+00:00"),
            Record(new
            {
                type = "assistant",
                sessionId = SessionId,
                cwd = fixture.Cwd,
                timestamp = "2026-08-24T10:01:00+00:00",
                message = new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "thinking", thinking = "internal reasoning" },
                        new { type = "text", text = "answer" },
                        new { type = "tool_use", name = "Bash", input = new { command = "ls" } }
                    }
                }
            }),
            Record(new { type = "attachment", sessionId = SessionId, cwd = fixture.Cwd, attachment = new { type = "deferred_tools_delta" } }),
            Record(new { type = "file-history-snapshot", messageId = "x", snapshot = new { trackedFileBackups = new { } } }),
            Record(new { type = "queue-operation", operation = "enqueue", sessionId = SessionId }),
            Record(new { type = "last-prompt", lastPrompt = "question", sessionId = SessionId }));

        var conversation = await new ClaudeConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal(ConversationAgent.Claude, conversation.SourceAgent);
        Assert.Equal(SessionId, conversation.SourceSessionId);
        Assert.Equal(
            [new PortableTurn(ConversationRole.User, "question"), new PortableTurn(ConversationRole.Assistant, "answer")],
            conversation.Turns);
        Assert.Equal(DateTimeOffset.Parse("2026-08-24T10:00:00+00:00"), conversation.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-08-24T10:01:00+00:00"), conversation.LastModifiedAt);
    }

    [Fact]
    public async Task ReadAsync_TakesTheLastAiTitleRecord()
    {
        await using var fixture = new TranscriptFixture();
        var path = fixture.Write(
            Record(new { type = "ai-title", aiTitle = "First guess", sessionId = SessionId }),
            Turn("user", "question"),
            Record(new { type = "ai-title", aiTitle = "Refined title", sessionId = SessionId }));

        var conversation = await new ClaudeConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal("Refined title", conversation.Title);
    }

    [Fact]
    public async Task ReadAsync_FallsBackToSummaryThenToTheFirstUserTurn()
    {
        await using var fixture = new TranscriptFixture();
        var withSummary = fixture.WriteNamed("summary",
            Record(new { type = "summary", summary = "Summarized title", sessionId = SessionId }),
            Turn("user", "question"));
        var withNeither = fixture.WriteNamed("bare", Turn("user", "the first user turn becomes the title"));

        var summarized = await new ClaudeConversationReader().ReadAsync(withSummary, CancellationToken.None);
        var bare = await new ClaudeConversationReader().ReadAsync(withNeither, CancellationToken.None);

        Assert.Equal("Summarized title", summarized.Title);
        Assert.Equal("the first user turn becomes the title", bare.Title);
    }

    [Fact]
    public async Task ReadAsync_SkipsTechnicalWrapperUserTurnsAndSidechainRecords()
    {
        await using var fixture = new TranscriptFixture();
        var path = fixture.Write(
            Turn("user", "<ide_opened_file>c:\\Repos\\Reborn\\ROADMAP.md</ide_opened_file>"),
            Turn("user", "<system-reminder>background context</system-reminder>"),
            // Written by Claude Code on interrupt, not typed by anyone.
            Turn("user", "[Request interrupted by user]"),
            Turn("user", "[Request interrupted by user for tool use]"),
            Turn("user", "real question"),
            Record(new
            {
                type = "assistant",
                isSidechain = true,
                sessionId = SessionId,
                cwd = fixture.Cwd,
                message = new { role = "assistant", content = new object[] { new { type = "text", text = "subagent chatter" } } }
            }),
            Turn("assistant", "real answer"));

        var conversation = await new ClaudeConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal(
            [new PortableTurn(ConversationRole.User, "real question"), new PortableTurn(ConversationRole.Assistant, "real answer")],
            conversation.Turns);
    }

    [Fact]
    public async Task ReadAsync_RejectsATranscriptWithoutConversationText()
    {
        await using var fixture = new TranscriptFixture();
        var path = fixture.Write(
            Record(new { type = "user", sessionId = SessionId, cwd = fixture.Cwd, message = new { role = "user", content = Array.Empty<object>() } }),
            Record(new { type = "ai-title", aiTitle = "Nothing to copy", sessionId = SessionId }));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClaudeConversationReader().ReadAsync(path, CancellationToken.None));
    }

    private static string Record(object value) => JsonSerializer.Serialize(value);

    private string Turn(string role, string text, string? timestamp = null) => Record(new
    {
        type = role,
        isSidechain = false,
        sessionId = SessionId,
        cwd = TranscriptFixture.SharedCwd,
        timestamp,
        message = new { role, content = new object[] { new { type = "text", text } } }
    });

    private sealed class TranscriptFixture : IAsyncDisposable
    {
        public const string SharedCwd = @"C:\Repos\Demo";
        private readonly string root = Path.Combine(Path.GetTempPath(), $"chs-claude-reader-{Guid.NewGuid():N}");

        public TranscriptFixture() => Directory.CreateDirectory(Path.Combine(root, "c--Repos-Demo"));

        public string Cwd => SharedCwd;

        public string Write(params string[] records) => WriteNamed("main", records);

        public string WriteNamed(string name, params string[] records)
        {
            var directory = Path.Combine(root, "c--Repos-Demo", name);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, SessionId + ".jsonl");
            File.WriteAllText(path, string.Join("\n", records) + "\n", new UTF8Encoding(false));
            return path;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }
}
