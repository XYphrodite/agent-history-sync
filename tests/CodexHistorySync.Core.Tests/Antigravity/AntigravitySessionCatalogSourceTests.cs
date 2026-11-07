using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravitySessionCatalogSourceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AntigravityPaths _paths;

    public AntigravitySessionCatalogSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "antigravity-cat-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "conversations"));
        _paths = new AntigravityPaths(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_ReturnsRootSessionsOnly()
    {
        var rootId1 = Guid.NewGuid().ToString();
        var rootId2 = Guid.NewGuid().ToString();
        var subId1 = Guid.NewGuid().ToString();

        // Create summary rows: 2 roots, 1 subagent
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: rootId1,
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "code",
            Title: "Root 1",
            Status: "ACTIVE",
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt: DateTimeOffset.UtcNow));

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: rootId2,
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "code",
            Title: "Root 2",
            Status: "ACTIVE",
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-20),
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: subId1,
            ParentConversationId: rootId1,
            NestingDepth: 1,
            AgentName: "subagent",
            Title: "Subagent 1",
            Status: "ACTIVE",
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-8),
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-2)));

        // Create conversation db only for rootId1
        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(rootId1), "traj-1", rootId1, Array.Empty<AntigravityStepRow>());

        var source = new AntigravitySessionCatalogSource(_paths);
        using var limiter = new SessionCatalogReadLimiter(4);
        var candidates = await source.ScanAsync(limiter, CancellationToken.None);

        Assert.Equal(2, candidates.Count);

        var c1 = candidates.Single(c => c.SessionId == rootId1);
        Assert.Equal("Root 1", c1.Title);
        Assert.True(c1.CanRead);
        Assert.Equal(_paths.AnchorPath(rootId1), c1.NativePath);
        Assert.Equal(ManagedTitleSource.Official, c1.TitleSource);

        var c2 = candidates.Single(c => c.SessionId == rootId2);
        Assert.Equal("Root 2", c2.Title);
        Assert.False(c2.CanRead); // db file does not exist
        Assert.Equal(ManagedTitleSource.Official, c2.TitleSource);
    }

    [Fact]
    public async Task ScanAsync_NormalizesTitleAndFallsBackToSessionId()
    {
        var id = Guid.NewGuid().ToString();
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: id,
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: null,
            Title: "   \r\n   ",
            Status: null,
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));

        var source = new AntigravitySessionCatalogSource(_paths);
        using var limiter = new SessionCatalogReadLimiter(4);
        var candidates = await source.ScanAsync(limiter, CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(id, candidate.Title);
        Assert.Equal(ManagedTitleSource.SessionId, candidate.TitleSource);
    }

    [Fact]
    public async Task ScanAsync_DuplicatesMarkedUnreadable()
    {
        var id = Guid.NewGuid().ToString();
        // Insert into summaries table manually to create duplicate rows
        using var conn = AntigravityDatabase.OpenConnection(_paths.ConversationSummariesDbPath, write: true);
        AntigravityDatabase.EnsureSummariesTable(conn);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS conversation_summaries_dup (
                    conversation_id TEXT,
                    parent_conversation_id TEXT,
                    nesting_depth INTEGER,
                    title TEXT
                );
                INSERT INTO conversation_summaries (conversation_id, parent_conversation_id, nesting_depth, title)
                VALUES (@id, '', 0, 'Title 1');
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(id), "traj", id, Array.Empty<AntigravityStepRow>());

        var source = new AntigravitySessionCatalogSource(_paths);
        using var limiter = new SessionCatalogReadLimiter(4);
        var candidates = await source.ScanAsync(limiter, CancellationToken.None);

        Assert.Single(candidates);
        Assert.True(candidates[0].CanRead);
    }

    [Fact]
    public void SessionCatalogSnapshot_BothOverloadsFunctionCorrectly()
    {
        var session = new ManagedSession(
            ManagedAgent.Antigravity,
            Guid.NewGuid().ToString(),
            "path.json",
            "Title",
            DateTimeOffset.UtcNow,
            false,
            true);

        // 9-agent constructor
        var snapshot9 = new SessionCatalogSnapshot([], [], [], [], [], [], [], [], [session])
        {
            ConfiguredAgents = [ManagedAgent.Antigravity]
        };
        Assert.Single(snapshot9.For(ManagedAgent.Antigravity));
        Assert.Empty(snapshot9.For(ManagedAgent.Codex));

        // 8-agent backwards-compatible overload
        var snapshot8 = new SessionCatalogSnapshot([], [], [], [], [], [], [], []);
        Assert.Empty(snapshot8.For(ManagedAgent.Antigravity));
    }
}
