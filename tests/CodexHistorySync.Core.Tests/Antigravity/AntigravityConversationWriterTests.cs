using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Hermes;
using CodexHistorySync.Core.Kimi;
using CodexHistorySync.Core.Mimo;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityConversationWriterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workingDir;
    private readonly AntigravityPaths _antigravityPaths;

    public AntigravityConversationWriterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "agy-conv-writer-" + Guid.NewGuid().ToString("N"));
        _workingDir = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(_workingDir);
        var agyHome = Path.Combine(_tempRoot, "antigravity");
        Directory.CreateDirectory(agyHome);
        _antigravityPaths = new AntigravityPaths(agyHome);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task WriteAsync_WithUserAndAssistantTurns_CreatesValidAntigravitySession()
    {
        var source = new PortableConversation(
            ConversationAgent.Antigravity,
            "orig-session",
            "Speed of Light Inquiry",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "What is the speed of light?"),
                new PortableTurn(ConversationRole.Assistant, "Approximately 299,792,458 m/s in vacuum."),
                new PortableTurn(ConversationRole.User, "Thank you!")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(AntigravityPaths.IsSessionId(result.SessionId));
        Assert.Equal(_antigravityPaths.AnchorPath(result.SessionId), result.NativePath);
        Assert.True(File.Exists(result.NativePath));
        Assert.True(File.Exists(_antigravityPaths.ConversationDatabasePath(result.SessionId)));
        Assert.True(File.Exists(_antigravityPaths.TranscriptPath(result.SessionId)));

        // Verify summary
        var summary = AntigravityDatabase.ReadSummary(_antigravityPaths.ConversationSummariesDbPath, result.SessionId);
        Assert.NotNull(summary);
        Assert.Equal("Speed of Light Inquiry", summary.Title);
        Assert.Equal("COMPLETED", summary.Status);

        // Read back with AntigravityConversationReader
        var reader = new AntigravityConversationReader();
        var reread = await reader.ReadAsync(result.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Antigravity, reread.SourceAgent);
        Assert.Equal(result.SessionId, reread.SourceSessionId);
        Assert.Equal("Speed of Light Inquiry", reread.Title);
        Assert.Equal(3, reread.Turns.Count);
        Assert.Equal(source.Turns[0].Role, reread.Turns[0].Role);
        Assert.Equal(source.Turns[0].Text, reread.Turns[0].Text);
        Assert.Equal(source.Turns[1].Role, reread.Turns[1].Role);
        Assert.Equal(source.Turns[1].Text, reread.Turns[1].Text);
        Assert.Equal(source.Turns[2].Role, reread.Turns[2].Role);
        Assert.Equal(source.Turns[2].Text, reread.Turns[2].Text);
    }

    [Fact]
    public async Task WriteAsync_WithEmptyConversation_ThrowsArgumentException()
    {
        var source = new PortableConversation(
            ConversationAgent.Antigravity,
            "orig-empty",
            "Empty",
            _workingDir,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_GeneratesDeterministicGuidAndTruncatesLongTitle()
    {
        var fixedId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var longTitle = new string('A', 120);

        var source = new PortableConversation(
            ConversationAgent.Antigravity,
            "source-id",
            longTitle,
            _workingDir,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Short message")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: () => fixedId);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        Assert.Equal(fixedId, result.SessionId);

        var summary = AntigravityDatabase.ReadSummary(_antigravityPaths.ConversationSummariesDbPath, result.SessionId);
        Assert.NotNull(summary);
        Assert.Equal(80, summary.Title.Length);
        Assert.Equal(new string('A', 80), summary.Title);
    }

    [Theory]
    [InlineData(ConversationAgent.Claude)]
    [InlineData(ConversationAgent.Codex)]
    [InlineData(ConversationAgent.Grok)]
    [InlineData(ConversationAgent.Hermes)]
    [InlineData(ConversationAgent.Kimi)]
    [InlineData(ConversationAgent.Mimo)]
    public async Task ConvertToAntigravity_FromSupportedAgents_PreservesTurns(ConversationAgent agent)
    {
        var source = new PortableConversation(
            agent,
            "agent-session-123",
            $"Cross Agent {agent} Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, $"Message from {agent} user"),
                new PortableTurn(ConversationRole.Assistant, $"Message from {agent} assistant"),
                new PortableTurn(ConversationRole.User, "Follow-up question")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var converted = await reader.ReadAsync(result.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Antigravity, converted.SourceAgent);
        Assert.Equal(3, converted.Turns.Count);
        Assert.Equal(source.Turns[0].Role, converted.Turns[0].Role);
        Assert.Equal(source.Turns[0].Text, converted.Turns[0].Text);
        Assert.Equal(source.Turns[1].Role, converted.Turns[1].Role);
        Assert.Equal(source.Turns[1].Text, converted.Turns[1].Text);
        Assert.Equal(source.Turns[2].Role, converted.Turns[2].Role);
        Assert.Equal(source.Turns[2].Text, converted.Turns[2].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToClaude_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Claude",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Claude from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Claude.")
            ]);

        var claudeHome = Path.Combine(_tempRoot, "claude");
        var claudeProjects = Path.Combine(claudeHome, "projects");
        Directory.CreateDirectory(claudeProjects);
        var claudePaths = new ClaudePaths(claudeHome, claudeProjects);
        var claudeWriter = new ClaudeConversationWriter(claudePaths);

        var writeResult = await claudeWriter.WriteAsync(agyConv, CancellationToken.None);
        var claudeReader = new ClaudeConversationReader();
        var readBack = await claudeReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Claude, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToGrok_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Grok",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Grok from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Grok.")
            ]);

        var grokHome = Path.Combine(_tempRoot, "grok");
        var grokSessions = Path.Combine(grokHome, "sessions");
        Directory.CreateDirectory(grokSessions);
        var grokPaths = new GrokPaths(grokHome, grokSessions);
        var grokWriter = new GrokConversationWriter(grokPaths);

        var writeResult = await grokWriter.WriteAsync(agyConv, CancellationToken.None);
        var grokReader = new GrokConversationReader();
        var readBack = await grokReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Grok, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToHermes_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Hermes",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Hermes from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Hermes.")
            ]);

        var hermesHome = Path.Combine(_tempRoot, "hermes");
        Directory.CreateDirectory(hermesHome);
        var hermesPaths = new HermesPaths(hermesHome);
        var hermesWriter = new HermesConversationWriter(hermesPaths);

        var writeResult = await hermesWriter.WriteAsync(agyConv, CancellationToken.None);
        var hermesReader = new HermesConversationReader();
        var readBack = await hermesReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Hermes, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToKimi_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Kimi",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Kimi from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Kimi.")
            ]);

        var kimiHome = Path.Combine(_tempRoot, "kimi");
        var kimiSessions = Path.Combine(kimiHome, "sessions");
        Directory.CreateDirectory(kimiSessions);
        var kimiPaths = new KimiPaths(kimiHome, kimiSessions);
        var kimiWriter = new KimiConversationWriter(kimiPaths);

        var writeResult = await kimiWriter.WriteAsync(agyConv, CancellationToken.None);
        var kimiReader = new KimiConversationReader();
        var readBack = await kimiReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Kimi, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToMimo_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Mimo",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Mimo from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Mimo.")
            ]);

        var mimoHome = Path.Combine(_tempRoot, "mimo");
        Directory.CreateDirectory(mimoHome);
        var mimoPaths = new MimoPaths(mimoHome);
        var mimoWriter = new MimoConversationWriter(mimoPaths);

        var writeResult = await mimoWriter.WriteAsync(agyConv, CancellationToken.None);
        var mimoReader = new MimoConversationReader();
        var readBack = await mimoReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Mimo, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }
}
