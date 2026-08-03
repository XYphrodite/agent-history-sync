using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;

namespace CodexHistorySync.IntegrationTests;

public sealed class RecoveryDrillTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-recovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task Divergence_export_resolution_interruption_and_backup_restore_preserve_expected_hashes()
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        await RunGitAsync(root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        try
        {
            var first = CreateDevice("first", remote, key);
            var second = CreateDevice("second", remote, key);
            await WriteSessionAsync(first.Paths.Sessions, "shared", "baseline");
            await first.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
            await second.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

            var firstPath = Path.Combine(first.Paths.Sessions, "shared.jsonl");
            var secondPath = Path.Combine(second.Paths.Sessions, "shared.jsonl");
            await File.AppendAllTextAsync(firstPath, Message("remote-version"));
            await File.AppendAllTextAsync(secondPath, Message("local-version"));
            var remoteHash = Hash(await File.ReadAllBytesAsync(firstPath));
            var localHash = Hash(await File.ReadAllBytesAsync(secondPath));
            await first.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
            Assert.Equal(1, (await second.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None)).Conflicts);
            var conflict = Assert.Single(await second.Conflicts.ListAsync(CancellationToken.None));

            var export = Path.Combine(root, "exported-conflict");
            var exported = await second.Engine.ResolveConflictAsync(conflict.Id, ConflictResolution.ExportBoth,
                export, CancellationToken.None);
            Assert.True(exported.Exported);
            Assert.Equal(new[] { localHash, remoteHash }.Order(),
                Directory.EnumerateFiles(export, "*.jsonl").Select(path => Hash(File.ReadAllBytes(path))).Order());

            await second.Engine.ResolveConflictAsync(conflict.Id, ConflictResolution.KeepRemote, null, CancellationToken.None);
            Assert.Equal(remoteHash, Hash(await File.ReadAllBytesAsync(secondPath)));
            var restorePoint = await second.Backups.CreateAsync(secondPath, "recovery-drill", CancellationToken.None);
            var stateBefore = await File.ReadAllBytesAsync(second.State.GetStatePath("repository"));

            await first.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
            await File.AppendAllTextAsync(firstPath, Message("new-remote-after-resolution"));
            await first.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
            var failing = CreateDevice("second", remote, key, new DiskFullFileSystem(), new FailingCleaner());
            var error = await Assert.ThrowsAsync<IOException>(() =>
                failing.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));
            Assert.Contains("disk full", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cleanup", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(remoteHash, Hash(await File.ReadAllBytesAsync(secondPath)));
            Assert.Equal(stateBefore, await File.ReadAllBytesAsync(second.State.GetStatePath("repository")));

            await File.WriteAllTextAsync(secondPath, "operator-corruption");
            await second.Backups.RestoreAsync(restorePoint.Id, CancellationToken.None);
            Assert.Equal(remoteHash, Hash(await File.ReadAllBytesAsync(secondPath)));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private Device CreateDevice(string name, string remote, byte[] key, IAtomicFileSystem? fileSystem = null,
        IOperationDirectoryCleaner? cleaner = null)
    {
        var home = Path.Combine(root, name, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        Directory.CreateDirectory(paths.Sessions);
        var local = Path.Combine(root, name, "local");
        var state = new LocalStateStore(local);
        var backups = new BackupStore("repository", local, paths, fileSystem);
        var conflicts = new ConflictStore("repository", local, paths);
        var writer = new CodexHistoryWriter(paths, backups, new StoppedDetector(), fileSystem);
        var provider = new GitStorageProvider("repository", remote, GitRemoteKind.Local,
            Path.Combine(root, name, "provider"));
        var staging = Path.Combine(local, "staging");
        var engine = cleaner is null
            ? new SyncEngine("repository", name, paths, key, new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(),
                state, writer, conflicts, provider, staging)
            : new SyncEngine("repository", name, paths, key, new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(),
                state, writer, conflicts, provider, staging, NoopHooks.Instance, cleaner);
        return new Device(paths, state, backups, conflicts, engine);
    }

    private static async Task WriteSessionAsync(string directory, string id, string text) =>
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".jsonl"),
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}\n" + Message(text), new UTF8Encoding(false));

    private static string Message(string text) => $"{{\"type\":\"message\",\"payload\":{{\"text\":\"{text}\"}}}}\n";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task RunGitAsync(string directory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private sealed record Device(CodexPaths Paths, LocalStateStore State, BackupStore Backups,
        ConflictStore Conflicts, SyncEngine Engine);
    private sealed class StoppedDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class NoopHooks : ISyncEngineHooks
    {
        public static readonly NoopHooks Instance = new();
        public void OnBeforeLocalPublicationPrecondition() { }
    }
    private sealed class FailingCleaner : IOperationDirectoryCleaner
    {
        public void Delete(string operationDirectory, string markerFileName) => throw new IOException("cleanup failed");
    }
    private sealed class DiskFullFileSystem : IAtomicFileSystem
    {
        private readonly AtomicFileSystem inner = new();
        public Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct) =>
            throw new IOException("Injected disk full while staging import.");
        public Task PublishAsync(string temporaryPath, string destinationPath, CodexHistorySync.Core.Model.ContentHash expectedSourceHash,
            CodexHistorySync.Core.Model.ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) =>
            inner.PublishAsync(temporaryPath, destinationPath, expectedSourceHash, expectedDestinationHash, mutationAllowed, ct);
        public Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct) =>
            inner.ReplaceAsync(temporaryPath, destinationPath, ct);
        public Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath,
            CodexHistorySync.Core.Model.ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) =>
            inner.ReplaceIfUnchangedAsync(temporaryPath, destinationPath, expectedDestinationHash, mutationAllowed, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);
        public Task<bool> DeleteIfUnchangedAsync(string path, CodexHistorySync.Core.Model.ContentHash expectedHash,
            Func<bool>? mutationAllowed, CancellationToken ct) => inner.DeleteIfUnchangedAsync(path, expectedHash, mutationAllowed, ct);
    }
}
