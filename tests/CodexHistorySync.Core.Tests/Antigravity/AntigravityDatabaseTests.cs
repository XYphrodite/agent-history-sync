using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityDatabaseTests : IDisposable
{
    private readonly string _tempDir;

    public AntigravityDatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "antigravity-db-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ReadSummariesNonExistentFileReturnsEmpty()
    {
        var dbPath = Path.Combine(_tempDir, "missing.db");
        var summaries = AntigravityDatabase.ReadSummaries(dbPath);
        Assert.Empty(summaries);
    }

    [Fact]
    public void ReadStepsNonExistentFileReturnsEmpty()
    {
        var dbPath = Path.Combine(_tempDir, "missing.db");
        var steps = AntigravityDatabase.ReadSteps(dbPath);
        Assert.Empty(steps);
    }

    [Fact]
    public void ReadTrajectoryMetaNonExistentFileReturnsNull()
    {
        var dbPath = Path.Combine(_tempDir, "missing.db");
        var meta = AntigravityDatabase.ReadTrajectoryMeta(dbPath);
        Assert.Null(meta);
    }

    [Fact]
    public void SummariesRoundTripPreservesFields()
    {
        var dbPath = Path.Combine(_tempDir, "summaries.db");
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        var updatedAt = DateTimeOffset.UtcNow;

        var expectedRow1 = new AntigravitySummaryRow(
            ConversationId: "conv-1",
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "teamwork",
            Title: "Test Title 1",
            Status: "COMPLETED",
            WorkspaceUris: new[] { "file:///c:/workspace1" },
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(createdAt.ToUnixTimeSeconds()),
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(updatedAt.ToUnixTimeSeconds()));

        var expectedRow2 = new AntigravitySummaryRow(
            ConversationId: "conv-2",
            ParentConversationId: "conv-1",
            NestingDepth: 1,
            AgentName: "worker",
            Title: "Subagent Task",
            Status: "RUNNING",
            WorkspaceUris: new[] { "file:///c:/workspace1", "file:///c:/workspace2" },
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(createdAt.ToUnixTimeSeconds()),
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(updatedAt.ToUnixTimeSeconds()));

        AntigravityDatabase.WriteSummary(dbPath, expectedRow1);
        AntigravityDatabase.WriteSummary(dbPath, expectedRow2);

        var summaries = AntigravityDatabase.ReadSummaries(dbPath);
        Assert.Equal(2, summaries.Count);

        var actual1 = summaries.First(s => s.ConversationId == "conv-1");
        Assert.Null(actual1.ParentConversationId);
        Assert.Equal(0, actual1.NestingDepth);
        Assert.Equal("teamwork", actual1.AgentName);
        Assert.Equal("Test Title 1", actual1.Title);
        Assert.Equal("COMPLETED", actual1.Status);
        Assert.Single(actual1.WorkspaceUris);
        Assert.Equal("file:///c:/workspace1", actual1.WorkspaceUris[0]);
        Assert.Equal(expectedRow1.CreatedAt, actual1.CreatedAt);
        Assert.Equal(expectedRow1.UpdatedAt, actual1.UpdatedAt);

        var actual2 = summaries.First(s => s.ConversationId == "conv-2");
        Assert.Equal("conv-1", actual2.ParentConversationId);
        Assert.Equal(1, actual2.NestingDepth);
        Assert.Equal("worker", actual2.AgentName);
        Assert.Equal("Subagent Task", actual2.Title);
        Assert.Equal("RUNNING", actual2.Status);
        Assert.Equal(2, actual2.WorkspaceUris.Count);
        Assert.Equal("file:///c:/workspace1", actual2.WorkspaceUris[0]);
        Assert.Equal("file:///c:/workspace2", actual2.WorkspaceUris[1]);
    }

    [Fact]
    public void ConversationStepsAndMetaRoundTripSucceeds()
    {
        var dbPath = Path.Combine(_tempDir, "conv.db");
        var trajectoryId = "traj-123";
        var conversationId = "conv-123";

        var steps = new List<AntigravityStepRow>
        {
            new(Index: 0, StepType: 14, Status: 3, Payload: new byte[] { 1, 2, 3, 4 }),
            new(Index: 1, StepType: 15, Status: 3, Payload: new byte[] { 5, 6, 7, 8 })
        };

        AntigravityDatabase.WriteSteps(dbPath, trajectoryId, conversationId, steps);

        var meta = AntigravityDatabase.ReadTrajectoryMeta(dbPath);
        Assert.NotNull(meta);
        Assert.Equal(trajectoryId, meta!.TrajectoryId);
        Assert.Equal(conversationId, meta.ConversationId);
        Assert.Equal(4, meta.TrajectoryType);
        Assert.Equal(2, meta.TotalSteps);

        var readSteps = AntigravityDatabase.ReadSteps(dbPath);
        Assert.Equal(2, readSteps.Count);
        Assert.Equal(0, readSteps[0].Index);
        Assert.Equal(14, readSteps[0].StepType);
        Assert.Equal(3, readSteps[0].Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, readSteps[0].Payload);

        Assert.Equal(1, readSteps[1].Index);
        Assert.Equal(15, readSteps[1].StepType);
        Assert.Equal(3, readSteps[1].Status);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, readSteps[1].Payload);
    }

    [Fact]
    public void ReadSummariesRealEnvironmentIfPresentSucceeds()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var realDb = Path.Combine(userProfile, ".gemini", "antigravity-cli", "conversation_summaries.db");
        if (!File.Exists(realDb)) return;

        var summaries = AntigravityDatabase.ReadSummaries(realDb);
        Assert.NotEmpty(summaries);
        var first = summaries[0];
        Assert.False(string.IsNullOrWhiteSpace(first.ConversationId));

        var convDb = Path.Combine(userProfile, ".gemini", "antigravity-cli", "conversations", $"{first.ConversationId}.db");
        if (File.Exists(convDb))
        {
            var meta = AntigravityDatabase.ReadTrajectoryMeta(convDb);
            Assert.NotNull(meta);
            Assert.Equal(first.ConversationId, meta!.ConversationId);

            var steps = AntigravityDatabase.ReadSteps(convDb);
            Assert.NotNull(steps);
        }
    }

    [Fact]
    public void ReadRealSession5cd2c358ExtractsThinkingFromPlannerStep()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sessionDb = Path.Combine(userProfile, ".gemini", "antigravity-cli", "conversations", "5cd2c358-74bf-45cd-908c-a86726b982bb.db");
        if (!File.Exists(sessionDb)) return;

        var steps = AntigravityDatabase.ReadSteps(sessionDb);
        Assert.NotEmpty(steps);

        var plannerStep = steps.FirstOrDefault(s => s.StepType == AntigravityProtobuf.StepTypePlannerResponse && s.Payload.Length > 0);
        Assert.NotNull(plannerStep);

        var (resp, think) = AntigravityProtobuf.ExtractPlannerResponse(plannerStep.Payload);
        Assert.False(string.IsNullOrWhiteSpace(resp) && string.IsNullOrWhiteSpace(think), "Planner step must have either response or thinking text.");

        var text = AntigravityProtobuf.TryExtractStepText(plannerStep.StepType, plannerStep.Payload);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public async Task ReadRealSession5cd2c358_AllVerificationRequirements()
    {
        var resolved = AntigravityPaths.TryResolve();
        Assert.NotNull(resolved);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expectedHome = Path.Combine(userProfile, ".gemini", "antigravity-cli");
        Assert.Equal(Path.GetFullPath(expectedHome), Path.GetFullPath(resolved!.Home));

        var summary = AntigravityDatabase.ReadSummary(resolved.ConversationSummariesDbPath, "5cd2c358-74bf-45cd-908c-a86726b982bb");
        Assert.NotNull(summary);
        Assert.Equal("5cd2c358-74bf-45cd-908c-a86726b982bb", summary!.ConversationId);
        Assert.Equal("Antigravity Sessions Project Review", summary.Title);

        var convDb = resolved.ConversationDatabasePath("5cd2c358-74bf-45cd-908c-a86726b982bb");
        Assert.True(File.Exists(convDb));
        var steps = AntigravityDatabase.ReadSteps(convDb);
        Assert.Equal(837, steps.Count);

        var reader = new AntigravityConversationReader();
        var conv = await reader.ReadAsync(convDb, CancellationToken.None);
        Assert.NotNull(conv);
        Assert.Equal(ConversationAgent.Antigravity, conv.SourceAgent);
        Assert.Equal("5cd2c358-74bf-45cd-908c-a86726b982bb", conv.SourceSessionId);
        Assert.Equal(239, conv.Turns.Count);
        Assert.Equal("Antigravity Sessions Project Review", conv.Title);
    }

    [Fact]
    public void ReadOperationsOnZeroByteFileReturnEmptyOrNullWithoutThrowing()
    {
        var zeroByteFile = Path.Combine(_tempDir, "zero_bytes.db");
        File.WriteAllBytes(zeroByteFile, Array.Empty<byte>());

        var summaries = AntigravityDatabase.ReadSummaries(zeroByteFile);
        Assert.Empty(summaries);

        var steps = AntigravityDatabase.ReadSteps(zeroByteFile);
        Assert.Empty(steps);

        var meta = AntigravityDatabase.ReadTrajectoryMeta(zeroByteFile);
        Assert.Null(meta);
    }

    [Fact]
    public void ReadOperationsOnDatabaseWithoutExpectedTablesReturnEmptyOrNullWithoutThrowing()
    {
        var emptyDb = Path.Combine(_tempDir, "empty_schema.db");
        using (var conn = AntigravityDatabase.OpenConnection(emptyDb, write: true))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE unrelated_table (id INTEGER PRIMARY KEY);";
            cmd.ExecuteNonQuery();
        }

        var summaries = AntigravityDatabase.ReadSummaries(emptyDb);
        Assert.Empty(summaries);

        var steps = AntigravityDatabase.ReadSteps(emptyDb);
        Assert.Empty(steps);

        var meta = AntigravityDatabase.ReadTrajectoryMeta(emptyDb);
        Assert.Null(meta);
    }

    [Fact]
    public void NoConnectionPoolingAllowsImmediateFileDeletionOnWindows()
    {
        var dbPath = Path.Combine(_tempDir, "pooling_check.db");

        var row = new AntigravitySummaryRow(
            ConversationId: "conv-pool",
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "agent",
            Title: "Title",
            Status: "DONE",
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        AntigravityDatabase.WriteSummary(dbPath, row);

        var summaries = AntigravityDatabase.ReadSummaries(dbPath);
        Assert.Single(summaries);

        // With builder.Pooling = false, File.Delete should succeed immediately without IOException
        var ex = Record.Exception(() => File.Delete(dbPath));
        Assert.Null(ex);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void WriteStepsCleansStaleOrphanStepsWhenStepCountDecreases()
    {
        var dbPath = Path.Combine(_tempDir, "stale_steps.db");
        var trajectoryId = "traj-1";
        var conversationId = "conv-1";

        // Initial write with 4 steps
        var initialSteps = new List<AntigravityStepRow>
        {
            new(0, 14, 3, new byte[] { 0 }),
            new(1, 15, 3, new byte[] { 1 }),
            new(2, 14, 3, new byte[] { 2 }),
            new(3, 15, 3, new byte[] { 3 })
        };
        AntigravityDatabase.WriteSteps(dbPath, trajectoryId, conversationId, initialSteps);
        var readInitial = AntigravityDatabase.ReadSteps(dbPath);
        Assert.Equal(4, readInitial.Count);

        // Subsequent write with only 2 steps (e.g. trajectory roll back or branch truncate)
        var truncatedSteps = new List<AntigravityStepRow>
        {
            new(0, 14, 3, new byte[] { 10 }),
            new(1, 15, 3, new byte[] { 11 })
        };
        AntigravityDatabase.WriteSteps(dbPath, trajectoryId, conversationId, truncatedSteps);

        var readTruncated = AntigravityDatabase.ReadSteps(dbPath);
        Assert.Equal(2, readTruncated.Count);
        Assert.Equal(0, readTruncated[0].Index);
        Assert.Equal(1, readTruncated[1].Index);
        Assert.Equal(new byte[] { 10 }, readTruncated[0].Payload);
        Assert.Equal(new byte[] { 11 }, readTruncated[1].Payload);
    }

    [Fact]
    public void ReadSummariesFallsBackToPreviewWhenTitleIsEmptyOrWhitespace()
    {
        var dbPath = Path.Combine(_tempDir, "fallback_title.db");
        using (var conn = AntigravityDatabase.OpenConnection(dbPath, write: true))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE conversation_summaries (
                    conversation_id TEXT PRIMARY KEY,
                    title TEXT,
                    preview TEXT,
                    created_at INTEGER,
                    updated_at INTEGER
                );
                INSERT INTO conversation_summaries (conversation_id, title, preview, created_at, updated_at)
                VALUES ('conv-fallback', '   ', 'Fallback Title From Preview', 1700000000, 1700000000);
                """;
            cmd.ExecuteNonQuery();
        }

        var summaries = AntigravityDatabase.ReadSummaries(dbPath);
        Assert.Single(summaries);
        Assert.Equal("Fallback Title From Preview", summaries[0].Title);
    }

    public void Dispose()
    {
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
}
