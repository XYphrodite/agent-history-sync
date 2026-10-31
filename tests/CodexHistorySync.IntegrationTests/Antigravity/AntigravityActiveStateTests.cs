using CodexHistorySync.Cli;
using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.IntegrationTests.Antigravity;

public sealed class AntigravityActiveStateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AntigravityPaths _paths;

    public AntigravityActiveStateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "antigravity-active-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "conversations"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "presence"));
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
    public async Task PresenceLock_WhenExclusiveLockHeld_MarksSessionActive()
    {
        var sessionId = Guid.NewGuid().ToString();
        var lockFile = Path.Combine(_paths.Home, "presence", $"{sessionId}.lock");
        File.WriteAllText(lockFile, "lock");

        var lockedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lockFile };
        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            isNamedProcessRunning: (_, _) => false,
            isExclusiveLockHeld: path => lockedFiles.Contains(path),
            enumerateFiles: Directory.GetFiles,
            readAllText: path => File.Exists(path) ? File.ReadAllText(path) : null,
            antigravityPaths: _paths);

        var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
        Assert.Contains(sessionId, activeIds);
        Assert.True(await activeState.IsActiveAsync(ManagedAgent.Antigravity, sessionId, "unused", CancellationToken.None));
    }

    [Fact]
    public async Task PresenceLock_WhenUnlocked_DoesNotMarkActive()
    {
        var sessionId = Guid.NewGuid().ToString();
        var lockFile = Path.Combine(_paths.Home, "presence", $"{sessionId}.lock");
        File.WriteAllText(lockFile, "lock");

        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            isNamedProcessRunning: (_, _) => false,
            isExclusiveLockHeld: _ => false,
            enumerateFiles: Directory.GetFiles,
            readAllText: path => File.Exists(path) ? File.ReadAllText(path) : null,
            antigravityPaths: _paths);

        var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
        Assert.DoesNotContain(sessionId, activeIds);
        Assert.False(await activeState.IsActiveAsync(ManagedAgent.Antigravity, sessionId, "unused", CancellationToken.None));
    }

    [Fact]
    public async Task PresenceLock_InvalidSessionId_Ignored()
    {
        var lockFile = Path.Combine(_paths.Home, "presence", "invalid_session.lock");
        File.WriteAllText(lockFile, "lock");

        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            isNamedProcessRunning: (_, _) => false,
            isExclusiveLockHeld: _ => true,
            enumerateFiles: Directory.GetFiles,
            readAllText: path => File.Exists(path) ? File.ReadAllText(path) : null,
            antigravityPaths: _paths);

        var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
        Assert.Empty(activeIds);
    }

    [Fact]
    public async Task DbWal_WhenExclusiveLockHeld_MarksSessionActive()
    {
        var sessionId = Guid.NewGuid().ToString();
        var walFile = Path.Combine(_paths.ConversationsDirectory, $"{sessionId}.db-wal");
        File.WriteAllText(walFile, "wal");

        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            isNamedProcessRunning: (_, _) => false,
            isExclusiveLockHeld: path => string.Equals(path, walFile, StringComparison.OrdinalIgnoreCase),
            enumerateFiles: Directory.GetFiles,
            readAllText: path => File.Exists(path) ? File.ReadAllText(path) : null,
            antigravityPaths: _paths);

        var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
        Assert.Contains(sessionId, activeIds);
        Assert.True(await activeState.IsActiveAsync(ManagedAgent.Antigravity, sessionId, "unused", CancellationToken.None));
    }

    [Fact]
    public async Task DbWal_WhenProcessRunningAndRecentWrite_MarksSessionActive()
    {
        var sessionId = Guid.NewGuid().ToString();
        var walFile = Path.Combine(_paths.ConversationsDirectory, $"{sessionId}.db-wal");
        File.WriteAllText(walFile, "wal");

        var now = DateTime.UtcNow;
        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            isNamedProcessRunning: (_, _) => false,
            isExclusiveLockHeld: _ => false,
            enumerateFiles: Directory.GetFiles,
            readAllText: path => File.Exists(path) ? File.ReadAllText(path) : null,
            claudePaths: null,
            isAnyNamedProcessRunning: name => name == "antigravity",
            lastWriteTimeUtc: _ => now.AddMinutes(-2), // 2 minutes ago (< 5 min)
            utcNow: () => now,
            antigravityPaths: _paths);

        var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
        Assert.Contains(sessionId, activeIds);
        Assert.True(await activeState.IsActiveAsync(ManagedAgent.Antigravity, sessionId, "unused", CancellationToken.None));
    }

    [Fact]
    public async Task DbWal_WhenProcessRunningAndOldWrite_DoesNotMarkActive()
    {
        var sessionId = Guid.NewGuid().ToString();
        var walFile = Path.Combine(_paths.ConversationsDirectory, $"{sessionId}.db-wal");
        File.WriteAllText(walFile, "wal");

        var now = DateTime.UtcNow;
        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            isNamedProcessRunning: (_, _) => false,
            isExclusiveLockHeld: _ => false,
            enumerateFiles: Directory.GetFiles,
            readAllText: path => File.Exists(path) ? File.ReadAllText(path) : null,
            claudePaths: null,
            isAnyNamedProcessRunning: name => name == "antigravity",
            lastWriteTimeUtc: _ => now.AddMinutes(-10), // 10 minutes ago (> 5 min)
            utcNow: () => now,
            antigravityPaths: _paths);

        var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
        Assert.DoesNotContain(sessionId, activeIds);
    }

    [Fact]
    public async Task DbWal_WhenProcessNotRunningAndRecentWrite_DoesNotMarkActive()
    {
        var sessionId = Guid.NewGuid().ToString();
        var walFile = Path.Combine(_paths.ConversationsDirectory, $"{sessionId}.db-wal");
        File.WriteAllText(walFile, "wal");

        var now = DateTime.UtcNow;
        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            isNamedProcessRunning: (_, _) => false,
            isExclusiveLockHeld: _ => false,
            enumerateFiles: Directory.GetFiles,
            readAllText: path => File.Exists(path) ? File.ReadAllText(path) : null,
            claudePaths: null,
            isAnyNamedProcessRunning: _ => false, // Process not running
            lastWriteTimeUtc: _ => now.AddMinutes(-1),
            utcNow: () => now,
            antigravityPaths: _paths);

        var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
        Assert.DoesNotContain(sessionId, activeIds);
    }

    [Fact]
    public async Task DbFile_WhenExclusiveLockHeld_MarksSessionActive()
    {
        var sessionId = Guid.NewGuid().ToString();
        var dbFile = Path.Combine(_paths.ConversationsDirectory, $"{sessionId}.db");
        File.WriteAllText(dbFile, "db");

        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            isNamedProcessRunning: (_, _) => false,
            isExclusiveLockHeld: path => string.Equals(path, dbFile, StringComparison.OrdinalIgnoreCase),
            enumerateFiles: Directory.GetFiles,
            readAllText: path => File.Exists(path) ? File.ReadAllText(path) : null,
            antigravityPaths: _paths);

        var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
        Assert.Contains(sessionId, activeIds);
        Assert.True(await activeState.IsActiveAsync(ManagedAgent.Antigravity, sessionId, "unused", CancellationToken.None));
    }

    [Fact]
    public async Task RealWindowsLock_FileStreamExclusive_DetectedByActiveState()
    {
        var sessionId = Guid.NewGuid().ToString();
        var lockFile = Path.Combine(_paths.Home, "presence", $"{sessionId}.lock");
        File.WriteAllText(lockFile, "presence");

        // Use public constructor which invokes real IsExclusiveLockHeld
        var activeState = new WindowsManagedSessionActiveState(
            codexPaths: null,
            grokPaths: null,
            antigravityPaths: _paths);

        // Before locking: not active
        Assert.False(await activeState.IsActiveAsync(ManagedAgent.Antigravity, sessionId, "unused", CancellationToken.None));

        // Hold an exclusive lock using FileStream with FileShare.None
        using (var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(await activeState.IsActiveAsync(ManagedAgent.Antigravity, sessionId, "unused", CancellationToken.None));
            var activeIds = await activeState.GetActiveSessionIdsAsync(ManagedAgent.Antigravity, CancellationToken.None);
            Assert.Contains(sessionId, activeIds);
        }

        // After releasing lock: not active
        Assert.False(await activeState.IsActiveAsync(ManagedAgent.Antigravity, sessionId, "unused", CancellationToken.None));
    }
}
