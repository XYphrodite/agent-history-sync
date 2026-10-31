using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityLocalSessionOperationsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AntigravityPaths _paths;

    public AntigravityLocalSessionOperationsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "antigravity-ops-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "conversations"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "annotations"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "brain"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".agent-sync-antigravity"));
        _paths = new AntigravityPaths(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed class NoOpDirectoryDeleter : IManagedSessionDirectoryDeleter
    {
        public Task DeleteAsync(string sessionsRoot, string sessionDirectory, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeActiveState : IManagedSessionActiveState
    {
        public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(ManagedAgent agent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<bool> IsActiveAsync(ManagedAgent agent, string sessionId, string nativePath, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task DeleteAsync_CreatesDurableBackupAndDeletesOriginalFiles()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);
        var annotationPath = _paths.AnnotationPath(sessionId);
        var brainDir = _paths.BrainSessionDirectory(sessionId);
        Directory.CreateDirectory(brainDir);
        var transcript = Path.Combine(brainDir, "transcript.jsonl");
        File.WriteAllText(transcript, "{\"text\":\"data\"}");
        File.WriteAllText(annotationPath, "title: test");

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("Delete test query"))
        };
        AntigravityDatabase.WriteSteps(convDb, "traj-1", sessionId, steps);
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: sessionId,
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "cli",
            Title: "Session To Delete",
            Status: "ACTIVE",
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));

        var anchor = _paths.AnchorPath(sessionId);
        File.WriteAllText(anchor, "{}");

        var ops = new LocalSessionOperations(
            null, null, new FakeActiveState(), new NoOpDirectoryDeleter(), null, null,
            antigravityPaths: _paths);

        var managed = new ManagedSession(
            ManagedAgent.Antigravity,
            sessionId,
            anchor,
            "Session To Delete",
            DateTimeOffset.UtcNow,
            false,
            true);

        await ops.DeleteAsync(managed, CancellationToken.None);

        // Verify original files deleted
        Assert.False(File.Exists(convDb));
        Assert.False(File.Exists(annotationPath));
        Assert.False(Directory.Exists(brainDir));
        Assert.False(File.Exists(anchor));

        // Verify summary row deleted
        var summaries = AntigravityDatabase.ReadSummaries(_paths.ConversationSummariesDbPath);
        Assert.DoesNotContain(summaries, s => s.ConversationId == sessionId);

        // Verify durable backup exists
        var backupDir = Path.Combine(_paths.Home, "backups", sessionId);
        Assert.True(Directory.Exists(backupDir));
        Assert.True(File.Exists(Path.Combine(backupDir, sessionId + ".db")));
        Assert.True(File.Exists(Path.Combine(backupDir, sessionId + ".pbtxt")));
        Assert.True(File.Exists(Path.Combine(backupDir, "brain", "transcript.jsonl")));
    }

    [Fact]
    public async Task DeleteAsync_Durability_WithWalShmAndNestedBrainDir()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);
        var walPath = convDb + "-wal";
        var shmPath = convDb + "-shm";
        var annotationPath = _paths.AnnotationPath(sessionId);
        var brainDir = _paths.BrainSessionDirectory(sessionId);
        var nestedArtifactDir = Path.Combine(brainDir, "artifacts", "sub");
        Directory.CreateDirectory(nestedArtifactDir);

        File.WriteAllText(annotationPath, "annotation content");
        File.WriteAllText(Path.Combine(brainDir, "transcript.jsonl"), "{\"text\":\"line1\"}\n");
        File.WriteAllText(Path.Combine(nestedArtifactDir, "model.bin"), "binary-artifact-payload");

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("Durability Query")),
            new(1, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodePlannerResponse("Durability Response"))
        };
        AntigravityDatabase.WriteSteps(convDb, "traj-durability", sessionId, steps);
        File.WriteAllText(walPath, "fake-wal-data");
        File.WriteAllText(shmPath, "fake-shm-data");

        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: sessionId,
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "cli",
            Title: "Session With Wal And Brain",
            Status: "ACTIVE",
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));

        var anchor = _paths.AnchorPath(sessionId);
        File.WriteAllText(anchor, "{}");

        var ops = new LocalSessionOperations(
            null, null, new FakeActiveState(), new NoOpDirectoryDeleter(), null, null,
            antigravityPaths: _paths);

        var managed = new ManagedSession(
            ManagedAgent.Antigravity,
            sessionId,
            anchor,
            "Session With Wal And Brain",
            DateTimeOffset.UtcNow,
            false,
            true);

        await ops.DeleteAsync(managed, CancellationToken.None);

        // Verify original files deleted
        Assert.False(File.Exists(convDb));
        Assert.False(File.Exists(walPath));
        Assert.False(File.Exists(shmPath));
        Assert.False(File.Exists(annotationPath));
        Assert.False(Directory.Exists(brainDir));
        Assert.False(File.Exists(anchor));

        // Verify durable backup exists and contains all components
        var backupDir = Path.Combine(_paths.Home, "backups", sessionId);
        Assert.True(Directory.Exists(backupDir));

        var backupDb = Path.Combine(backupDir, sessionId + ".db");
        var backupWal = Path.Combine(backupDir, sessionId + ".db-wal");
        var backupShm = Path.Combine(backupDir, sessionId + ".db-shm");
        var backupAnnotation = Path.Combine(backupDir, sessionId + ".pbtxt");
        var backupTranscript = Path.Combine(backupDir, "brain", "transcript.jsonl");
        var backupArtifact = Path.Combine(backupDir, "brain", "artifacts", "sub", "model.bin");

        Assert.True(File.Exists(backupDb));
        Assert.True(File.Exists(backupWal));
        Assert.True(File.Exists(backupShm));
        Assert.True(File.Exists(backupAnnotation));
        Assert.True(File.Exists(backupTranscript));
        Assert.True(File.Exists(backupArtifact));

        Assert.Equal("fake-wal-data", File.ReadAllText(backupWal));
        Assert.NotEmpty(File.ReadAllBytes(backupShm));
        Assert.Equal("binary-artifact-payload", File.ReadAllText(backupArtifact));

        // Delete the temporary fake wal and shm in backup to verify the sqlite db itself is intact
        File.Delete(backupWal);
        File.Delete(backupShm);
        var recoveredSteps = AntigravityDatabase.ReadSteps(backupDb);
        Assert.Equal(2, recoveredSteps.Count);
        Assert.Equal("Durability Query", AntigravityProtobuf.ExtractUserInput(recoveredSteps[0].Payload!));
    }

    [Fact]
    public async Task DeleteAsync_ActiveSession_ThrowsAndDoesNotDelete()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);
        var anchor = _paths.AnchorPath(sessionId);
        File.WriteAllText(anchor, "{}");

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("Active session test"))
        };
        AntigravityDatabase.WriteSteps(convDb, "traj-active", sessionId, steps);
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: sessionId,
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "cli",
            Title: "Active Session",
            Status: "ACTIVE",
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));

        var alwaysActiveState = new AlwaysActiveState();
        var ops = new LocalSessionOperations(
            null, null, alwaysActiveState, new NoOpDirectoryDeleter(), null, null,
            antigravityPaths: _paths);

        var managed = new ManagedSession(
            ManagedAgent.Antigravity,
            sessionId,
            anchor,
            "Active Session",
            DateTimeOffset.UtcNow,
            false,
            true);

        await Assert.ThrowsAsync<ManagedSessionOperationException>(() => ops.DeleteAsync(managed, CancellationToken.None));

        // Original files must still exist!
        Assert.True(File.Exists(convDb));
        Assert.True(File.Exists(anchor));

        var summaries = AntigravityDatabase.ReadSummaries(_paths.ConversationSummariesDbPath);
        Assert.Contains(summaries, s => s.ConversationId == sessionId);
    }

    [Fact]
    public async Task DeleteAsync_OverwritesPreexistingBackup()
    {
        var sessionId = Guid.NewGuid().ToString();
        var convDb = _paths.ConversationDatabasePath(sessionId);
        var anchor = _paths.AnchorPath(sessionId);
        File.WriteAllText(anchor, "{}");

        var steps = new List<AntigravityStepRow>
        {
            new(0, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone,
                AntigravityProtobuf.EncodeUserInput("New query"))
        };
        AntigravityDatabase.WriteSteps(convDb, "traj-new", sessionId, steps);
        AntigravityDatabase.WriteSummary(_paths.ConversationSummariesDbPath, new AntigravitySummaryRow(
            ConversationId: sessionId,
            ParentConversationId: null,
            NestingDepth: 0,
            AgentName: "cli",
            Title: "Preexisting Backup Test",
            Status: "ACTIVE",
            WorkspaceUris: Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow));

        // Pre-create the backup directory with stale content
        var backupDir = Path.Combine(_paths.Home, "backups", sessionId);
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, sessionId + ".db"), "stale corrupted data");

        var ops = new LocalSessionOperations(
            null, null, new FakeActiveState(), new NoOpDirectoryDeleter(), null, null,
            antigravityPaths: _paths);

        var managed = new ManagedSession(
            ManagedAgent.Antigravity,
            sessionId,
            anchor,
            "Preexisting Backup Test",
            DateTimeOffset.UtcNow,
            false,
            true);

        await ops.DeleteAsync(managed, CancellationToken.None);

        // Verification: backup now contains valid sqlite database, not stale string
        var backupDb = Path.Combine(backupDir, sessionId + ".db");
        Assert.True(File.Exists(backupDb));
        var recoveredSteps = AntigravityDatabase.ReadSteps(backupDb);
        Assert.Single(recoveredSteps);
        Assert.Equal("New query", AntigravityProtobuf.ExtractUserInput(recoveredSteps[0].Payload!));
    }

    private sealed class AlwaysActiveState : IManagedSessionActiveState
    {
        public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(ManagedAgent agent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<bool> IsActiveAsync(ManagedAgent agent, string sessionId, string nativePath, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}

