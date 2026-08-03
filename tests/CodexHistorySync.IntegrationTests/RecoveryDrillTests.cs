using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Cli;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using CodexHistorySync.Windows;

namespace CodexHistorySync.IntegrationTests;

public sealed class RecoveryDrillTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-recovery-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(ImportInterruption.AfterStagingAndBackup)]
    [InlineData(ImportInterruption.AtLocalReplace)]
    public async Task Interrupted_import_preserves_live_bytes_baseline_and_durable_recovery_until_startup_cleanup(
        ImportInterruption interruption)
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "interrupted-remote.git");
        await RunGitAsync(root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        try
        {
            var source = CreateDevice("interrupted-source", remote, key);
            var target = CreateDevice("interrupted-target", remote, key);
            await WriteSessionAsync(source.Paths.Sessions, "interrupted-shared", "baseline");
            await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
            await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

            var sourcePath = Path.Combine(source.Paths.Sessions, "interrupted-shared.jsonl");
            var targetPath = Path.Combine(target.Paths.Sessions, "interrupted-shared.jsonl");
            var liveBefore = await File.ReadAllBytesAsync(targetPath);
            var baselinePath = target.State.GetStatePath("repository");
            var baselineBefore = await File.ReadAllBytesAsync(baselinePath);
            await File.AppendAllTextAsync(sourcePath, Message("incoming-after-baseline"));
            await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
            var incoming = await File.ReadAllBytesAsync(sourcePath);
            var beforeHash = new ContentHash(Hash(liveBefore));
            var incomingHash = new ContentHash(Hash(incoming));
            var local = Assert.Single(await new SessionScanner(TimeSpan.Zero)
                .ScanAsync(target.Paths, CancellationToken.None));
            var operationId = "interrupted-" + interruption.ToString().ToLowerInvariant();
            var operationDirectory = Path.Combine(target.StagingRoot, operationId);
            var downloadDirectory = Path.Combine(operationDirectory, "downloads");
            Directory.CreateDirectory(downloadDirectory);
            var stagedDownload = Path.Combine(downloadDirectory, "interrupted-shared.jsonl");
            await File.WriteAllBytesAsync(stagedDownload, incoming);
            var batch = await HistoryMutationBatch.PrepareAsync(target.Writer, operationDirectory, operationId,
                [new HistoryMutationPlan(local, ExpectedHistoryState.Present(beforeHash),
                    ExpectedHistoryState.Present(incomingHash))], CancellationToken.None);
            await batch.BeginApplyAsync(local.Id, CancellationToken.None);

            if (interruption == ImportInterruption.AtLocalReplace)
            {
                var temporary = BackupStore.SiblingTemporaryPath(targetPath);
                var atomic = new AtomicFileSystem();
                try
                {
                    await using var content = new MemoryStream(incoming, writable: false);
                    await atomic.WriteTemporaryAsync(temporary, content, CancellationToken.None);
                    await Assert.ThrowsAsync<OperationCanceledException>(() => atomic.PublishAsync(temporary,
                        targetPath, incomingHash, beforeHash,
                        () => throw new OperationCanceledException("Injected cancellation immediately before local replacement."),
                        CancellationToken.None));
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }

            Assert.Equal(liveBefore, await File.ReadAllBytesAsync(targetPath));
            Assert.Equal(baselineBefore, await File.ReadAllBytesAsync(baselinePath));
            Assert.True(File.Exists(stagedDownload));
            Assert.True(File.Exists(Path.Combine(operationDirectory, HistoryMutationBatch.MarkerFileName)));
            Assert.True(File.Exists(HistoryMutationBatch.CleanupEvidencePath(operationDirectory)));
            var backup = Assert.Single(await target.Backups.ListAsync(CancellationToken.None));
            Assert.Equal(operationId, backup.OperationId);
            Assert.Equal(liveBefore, await File.ReadAllBytesAsync(backup.ContentPath));

            var restarted = CreateDevice("interrupted-target", remote, key, provider: new OfflineProvider());
            var error = await Assert.ThrowsAsync<IOException>(() =>
                restarted.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

            Assert.Contains("offline", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(liveBefore, await File.ReadAllBytesAsync(targetPath));
            Assert.Equal(baselineBefore, await File.ReadAllBytesAsync(baselinePath));
            Assert.False(Directory.Exists(operationDirectory));
            Assert.False(File.Exists(HistoryMutationBatch.CleanupEvidencePath(operationDirectory)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(restarted.StagingRoot));
            Assert.Equal(liveBefore, await File.ReadAllBytesAsync(backup.ContentPath));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task Disposable_two_device_agents_converge_after_interleaved_cycles_restart_and_explicit_conflict_resolution()
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "acceptance-remote.git");
        await RunGitAsync(root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        try
        {
            var first = CreateDevice("acceptance-first", remote, key);
            var second = CreateDevice("acceptance-second", remote, key);
            await WriteSessionAsync(first.Paths.Sessions, "device-a-chat", "created-on-device-a");
            await WriteSessionAsync(second.Paths.Sessions, "device-b-chat", "created-on-device-b");
            var deviceABytes = await File.ReadAllBytesAsync(Path.Combine(first.Paths.Sessions, "device-a-chat.jsonl"));
            var deviceBBytes = await File.ReadAllBytesAsync(Path.Combine(second.Paths.Sessions, "device-b-chat.jsonl"));
            var expectedConvergence = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["device-a-chat"] = Hash(deviceABytes),
                ["device-b-chat"] = Hash(deviceBBytes)
            };

            var firstPublication = await RunStoppedAgentCycleAsync(first);
            var interleavedPublication = await RunStoppedAgentCycleAsync(second);
            var firstConvergence = await RunStoppedAgentCycleAsync(first);

            Assert.Equal((1, 0), (firstPublication.Result.Uploaded, firstPublication.Result.Downloaded));
            Assert.Equal((1, 1), (interleavedPublication.Result.Uploaded, interleavedPublication.Result.Downloaded));
            Assert.Equal((0, 1), (firstConvergence.Result.Uploaded, firstConvergence.Result.Downloaded));
            Assert.Equal(expectedConvergence, await ScanHashesAsync(first));
            Assert.Equal(expectedConvergence, await ScanHashesAsync(second));

            await first.Engine.DisposeAsync();
            await second.Engine.DisposeAsync();
            first = CreateDevice("acceptance-first", remote, key);
            second = CreateDevice("acceptance-second", remote, key);
            var firstRestart = await RunStoppedAgentCycleAsync(first);
            var secondRestart = await RunStoppedAgentCycleAsync(second);

            Assert.Equal((0, 0), (firstRestart.Result.Uploaded, firstRestart.Result.Downloaded));
            Assert.Equal((0, 0), (secondRestart.Result.Uploaded, secondRestart.Result.Downloaded));
            Assert.Equal(expectedConvergence, await ScanHashesAsync(first));
            Assert.Equal(expectedConvergence, await ScanHashesAsync(second));

            var firstSharedPath = Path.Combine(first.Paths.Sessions, "device-a-chat.jsonl");
            var secondSharedPath = Path.Combine(second.Paths.Sessions, "device-a-chat.jsonl");
            await File.AppendAllTextAsync(firstSharedPath, Message("same-chat-change-on-device-a"));
            await File.AppendAllTextAsync(secondSharedPath, Message("same-chat-change-on-device-b"));
            var remoteVersion = await File.ReadAllBytesAsync(firstSharedPath);
            var localVersion = await File.ReadAllBytesAsync(secondSharedPath);
            var remoteHash = new ContentHash(Hash(remoteVersion));
            var localHash = new ContentHash(Hash(localVersion));
            var baselineHash = new ContentHash(Hash(deviceABytes));

            Assert.Equal(1, (await RunStoppedAgentCycleAsync(first)).Result.Uploaded);
            var conflictCycle = await RunStoppedAgentCycleAsync(second);

            Assert.Equal(1, conflictCycle.Result.Conflicts);
            Assert.Contains(conflictCycle.Notifications,
                item => item.Kind == AgentNotificationKind.UnresolvedConflict && item.Count == 1);
            Assert.Equal(localVersion, await File.ReadAllBytesAsync(secondSharedPath));
            var conflict = Assert.Single(await second.Conflicts.ListAsync(CancellationToken.None));
            Assert.Equal("device-a-chat", conflict.Provenance.Metadata.ObjectId.Value);
            Assert.Equal(localHash, conflict.Provenance.LocalHash);
            Assert.Equal(remoteHash, conflict.Provenance.RemoteHash);
            Assert.Equal(baselineHash, conflict.Provenance.BaselineHash);
            Assert.Equal("acceptance-second", conflict.Provenance.LocalDeviceId);
            Assert.Equal("acceptance-first", conflict.Provenance.RemoteDeviceId);
            Assert.Equal(["local.encrypted", "manifest.json", "remote.encrypted"],
                Directory.EnumerateFiles(conflict.DirectoryPath).Select(path => Path.GetFileName(path)!)
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray());

            var export = Path.Combine(root, "acceptance-conflict-evidence");
            var exported = await second.Engine.ResolveConflictAsync(conflict.Id, ConflictResolution.ExportBoth,
                export, CancellationToken.None);

            Assert.True(exported.Exported);
            Assert.Equal(1, exported.RemainingConflicts);
            Assert.Single(await second.Conflicts.ListAsync(CancellationToken.None));
            Assert.Equal(new[] { localHash.Hex, remoteHash.Hex }.Order(StringComparer.Ordinal),
                Directory.EnumerateFiles(export, "*.jsonl").Select(path => Hash(File.ReadAllBytes(path)))
                    .Order(StringComparer.Ordinal));

            var resolved = await second.Engine.ResolveConflictAsync(conflict.Id, ConflictResolution.KeepRemote,
                null, CancellationToken.None);

            Assert.False(resolved.Exported);
            Assert.Equal(0, resolved.RemainingConflicts);
            Assert.Empty(await second.Conflicts.ListAsync(CancellationToken.None));
            Assert.Equal(remoteVersion, await File.ReadAllBytesAsync(secondSharedPath));
            await RunStoppedAgentCycleAsync(first);
            await RunStoppedAgentCycleAsync(second);
            var resolvedConvergence = new Dictionary<string, string>(expectedConvergence, StringComparer.Ordinal)
            {
                ["device-a-chat"] = remoteHash.Hex
            };
            Assert.Equal(resolvedConvergence, await ScanHashesAsync(first));
            Assert.Equal(resolvedConvergence, await ScanHashesAsync(second));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

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
        IOperationDirectoryCleaner? cleaner = null, IStorageProvider? provider = null)
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
        var storage = provider ?? new GitStorageProvider("repository", remote, GitRemoteKind.Local,
            Path.Combine(root, name, "provider"));
        var staging = Path.Combine(local, "staging");
        var engine = cleaner is null
            ? new SyncEngine("repository", name, paths, key, new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(),
                state, writer, conflicts, storage, staging)
            : new SyncEngine("repository", name, paths, key, new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(),
                state, writer, conflicts, storage, staging, NoopHooks.Instance, cleaner);
        return new Device(paths, state, backups, conflicts, writer, staging, engine);
    }

    private static async Task WriteSessionAsync(string directory, string id, string text) =>
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".jsonl"),
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}\n" + Message(text), new UTF8Encoding(false));

    private static string Message(string text) => $"{{\"type\":\"message\",\"payload\":{{\"text\":\"{text}\"}}}}\n";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<IReadOnlyDictionary<string, string>> ScanHashesAsync(Device device) =>
        (await new SessionScanner(TimeSpan.Zero).ScanAsync(device.Paths, CancellationToken.None))
        .ToDictionary(item => item.Id.Value, item => item.Hash.Hex, StringComparer.Ordinal);

    private static async Task<AgentCycle> RunStoppedAgentCycleAsync(Device device)
    {
        using var stop = new CancellationTokenSource();
        var operations = new EngineAgentOperations(device.Engine, stop);
        var notifier = new RecordingNotifier();
        var worker = new AgentWorker(new StoppedDetector(), operations, new BlockingClock(), notifier,
            new NoopLogger(), new AgentWorkerOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), 3));

        await worker.RunAsync(stop.Token);

        Assert.Equal([SyncMode.Bidirectional], operations.Modes);
        return new AgentCycle(Assert.Single(operations.Results), notifier.Notifications);
    }

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
        ConflictStore Conflicts, CodexHistoryWriter Writer, string StagingRoot, SyncEngine Engine);
    private sealed record AgentCycle(SyncResult Result, IReadOnlyList<AgentNotification> Notifications);
    private sealed class StoppedDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class EngineAgentOperations(SyncEngine engine, CancellationTokenSource stop) : IAgentSyncOperations
    {
        public List<SyncMode> Modes { get; } = [];
        public List<SyncResult> Results { get; } = [];
        public async Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken)
        {
            Modes.Add(mode);
            var result = await engine.SynchronizeAsync(mode, cancellationToken);
            Results.Add(result);
            stop.Cancel();
            return result;
        }
        public Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A stopped-agent cycle must not request active-session status.");
    }
    private sealed class BlockingClock : IAgentClock
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
    private sealed class RecordingNotifier : IAgentNotifier
    {
        public List<AgentNotification> Notifications { get; } = [];
        public Task NotifyAsync(AgentNotification notification, CancellationToken cancellationToken)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }
    private sealed class NoopLogger : IAgentLogger
    {
        public Task WriteAsync(AgentLogEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
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
    private sealed class OfflineProvider : IStorageProvider
    {
        public Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct) => throw new IOException("offline");
        public Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct) =>
            throw new IOException("offline");
    }
    public enum ImportInterruption
    {
        AfterStagingAndBackup,
        AtLocalReplace
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
