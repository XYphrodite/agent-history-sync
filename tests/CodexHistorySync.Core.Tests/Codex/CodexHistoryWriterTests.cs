using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Tests.Codex;

public sealed class CodexHistoryWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codex-history-sync-writer-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImportAsync_WhenAtomicReplaceFails_PreservesOriginalCreatesBackupAndCleansTemporary()
    {
        var fixture = CreateFixture(new FailingReplaceFileSystem());
        var path = await WriteSessionAsync(fixture.Paths, "chat.jsonl", "chat", "old");
        var original = await File.ReadAllBytesAsync(path);
        var incoming = Session("chat", "new");

        await Assert.ThrowsAsync<IOException>(() => fixture.Writer.ImportAsync(Object(path, incoming), new MemoryStream(incoming), "import-1", CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.EnumerateFiles(fixture.Backups.RootPath, "manifest.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ImportAsync_RejectsIncompleteOrInvalidUtf8JsonlWithoutChangingDestinationOrCreatingBackup()
    {
        var fixture = CreateFixture(new AtomicFileSystem());
        var path = await WriteSessionAsync(fixture.Paths, "chat.jsonl", "chat", "old");
        var original = await File.ReadAllBytesAsync(path);
        var invalid = new byte[] { (byte)'{', 0xff, (byte)'}', (byte)'\n' };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Writer.ImportAsync(Object(path, invalid), new MemoryStream(invalid), "import-invalid", CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.False(Directory.Exists(fixture.Backups.RootPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task ImportAsync_RechecksCodexImmediatelyBeforeReplace()
    {
        var detector = new SequencedProcessDetector(false, true);
        var fixture = CreateFixture(new AtomicFileSystem(), detector);
        var path = await WriteSessionAsync(fixture.Paths, "chat.jsonl", "chat", "old");
        var original = await File.ReadAllBytesAsync(path);
        var incoming = Session("chat", "new");

        await Assert.ThrowsAsync<CodexBecameActiveException>(() => fixture.Writer.ImportAsync(
            Object(path, incoming), new MemoryStream(incoming), "import-running", CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, detector.CheckCount);
        Assert.Single(Directory.EnumerateFiles(fixture.Backups.RootPath, "manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImportAsync_WhenDestinationChangesAfterBackup_PreservesConcurrentBytes()
    {
        var fileSystem = new MutatingReplaceFileSystem();
        var fixture = CreateFixture(fileSystem);
        var path = await WriteSessionAsync(fixture.Paths, "chat.jsonl", "chat", "old");
        var incoming = Session("chat", "remote");
        fileSystem.Destination = path;

        await Assert.ThrowsAsync<IOException>(() => fixture.Writer.ImportAsync(Object(path, incoming), new MemoryStream(incoming), "import-race", CancellationToken.None));

        Assert.Equal("concurrent-writer", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task ImportAsync_WhenDestinationChangedSincePlan_ReturnsConflictAndPreservesConcurrentBytes()
    {
        var fixture = CreateFixture(new AtomicFileSystem());
        var path = await WriteSessionAsync(fixture.Paths, "chat.jsonl", "chat", "planned");
        var planned = new ContentHash(Hash(await File.ReadAllBytesAsync(path)));
        var concurrent = Session("chat", "concurrent");
        await File.WriteAllBytesAsync(path, concurrent);
        var incoming = Session("chat", "remote");

        var result = await fixture.Writer.ImportAsync(Object(path, incoming), new MemoryStream(incoming), "import-planned-race",
            ExpectedHistoryState.Present(planned), CancellationToken.None);

        Assert.Equal(ImportApplyResult.Conflict, result);
        Assert.Equal(concurrent, await File.ReadAllBytesAsync(path));
        Assert.False(Directory.Exists(fixture.Backups.RootPath));
    }

    [Fact]
    public async Task ApplyTombstoneAsync_ChangedSinceBaselineReturnsConflictWithoutBackupOrDelete()
    {
        var fixture = CreateFixture(new AtomicFileSystem());
        var path = await WriteSessionAsync(fixture.Paths, "chat.jsonl", "chat", "changed");
        var current = await File.ReadAllBytesAsync(path);

        var result = await fixture.Writer.ApplyTombstoneAsync(Object(path, current), new ContentHash(Hash(Session("chat", "baseline"))), "delete-1", CancellationToken.None);

        Assert.Equal(TombstoneApplyResult.Conflict, result);
        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(fixture.Backups.RootPath));
    }

    [Fact]
    public async Task ApplyTombstoneAsync_UnchangedCreatesVerifiedBackupBeforeDelete()
    {
        var fixture = CreateFixture(new AtomicFileSystem());
        var path = await WriteSessionAsync(fixture.Paths, "chat.jsonl", "chat", "baseline");
        var current = await File.ReadAllBytesAsync(path);

        var result = await fixture.Writer.ApplyTombstoneAsync(Object(path, current), new ContentHash(Hash(current)), "delete-1", CancellationToken.None);

        Assert.Equal(TombstoneApplyResult.Applied, result);
        Assert.False(File.Exists(path));
        var backup = Assert.Single(await fixture.Backups.ListAsync(CancellationToken.None));
        Assert.Equal(current, await File.ReadAllBytesAsync(backup.ContentPath));
    }

    [Fact]
    public async Task ApplyTombstoneAsync_WhenDestinationChangesAtDelete_ReturnsConflictAndPreservesConcurrentBytes()
    {
        var fileSystem = new MutatingDeleteFileSystem();
        var fixture = CreateFixture(fileSystem);
        var path = await WriteSessionAsync(fixture.Paths, "chat.jsonl", "chat", "baseline");
        var baseline = await File.ReadAllBytesAsync(path);
        fileSystem.Destination = path;

        var result = await fixture.Writer.ApplyTombstoneAsync(Object(path, baseline), new ContentHash(Hash(baseline)), "delete-race", CancellationToken.None);

        Assert.Equal(TombstoneApplyResult.Conflict, result);
        Assert.Equal("concurrent-writer", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ImportAsync_RejectsTraversalAndLookalikePrefixDestinations()
    {
        var fixture = CreateFixture(new AtomicFileSystem());
        var incoming = Session("chat", "new");
        var escaped = Path.Combine(fixture.Paths.Sessions, "..", "outside.jsonl");
        var lookalike = fixture.Paths.Sessions + "-evil" + Path.DirectorySeparatorChar + "outside.jsonl";

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Writer.ImportAsync(Object(escaped, incoming), new MemoryStream(incoming), "escape", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Writer.ImportAsync(Object(lookalike, incoming), new MemoryStream(incoming), "lookalike", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_WhenStagingChangesInsideAtomicBoundary_RejectsAndCleansTemporary(bool existingDestination)
    {
        var hooks = new ActionAtomicHooks { AfterSourceHash = path => File.WriteAllText(path, "tampered") };
        var fixture = CreateFixture(new AtomicFileSystem(hooks));
        var path = Path.Combine(fixture.Paths.Sessions, "staged-race.jsonl");
        Directory.CreateDirectory(fixture.Paths.Sessions);
        var original = Session("chat", "original");
        if (existingDestination) await File.WriteAllBytesAsync(path, original);
        var incoming = Session("chat", "incoming");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Writer.ImportAsync(Object(path, incoming), new MemoryStream(incoming), "stage-race", CancellationToken.None));

        if (existingDestination) Assert.Equal(original, await File.ReadAllBytesAsync(path));
        else Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.Sessions, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_WhenCodexStartsAfterAtomicHash_DoesNotPublish(bool existingDestination)
    {
        var detector = new MutableProcessDetector();
        var hooks = new ActionAtomicHooks { AfterSourceHash = _ => detector.Running = true };
        var fixture = CreateFixture(new AtomicFileSystem(hooks), detector);
        var path = Path.Combine(fixture.Paths.Sessions, "process-race.jsonl");
        Directory.CreateDirectory(fixture.Paths.Sessions);
        var original = Session("chat", "original");
        if (existingDestination) await File.WriteAllBytesAsync(path, original);
        var incoming = Session("chat", "incoming");

        await Assert.ThrowsAsync<CodexBecameActiveException>(() => fixture.Writer.ImportAsync(
            Object(path, incoming), new MemoryStream(incoming), "process-race", CancellationToken.None));

        if (existingDestination) Assert.Equal(original, await File.ReadAllBytesAsync(path));
        else Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ApplyTombstoneAsync_WhenCodexStartsAfterAtomicHash_DoesNotDelete()
    {
        var detector = new MutableProcessDetector();
        var hooks = new ActionAtomicHooks { AfterDestinationHash = _ => detector.Running = true };
        var fixture = CreateFixture(new AtomicFileSystem(hooks), detector);
        var path = await WriteSessionAsync(fixture.Paths, "delete-process-race.jsonl", "chat", "original");
        var original = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<CodexBecameActiveException>(() => fixture.Writer.ApplyTombstoneAsync(
            Object(path, original), new ContentHash(Hash(original)), "delete-process-race", CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Fixture CreateFixture(IAtomicFileSystem fileSystem, ICodexProcessDetector? detector = null)
    {
        var home = Path.Combine(_root, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        var backups = new BackupStore("repo", Path.Combine(_root, "local"), paths, fileSystem);
        return new Fixture(paths, backups, new CodexHistoryWriter(paths, backups, detector ?? new SequencedProcessDetector(false, false), fileSystem));
    }

    private static async Task<string> WriteSessionAsync(CodexPaths paths, string name, string id, string side)
    {
        var path = Path.Combine(paths.Sessions, name);
        Directory.CreateDirectory(paths.Sessions);
        await File.WriteAllBytesAsync(path, Session(id, side));
        return path;
    }

    private static byte[] Session(string id, string side) => Encoding.UTF8.GetBytes($"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}},\"side\":\"{side}\"}}\n");
    private static LocalObject Object(string path, byte[] bytes) => new(new LogicalObjectId("chat"), ObjectKind.ActiveSession, path, new ContentHash(Hash(bytes)), bytes.Length, DateTimeOffset.UtcNow);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Fixture(CodexPaths Paths, BackupStore Backups, CodexHistoryWriter Writer);

    private sealed class SequencedProcessDetector(params bool[] results) : ICodexProcessDetector
    {
        private int _index;
        public int CheckCount { get; private set; }
        public bool IsRunning()
        {
            CheckCount++;
            return results[Math.Min(_index++, results.Length - 1)];
        }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MutableProcessDetector : ICodexProcessDetector
    {
        public bool Running { get; set; }
        public bool IsRunning() => Running;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ActionAtomicHooks : IAtomicFileSystemHooks
    {
        public Action<string>? AfterSourceHash { get; init; }
        public Action<string>? AfterDestinationHash { get; init; }
        void IAtomicFileSystemHooks.OnAfterSourceHash(string path) => AfterSourceHash?.Invoke(path);
        void IAtomicFileSystemHooks.OnAfterDestinationHash(string path) => AfterDestinationHash?.Invoke(path);
        void IAtomicFileSystemHooks.OnAfterDeleteCapture(string quarantinePath, string destinationPath) { }
        void IAtomicFileSystemHooks.OnBeforeArtifactCleanup(string path) { }
        void IAtomicFileSystemHooks.OnBeforeMutationPathValidation(string path) { }
        void IAtomicFileSystemHooks.OnAfterPublishMutation(string destinationPath) { }
    }

    private sealed class FailingReplaceFileSystem : IAtomicFileSystem
    {
        private readonly AtomicFileSystem _inner = new();
        public Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct) => _inner.WriteTemporaryAsync(path, content, ct);
        public Task PublishAsync(string temporaryPath, string destinationPath, ContentHash expectedSourceHash, ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) => throw new IOException("Injected replacement failure.");
        public Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct) => throw new IOException("Injected replacement failure.");
        public Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) => throw new IOException("Injected replacement failure.");
        public Task DeleteAsync(string path, CancellationToken ct) => _inner.DeleteAsync(path, ct);
        public Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct) => _inner.DeleteIfUnchangedAsync(path, expectedHash, mutationAllowed, ct);
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
        public async Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Destination, "concurrent-writer", ct);
            await _inner.ReplaceAsync(temporaryPath, destinationPath, ct);
        }
        public async Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Destination, "concurrent-writer", ct);
            return await _inner.ReplaceIfUnchangedAsync(temporaryPath, destinationPath, expectedDestinationHash, mutationAllowed, ct);
        }
        public Task DeleteAsync(string path, CancellationToken ct) => _inner.DeleteAsync(path, ct);
        public Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct) => _inner.DeleteIfUnchangedAsync(path, expectedHash, mutationAllowed, ct);
    }

    private sealed class MutatingDeleteFileSystem : IAtomicFileSystem
    {
        private readonly AtomicFileSystem _inner = new();
        public string Destination { get; set; } = string.Empty;
        public Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct) => _inner.WriteTemporaryAsync(path, content, ct);
        public Task PublishAsync(string temporaryPath, string destinationPath, ContentHash expectedSourceHash, ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) => _inner.PublishAsync(temporaryPath, destinationPath, expectedSourceHash, expectedDestinationHash, mutationAllowed, ct);
        public Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct) => _inner.ReplaceAsync(temporaryPath, destinationPath, ct);
        public Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) => _inner.ReplaceIfUnchangedAsync(temporaryPath, destinationPath, expectedDestinationHash, mutationAllowed, ct);
        public async Task DeleteAsync(string path, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Destination, "concurrent-writer", ct);
            await _inner.DeleteAsync(path, ct);
        }
        public async Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct)
        {
            await File.WriteAllTextAsync(Destination, "concurrent-writer", ct);
            return await _inner.DeleteIfUnchangedAsync(path, expectedHash, mutationAllowed, ct);
        }
    }
}
