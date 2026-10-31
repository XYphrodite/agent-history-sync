using System.Text;
using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class Challenger1EdgeCaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AntigravityPaths _paths;

    public Challenger1EdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agy-challenger1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "conversations"));
        _paths = new AntigravityPaths(_tempDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Catalog_MissingSummariesDatabaseFile_ReturnsEmptyGracefully()
    {
        var nonExistentHome = Path.Combine(_tempDir, "does-not-exist");
        var paths = new AntigravityPaths(nonExistentHome);
        var source = new AntigravitySessionCatalogSource(paths);
        using var limiter = new SessionCatalogReadLimiter(4);

        var candidates = await source.ScanAsync(limiter, CancellationToken.None);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Catalog_ZeroByteDatabaseFile_ReturnsEmptyGracefully()
    {
        File.WriteAllBytes(_paths.ConversationSummariesDbPath, Array.Empty<byte>());

        var source = new AntigravitySessionCatalogSource(_paths);
        using var limiter = new SessionCatalogReadLimiter(4);

        var candidates = await source.ScanAsync(limiter, CancellationToken.None);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Catalog_NoSummariesTable_ReturnsEmptyGracefully()
    {
        using (var conn = AntigravityDatabase.OpenConnection(_paths.ConversationSummariesDbPath, write: true))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE some_other_table (id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }

        var source = new AntigravitySessionCatalogSource(_paths);
        using var limiter = new SessionCatalogReadLimiter(4);

        var candidates = await source.ScanAsync(limiter, CancellationToken.None);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task Catalog_DuplicatesMarkedUnreadable()
    {
        // Create table without primary key to simulate legacy or corrupt DB with duplicate IDs
        using (var conn = AntigravityDatabase.OpenConnection(_paths.ConversationSummariesDbPath, write: true))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE conversation_summaries (
                    conversation_id TEXT,
                    parent_conversation_id TEXT,
                    nesting_depth INTEGER,
                    title TEXT,
                    created_at INTEGER,
                    updated_at INTEGER
                );
                """;
            cmd.ExecuteNonQuery();
        }

        var dupId = Guid.NewGuid().ToString();
        var uniqueId = Guid.NewGuid().ToString();

        // Write 2 rows with dupId, 1 with uniqueId
        using (var conn = AntigravityDatabase.OpenConnection(_paths.ConversationSummariesDbPath, write: true))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO conversation_summaries (conversation_id, nesting_depth, title, created_at, updated_at)
                VALUES (@id1, 0, 'Dup Copy 1', 1000, 1000);
                INSERT INTO conversation_summaries (conversation_id, nesting_depth, title, created_at, updated_at)
                VALUES (@id1, 0, 'Dup Copy 2', 1000, 1000);
                INSERT INTO conversation_summaries (conversation_id, nesting_depth, title, created_at, updated_at)
                VALUES (@id2, 0, 'Unique Session', 1000, 1000);
                """;
            cmd.Parameters.AddWithValue("@id1", dupId);
            cmd.Parameters.AddWithValue("@id2", uniqueId);
            cmd.ExecuteNonQuery();
        }

        // Create valid conv db files for both
        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(dupId), "traj", dupId, Array.Empty<AntigravityStepRow>());
        AntigravityDatabase.WriteSteps(_paths.ConversationDatabasePath(uniqueId), "traj", uniqueId, Array.Empty<AntigravityStepRow>());

        var source = new AntigravitySessionCatalogSource(_paths);
        using var limiter = new SessionCatalogReadLimiter(4);
        var candidates = await source.ScanAsync(limiter, CancellationToken.None);

        Assert.Equal(3, candidates.Count);

        var dups = candidates.Where(c => c.SessionId == dupId).ToList();
        Assert.Equal(2, dups.Count);
        Assert.All(dups, d => Assert.False(d.CanRead));

        var unique = candidates.Single(c => c.SessionId == uniqueId);
        Assert.True(unique.CanRead);
    }

    [Fact]
    public async Task Catalog_CancellationToken_AbortsScan()
    {
        var source = new AntigravitySessionCatalogSource(_paths);
        using var limiter = new SessionCatalogReadLimiter(4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.ScanAsync(limiter, cts.Token));
    }

    [Fact]
    public async Task Catalog_TitleTruncationAndWrapperHandling()
    {
        var longTitleId = Guid.NewGuid().ToString();
        var wrapperTitleId = Guid.NewGuid().ToString();
        var normalTitleId = Guid.NewGuid().ToString();

        var titleOver80 = new string('A', 120);

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            longTitleId, null, 0, "agent", titleOver80, "OK", Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            wrapperTitleId, null, 0, "agent", "<environment_context> some text", "OK", Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            normalTitleId, null, 0, "agent", "Normal Title\r\nWith Newlines", "OK", Array.Empty<string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var source = new AntigravitySessionCatalogSource(_paths);
        using var limiter = new SessionCatalogReadLimiter(4);
        var candidates = await source.ScanAsync(limiter, CancellationToken.None);

        var longCandidate = candidates.Single(c => c.SessionId == longTitleId);
        Assert.Equal(80, longCandidate.Title.Length);
        Assert.Equal(new string('A', 80), longCandidate.Title);
        Assert.Equal(ManagedTitleSource.Official, longCandidate.TitleSource);

        var wrapperCandidate = candidates.Single(c => c.SessionId == wrapperTitleId);
        Assert.Equal(wrapperTitleId, wrapperCandidate.Title);
        Assert.Equal(ManagedTitleSource.SessionId, wrapperCandidate.TitleSource);

        var normalCandidate = candidates.Single(c => c.SessionId == normalTitleId);
        Assert.Equal("Normal Title  With Newlines", normalCandidate.Title);
    }

    [Fact]
    public async Task ConversationReader_CorruptedProtobufStepPayloads_HandledGracefully()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);

        // Mix corrupt payloads with one valid user step and one valid assistant step
        var steps = new List<AntigravityStepRow>
        {
            // Corrupt 1: All 0xFF varint overflow
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }),

            // Corrupt 2: Declares length 10,000 but only has 2 bytes
            new(1, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                new byte[] { (19 << 3) | 2, 0x90, 0x4E, 0x01, 0x02 }),

            // Corrupt 3: Invalid wire type 7
            new(2, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                new byte[] { (20 << 3) | 7, 0x01 }),

            // Valid user step
            new(3, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("Valid question after corrupt steps")),

            // Corrupt 4: Truncated 1 byte
            new(4, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                new byte[] { 0x08 }),

            // Valid assistant step
            new(5, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodePlannerResponse("Valid answer despite corrupt neighbours"))
        };

        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, steps);

        var reader = new AntigravityConversationReader();
        var conversation = await reader.ReadAsync(convDb, CancellationToken.None);

        Assert.NotNull(conversation);
        Assert.Equal(sessionId, conversation.SourceSessionId);
        Assert.Equal(2, conversation.Turns.Count);

        Assert.Equal(ConversationRole.User, conversation.Turns[0].Role);
        Assert.Equal("Valid question after corrupt steps", conversation.Turns[0].Text);

        Assert.Equal(ConversationRole.Assistant, conversation.Turns[1].Role);
        Assert.Equal("Valid answer despite corrupt neighbours", conversation.Turns[1].Text);
    }

    [Fact]
    public async Task ConversationReader_AllCorruptPayloadsThrowsInvalidDataException()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone, new byte[] { 0xFF, 0xFF }),
            new(1, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone, new byte[] { 0x00, 0x01 })
        };

        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, steps);

        var reader = new AntigravityConversationReader();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadAsync(convDb, CancellationToken.None));
    }

    [Fact]
    public async Task ConversationReader_WhitespaceResponseFallsBackToThinking()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("What should I do next?")),
            // Response is pure whitespace "   \r\n   "
            new(1, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodePlannerResponse("   \r\n   ", "Plan step 1: Check inputs. Plan step 2: Execute."))
        };

        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, steps);

        var reader = new AntigravityConversationReader();
        var conversation = await reader.ReadAsync(convDb, CancellationToken.None);

        Assert.Equal(2, conversation.Turns.Count);
        Assert.Equal(ConversationRole.Assistant, conversation.Turns[1].Role);
        Assert.Equal("Plan step 1: Check inputs. Plan step 2: Execute.", conversation.Turns[1].Text);
    }

    [Fact]
    public async Task ConversationReader_ResponsePreferredOverThinkingWhenBothPresent()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("Compute square of 9")),
            new(1, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodePlannerResponse("The square of 9 is 81.", "Thinking: 9 * 9 = 81."))
        };

        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, steps);

        var reader = new AntigravityConversationReader();
        var conversation = await reader.ReadAsync(convDb, CancellationToken.None);

        Assert.Equal(2, conversation.Turns.Count);
        Assert.Equal(ConversationRole.Assistant, conversation.Turns[1].Role);
        Assert.Equal("The square of 9 is 81.", conversation.Turns[1].Text);
    }

    [Fact]
    public async Task ConversationReader_ReadRealSession5cd2c358_EndToEnd()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sessionDb = Path.Combine(userProfile, ".gemini", "antigravity-cli", "conversations", "5cd2c358-74bf-45cd-908c-a86726b982bb.db");
        if (!File.Exists(sessionDb)) return;

        var reader = new AntigravityConversationReader();
        var conv = await reader.ReadAsync(sessionDb, CancellationToken.None);

        Assert.NotNull(conv);
        Assert.Equal(ConversationAgent.Antigravity, conv.SourceAgent);
        Assert.Equal("5cd2c358-74bf-45cd-908c-a86726b982bb", conv.SourceSessionId);
        Assert.NotEmpty(conv.Turns);
        Assert.Contains(conv.Turns, t => t.Role == ConversationRole.User);
        Assert.Contains(conv.Turns, t => t.Role == ConversationRole.Assistant);
        Assert.False(string.IsNullOrWhiteSpace(conv.Title));
    }
}
