using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravitySessionFamilyReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AntigravityPaths _paths;

    public AntigravitySessionFamilyReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "antigravity-fam-test-" + Guid.NewGuid().ToString("N"));
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
    public async Task ReadAsync_BuildsHierarchicalSubagentTree()
    {
        var rootId = Guid.NewGuid().ToString();
        var child1Id = Guid.NewGuid().ToString();
        var child2Id = Guid.NewGuid().ToString();
        var grandChildId = Guid.NewGuid().ToString();

        // Write summaries
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            rootId, null, 0, "parent", "Root Session", null, Array.Empty<string>(), DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow));

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            child1Id, rootId, 1, "worker-1", "Worker 1", null, Array.Empty<string>(), DateTimeOffset.UtcNow.AddMinutes(-20), DateTimeOffset.UtcNow.AddMinutes(-15)));

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            child2Id, rootId, 1, "worker-2", "Worker 2", null, Array.Empty<string>(), DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-5)));

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            grandChildId, child1Id, 2, "specialist", "Specialist Grandchild", null, Array.Empty<string>(), DateTimeOffset.UtcNow.AddMinutes(-14), DateTimeOffset.UtcNow.AddMinutes(-12)));

        // Create db files
        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(rootId), "t1", rootId, Array.Empty<AntigravityStepRow>());
        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(child1Id), "t2", child1Id, Array.Empty<AntigravityStepRow>());
        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(child2Id), "t3", child2Id, Array.Empty<AntigravityStepRow>());
        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(grandChildId), "t4", grandChildId, Array.Empty<AntigravityStepRow>());

        var rootSession = new ManagedSession(
            ManagedAgent.Antigravity,
            rootId,
            _paths.AnchorPath(rootId),
            "Root Session",
            DateTimeOffset.UtcNow,
            false,
            true);

        var reader = new LocalSessionFamilyReader(null, null, _paths);
        var thread = await reader.ReadAsync(rootSession, CancellationToken.None);

        Assert.Equal(rootId, thread.Session.SessionId);
        Assert.Equal(2, thread.Children.Count);

        // Children ordered by UpdatedAt descending: child2 (updated -5m) should be first, child1 (updated -15m) second
        var firstChild = thread.Children[0];
        Assert.Equal(child2Id, firstChild.Session.SessionId);
        Assert.Empty(firstChild.Children);

        var secondChild = thread.Children[1];
        Assert.Equal(child1Id, secondChild.Session.SessionId);
        Assert.Single(secondChild.Children);

        var grandChild = secondChild.Children[0];
        Assert.Equal(grandChildId, grandChild.Session.SessionId);
        Assert.Empty(grandChild.Children);
    }

    [Fact]
    public async Task ReadAsync_CycleDetectionPreventsInfiniteLoop()
    {
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();

        // Cyclic dependency: id1 -> id2 -> id1
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            id1, null, 0, "agent", "Session 1", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            id2, id1, 1, "agent", "Session 2", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Create cyclic pointer
        using var conn = AntigravityDatabase.OpenConnection(_paths.ConversationSummariesDbPath, write: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO conversation_summaries (conversation_id, parent_conversation_id, nesting_depth, title) VALUES (@id1, @id2, 2, 'Session 1 Cyclical');";
        cmd.Parameters.AddWithValue("@id1", id1);
        cmd.Parameters.AddWithValue("@id2", id2);
        cmd.ExecuteNonQuery();

        var rootSession = new ManagedSession(
            ManagedAgent.Antigravity,
            id1,
            _paths.AnchorPath(id1),
            "Session 1",
            DateTimeOffset.UtcNow,
            false,
            true);

        var reader = new LocalSessionFamilyReader(null, null, _paths);
        var thread = await reader.ReadAsync(rootSession, CancellationToken.None);

        // Must complete without stack overflow
        Assert.NotNull(thread);
    }

    [Fact]
    public async Task ReadAsync_DepthLimit32_TruncatesAt32Levels()
    {
        // Create a chain of 40 generations (depth 0 to depth 39)
        var ids = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid().ToString()).ToList();

        for (int i = 0; i < ids.Count; i++)
        {
            var parentId = i == 0 ? null : ids[i - 1];
            AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
                ids[i], parentId, i, "agent", $"Session Depth {i}", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(ids[i]), $"t{i}", ids[i], Array.Empty<AntigravityStepRow>());
        }

        var rootSession = new ManagedSession(
            ManagedAgent.Antigravity,
            ids[0],
            _paths.AnchorPath(ids[0]),
            "Session Depth 0",
            DateTimeOffset.UtcNow,
            false,
            true);

        var reader = new LocalSessionFamilyReader(null, null, _paths);
        var thread = await reader.ReadAsync(rootSession, CancellationToken.None);

        // Traverse down the chain to measure depth
        var current = thread;
        int depthReached = 0;
        while (current.Children.Count > 0)
        {
            Assert.Single(current.Children);
            current = current.Children[0];
            depthReached++;
        }

        // Depth limit is 32: root is depth 0, depth 31 has child at depth 32, which cannot have children.
        Assert.Equal(32, depthReached);
        Assert.Equal(ids[32], current.Session.SessionId);
        Assert.Empty(current.Children);
    }

    [Fact]
    public async Task ReadAsync_SelfReferencingRoot_DoesNotLoop()
    {
        var id = Guid.NewGuid().ToString();
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            id, id, 0, "agent", "Self Referencing", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(id), "t", id, Array.Empty<AntigravityStepRow>());

        var rootSession = new ManagedSession(
            ManagedAgent.Antigravity,
            id,
            _paths.AnchorPath(id),
            "Self Referencing",
            DateTimeOffset.UtcNow,
            false,
            true);

        var reader = new LocalSessionFamilyReader(null, null, _paths);
        var thread = await reader.ReadAsync(rootSession, CancellationToken.None);

        Assert.NotNull(thread);
        Assert.Empty(thread.Children);
    }

    [Fact]
    public async Task ReadAsync_ThreeWayCycle_DoesNotLoop()
    {
        var idA = Guid.NewGuid().ToString();
        var idB = Guid.NewGuid().ToString();
        var idC = Guid.NewGuid().ToString();

        // A -> B -> C -> A
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            idA, null, 0, "agent", "Node A", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            idB, idA, 1, "agent", "Node B", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            idC, idB, 2, "agent", "Node C", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Point C back to A
        using (var conn = AntigravityDatabase.OpenConnection(_paths.ConversationSummariesDbPath, write: true))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO conversation_summaries (conversation_id, parent_conversation_id, nesting_depth, title) VALUES (@idA, @idC, 3, 'Node A Cycle');";
            cmd.Parameters.AddWithValue("@idA", idA);
            cmd.Parameters.AddWithValue("@idC", idC);
            cmd.ExecuteNonQuery();
        }

        var rootSession = new ManagedSession(
            ManagedAgent.Antigravity,
            idA,
            _paths.AnchorPath(idA),
            "Node A",
            DateTimeOffset.UtcNow,
            false,
            true);

        var reader = new LocalSessionFamilyReader(null, null, _paths);
        var thread = await reader.ReadAsync(rootSession, CancellationToken.None);

        Assert.NotNull(thread);
        Assert.Single(thread.Children);
        var nodeB = thread.Children[0];
        Assert.Equal(idB, nodeB.Session.SessionId);
        Assert.Single(nodeB.Children);
        var nodeC = nodeB.Children[0];
        Assert.Equal(idC, nodeC.Session.SessionId);
        // Node C's child would be A, but A was already visited, so C has no children
        Assert.Empty(nodeC.Children);
    }

    [Fact]
    public async Task ReadAsync_DiamondDag_VisitsNodeOnceWithoutInfiniteLoop()
    {
        var idRoot = Guid.NewGuid().ToString();
        var idB = Guid.NewGuid().ToString();
        var idC = Guid.NewGuid().ToString();
        var idD = Guid.NewGuid().ToString();

        // Root -> B, Root -> C
        // B -> D, C -> D
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            idRoot, null, 0, "agent", "Root", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            idB, idRoot, 1, "agent", "B", null, Array.Empty<string>(), DateTimeOffset.UtcNow.AddMinutes(2), DateTimeOffset.UtcNow.AddMinutes(2)));
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            idC, idRoot, 1, "agent", "C", null, Array.Empty<string>(), DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(1)));

        // In summaries, primary parent is B
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            idD, idB, 2, "agent", "D", null, Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var rootSession = new ManagedSession(
            ManagedAgent.Antigravity,
            idRoot,
            _paths.AnchorPath(idRoot),
            "Root",
            DateTimeOffset.UtcNow,
            false,
            true);

        var reader = new LocalSessionFamilyReader(null, null, _paths);
        var thread = await reader.ReadAsync(rootSession, CancellationToken.None);

        Assert.NotNull(thread);
        Assert.Equal(2, thread.Children.Count);
    }
}

