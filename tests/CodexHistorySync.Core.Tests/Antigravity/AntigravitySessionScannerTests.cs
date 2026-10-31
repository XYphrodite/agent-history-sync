using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Model;
using Xunit;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravitySessionScannerTests : IDisposable
{
    private readonly AntigravityTestFixture fixture = new();

    public void Dispose()
    {
        fixture.Dispose();
    }

    [Fact]
    public async Task Scanner_PublishesStableSessions_WhenDatabaseIsStable()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "Stable Session", "User input", "Planner response");

        var scanner = new AntigravitySessionScanner(
            waitForStability: _ => Task.CompletedTask,
            isAntigravityRunning: () => false,
            activityWindow: TimeSpan.FromSeconds(30),
            now: () => DateTimeOffset.UtcNow,
            isExclusiveLockHeld: _ => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        var item = Assert.Single(result.Objects);
        Assert.Equal(ObjectKind.AntigravitySession, item.Kind);
        Assert.Equal("ag-" + sessionId, item.Id.Value);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.DoesNotContain(ObjectKind.AntigravitySession, result.UncertainKinds);
    }

    [Fact]
    public async Task Scanner_DefersSession_WhenAntigravityProcessIsRunningAndSessionIsRecentlyActive()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var nowTime = DateTimeOffset.UtcNow;
        fixture.InsertSummaryRow(sessionId, "Live Active Session", updatedAt: nowTime.ToUnixTimeSeconds());
        fixture.InsertUserStep(sessionId, 0, "Current query", timestamp: nowTime);

        // Process is running and active within 30s window
        var scanner = new AntigravitySessionScanner(
            waitForStability: _ => Task.CompletedTask,
            isAntigravityRunning: () => true,
            activityWindow: TimeSpan.FromSeconds(30),
            now: () => nowTime,
            isExclusiveLockHeld: _ => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.Contains(ObjectKind.AntigravitySession, result.UncertainKinds);
    }

    [Fact]
    public async Task Scanner_DefersSession_WhenExclusiveLockIsHeldOnPresenceLockFile()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "Locked by Presence", "Prompt", "Response");

        var presenceDir = Path.Combine(fixture.Home, "presence");
        Directory.CreateDirectory(presenceDir);
        var lockFile = Path.Combine(presenceDir, sessionId + ".lock");
        File.WriteAllText(lockFile, "lock");

        var scanner = new AntigravitySessionScanner(
            waitForStability: _ => Task.CompletedTask,
            isAntigravityRunning: () => false,
            activityWindow: TimeSpan.FromSeconds(30),
            now: () => DateTimeOffset.UtcNow,
            isExclusiveLockHeld: path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(lockFile), StringComparison.OrdinalIgnoreCase));

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.Contains(ObjectKind.AntigravitySession, result.UncertainKinds);
    }

    [Fact]
    public async Task Scanner_DefersSession_WhenExclusiveLockIsHeldOnWalFile()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "Locked by WAL", "Prompt", "Response");

        var walFile = Path.Combine(fixture.Paths.ConversationsDirectory, sessionId + ".db-wal");
        File.WriteAllText(walFile, "wal");

        var scanner = new AntigravitySessionScanner(
            waitForStability: _ => Task.CompletedTask,
            isAntigravityRunning: () => false,
            activityWindow: TimeSpan.FromSeconds(30),
            now: () => DateTimeOffset.UtcNow,
            isExclusiveLockHeld: path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(walFile), StringComparison.OrdinalIgnoreCase));

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.Contains(ObjectKind.AntigravitySession, result.UncertainKinds);
    }

    [Fact]
    public async Task Scanner_DetectsInstability_WhenSessionChangesBetweenPhase1AndPhase2()
    {
        var sessionId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        fixture.BuildStandardSession(sessionId, "Initial Title", "Prompt", "Response");

        var scanner = new AntigravitySessionScanner(
            waitForStability: async _ =>
            {
                // Mutate the session during stability wait
                fixture.InsertSummaryRow(sessionId, "Title Mutated Concurrently");
                await Task.Yield();
            },
            isAntigravityRunning: () => false,
            activityWindow: TimeSpan.FromSeconds(30),
            now: () => DateTimeOffset.UtcNow,
            isExclusiveLockHeld: _ => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.Contains(ObjectKind.AntigravitySession, result.UncertainKinds);
    }

    [Fact]
    public async Task Scanner_ConfirmsAbsence_WhenSummariesDbExistsAndIsEmpty()
    {
        fixture.CreateSummariesDatabase();

        var scanner = new AntigravitySessionScanner(
            waitForStability: _ => Task.CompletedTask,
            isAntigravityRunning: () => false,
            activityWindow: TimeSpan.FromSeconds(30),
            now: () => DateTimeOffset.UtcNow,
            isExclusiveLockHeld: _ => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.DoesNotContain(ObjectKind.AntigravitySession, result.UncertainKinds);
    }

    [Fact]
    public async Task Scanner_MarksUncertain_WhenHomeDoesNotExist()
    {
        var nonExistentHome = Path.Combine(fixture.Root, "nonexistent");
        var nonExistentPaths = new AntigravityPaths(nonExistentHome);

        var scanner = new AntigravitySessionScanner(
            waitForStability: _ => Task.CompletedTask,
            isAntigravityRunning: () => false);

        var result = await scanner.ScanDetailedAsync(nonExistentPaths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.Contains(ObjectKind.AntigravitySession, result.UncertainKinds);
    }

    [Fact]
    public async Task Scanner_MarksUncertain_WhenSummariesDbDoesNotExistAndNoConversationsExist()
    {
        // Home exists, but summaries DB and conversations dir have no db files
        var emptyHome = Path.Combine(fixture.Root, "empty-home");
        Directory.CreateDirectory(emptyHome);
        var emptyPaths = new AntigravityPaths(emptyHome);

        var scanner = new AntigravitySessionScanner(
            waitForStability: _ => Task.CompletedTask,
            isAntigravityRunning: () => false);

        var result = await scanner.ScanDetailedAsync(emptyPaths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.Contains(ObjectKind.AntigravitySession, result.UncertainKinds);
    }

    [Fact]
    public async Task Scanner_MarksUncertain_WhenSummariesDbIsCorrupted()
    {
        File.WriteAllText(fixture.Paths.ConversationSummariesDbPath, "corrupted not a sqlite database");

        var scanner = new AntigravitySessionScanner(
            waitForStability: _ => Task.CompletedTask,
            isAntigravityRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.AntigravitySession));
        Assert.Contains(ObjectKind.AntigravitySession, result.UncertainKinds);
    }
}
