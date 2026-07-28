using System.Text;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;
using Xunit.Sdk;

namespace CodexHistorySync.Core.Tests.Sync;

public sealed class BackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codex-history-sync-backups-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAndRestoreAsync_VerifiesAndRestoresOriginalBytes()
    {
        var fixture = CreateFixture();
        var original = Path.Combine(fixture.Paths.Sessions, "chat.jsonl");
        await WriteAsync(original, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"chat\"}}\n");
        var expected = await File.ReadAllBytesAsync(original);

        var backup = await fixture.Store.CreateAsync(original, "replace-1", CancellationToken.None);
        await File.WriteAllTextAsync(original, "changed");
        await fixture.Store.RestoreAsync(backup.Id, CancellationToken.None);

        Assert.Equal(expected, await File.ReadAllBytesAsync(original));
        Assert.Equal(Path.GetFullPath(original), backup.OriginalPath);
        Assert.True(File.Exists(backup.ContentPath));
    }

    [Fact]
    public async Task RestoreAsync_RejectsTamperedBackupBeforeMutatingDestination()
    {
        var fixture = CreateFixture();
        var original = Path.Combine(fixture.Paths.Sessions, "chat.jsonl");
        await WriteAsync(original, "original\n");
        var backup = await fixture.Store.CreateAsync(original, "replace-1", CancellationToken.None);
        await File.WriteAllTextAsync(backup.ContentPath, "tampered");
        await File.WriteAllTextAsync(original, "current");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.RestoreAsync(backup.Id, CancellationToken.None));

        Assert.Equal("current", await File.ReadAllTextAsync(original));
    }

    [Fact]
    public async Task RestoreAsync_WhenReplacingCurrentFile_BackupsCurrentBytesFirst()
    {
        var fixture = CreateFixture();
        var original = Path.Combine(fixture.Paths.Sessions, "chat.jsonl");
        await WriteAsync(original, "original\n");
        var restorePoint = await fixture.Store.CreateAsync(original, "initial-point", CancellationToken.None);
        await File.WriteAllTextAsync(original, "current-before-restore");

        await fixture.Store.RestoreAsync(restorePoint.Id, CancellationToken.None);

        var backups = await fixture.Store.ListAsync(CancellationToken.None);
        var replacedVersion = Assert.Single(backups, item => item.OperationId.StartsWith("restore-", StringComparison.Ordinal));
        Assert.Equal("current-before-restore", await File.ReadAllTextAsync(replacedVersion.ContentPath));
    }

    [Fact]
    public async Task RestoreAsync_WhenDestinationChangesAfterSafetyBackup_PreservesConcurrentBytes()
    {
        var fileSystem = new MutatingReplaceFileSystem();
        var fixture = CreateFixture(fileSystem: fileSystem);
        var original = Path.Combine(fixture.Paths.Sessions, "chat.jsonl");
        await WriteAsync(original, "restore-point\n");
        var restorePoint = await fixture.Store.CreateAsync(original, "initial-point", CancellationToken.None);
        await File.WriteAllTextAsync(original, "current-before-restore");
        fileSystem.Destination = original;

        await Assert.ThrowsAsync<IOException>(() => fixture.Store.RestoreAsync(restorePoint.Id, CancellationToken.None));

        Assert.Equal("concurrent-writer", await File.ReadAllTextAsync(original));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(original)!, "*.tmp"));
    }

    [Fact]
    public async Task PruneAsync_UsesInjectedClockAndThirtyDayDefault()
    {
        var fixture = CreateFixture(new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero));
        var old = Path.Combine(fixture.Paths.Sessions, "old.jsonl");
        await WriteAsync(old, "old\n");
        var oldBackup = await fixture.Store.CreateAsync(old, "old-operation", CancellationToken.None);
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 8, 28, 12, 0, 1, TimeSpan.Zero));
        var recent = Path.Combine(fixture.Paths.Sessions, "recent.jsonl");
        await WriteAsync(recent, "recent\n");
        var recentBackup = await fixture.Store.CreateAsync(recent, "recent-operation", CancellationToken.None);

        var removed = await fixture.Store.PruneAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(oldBackup.DirectoryPath));
        Assert.True(Directory.Exists(recentBackup.DirectoryPath));
    }

    [Fact]
    public void Constructor_RejectsBackupRootInsideOrLookalikeEscapingCodexPaths()
    {
        var home = Path.Combine(_root, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);

        Assert.Throws<ArgumentException>(() => new BackupStore("repo", Path.Combine(paths.Sessions, "backups"), paths));
        var safeLookalike = Path.Combine(_root, "codex-other");
        var store = new BackupStore("repo", safeLookalike, paths);
        Assert.StartsWith(Path.GetFullPath(safeLookalike), store.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListAsync_RejectsSymbolicLinkRecordDirectory()
    {
        var fixture = CreateFixture();
        var original = Path.Combine(fixture.Paths.Sessions, "linked-record.jsonl");
        await WriteAsync(original, "linked\n");
        var backup = await fixture.Store.CreateAsync(original, "linked-record", CancellationToken.None);
        var outside = Path.Combine(_root, "outside-backup-record");
        Directory.Move(backup.DirectoryPath, outside);
        try { Directory.CreateSymbolicLink(backup.DirectoryPath, outside); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic-link creation is unavailable: {exception.GetType().Name}");
        }

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WhenPrimaryAndStagingCleanupFail_ExposesEncryptedBackupEvidence()
    {
        var home = Path.Combine(_root, "cleanup-codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        var original = Path.Combine(paths.Sessions, "cleanup.jsonl");
        await WriteAsync(original, "cleanup\n");
        var store = new BackupStore(
            "repo-cleanup", Path.Combine(_root, "cleanup-local"), paths,
            new AtomicFileSystem(), new TestTimeProvider(DateTimeOffset.UtcNow), retention: null,
            new FailingStagingDirectoryCleaner());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<AtomicMutationException>(() =>
            store.CreateAsync(original, "cleanup-failure", cancellation.Token));

        var staging = Assert.Single(error.PreservedPaths);
        Assert.True(Directory.Exists(staging));
        Assert.EndsWith(".tmp", staging, StringComparison.Ordinal);
        Assert.IsType<AggregateException>(error.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Fixture CreateFixture(DateTimeOffset? now = null, IAtomicFileSystem? fileSystem = null)
    {
        var home = Path.Combine(_root, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        var clock = new TestTimeProvider(now ?? DateTimeOffset.UtcNow);
        return new Fixture(paths, clock, new BackupStore("repo", Path.Combine(_root, "local"), paths, fileSystem, clock));
    }

    private static async Task WriteAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
    }

    private sealed record Fixture(CodexPaths Paths, TestTimeProvider Clock, BackupStore Store);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTimeOffset now) => _now = now;
    }

    private sealed class MutatingReplaceFileSystem : IAtomicFileSystem
    {
        private readonly AtomicFileSystem _inner = new();
        public string Destination { get; set; } = string.Empty;
        public Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct) => _inner.WriteTemporaryAsync(path, content, ct);
        public async Task PublishAsync(string temporaryPath, string destinationPath, ContentHash expectedSourceHash, ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Destination, "concurrent-writer", ct);
            await _inner.PublishAsync(temporaryPath, destinationPath, expectedSourceHash, expectedDestinationHash, mutationAllowed, ct);
        }
        public Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct) => _inner.ReplaceAsync(temporaryPath, destinationPath, ct);
        public async Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Destination, "concurrent-writer", ct);
            return await _inner.ReplaceIfUnchangedAsync(temporaryPath, destinationPath, expectedDestinationHash, mutationAllowed, ct);
        }
        public Task DeleteAsync(string path, CancellationToken ct) => _inner.DeleteAsync(path, ct);
        public Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct) => _inner.DeleteIfUnchangedAsync(path, expectedHash, mutationAllowed, ct);
    }

    private sealed class FailingStagingDirectoryCleaner : IStagingDirectoryCleaner
    {
        public void Delete(string path) => throw new IOException("Injected staging cleanup failure.");
    }
}
