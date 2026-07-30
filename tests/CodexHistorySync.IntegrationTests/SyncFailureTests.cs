using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.IntegrationTests;

public sealed class SyncFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-failures-{Guid.NewGuid():N}");

    [Fact]
    public async Task OfflineProvider_DoesNotCreateBaseline()
    {
        var device = CreateDevice("offline", RandomNumberGenerator.GetBytes(32), new OfflineProvider());
        await WriteSessionAsync(device.Paths.Sessions, "offline-session");

        await Assert.ThrowsAsync<IOException>(() => device.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None));

        Assert.False(File.Exists(device.State.GetStatePath("repository")));
        Assert.Single(await ScanAsync(device));
    }

    [Fact]
    public async Task IncompleteObservedSession_DoesNotPublishFalseTombstone()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-incomplete-local", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "possibly-live");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("target-incomplete-local", key, provider);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(target.Paths.Sessions, "possibly-live.jsonl"), "{\"type\":\"session_meta\",\"payload\":{\"id\":\"possibly-live\"}}");

        var result = await target.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);

        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, provider.PublishCalls);
        Assert.False(Assert.Single((await target.State.LoadAsync("repository", CancellationToken.None)).Objects).IsDeleted);
    }

    [Fact]
    public async Task MissingSessionRoot_WithBaseline_DoesNotPublishTombstone()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("missing-root-source", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "root-may-be-unavailable");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("missing-root-target", key, provider);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var baseline = await target.State.LoadAsync("repository", CancellationToken.None);
        Directory.Delete(target.Paths.Sessions, recursive: true);

        var result = await target.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);

        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, provider.PublishCalls);
        Assert.Equal(baseline.Objects.ToArray(), (await target.State.LoadAsync("repository", CancellationToken.None)).Objects.ToArray());
    }

    [Fact]
    public async Task SessionAppearingAfterScan_DoesNotPublishTombstone()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-final-absence", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "reappearing");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var initialTarget = CreateDevice("target-final-absence", key, provider);
        await initialTarget.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var targetPath = Path.Combine(initialTarget.Paths.Sessions, "reappearing.jsonl");
        File.Delete(targetPath);
        var hooked = new ReadHookProvider(provider, () => WriteSessionAsync(initialTarget.Paths.Sessions, "reappearing"));
        var target = CreateDevice("target-final-absence", key, hooked);

        var result = await target.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);

        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, provider.PublishCalls);
        Assert.True(File.Exists(targetPath));
    }

    [Fact]
    public async Task SessionReappearingAtPublicationPrecondition_AbortsTombstoneAndReplans()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("precondition-source", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "precondition-session");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var initialTarget = CreateDevice("precondition-target", key, provider);
        await initialTarget.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var path = Path.Combine(initialTarget.Paths.Sessions, "precondition-session.jsonl");
        File.Delete(path);
        var hook = new ReappearingPublicationHook(() => WriteSessionAsync(initialTarget.Paths.Sessions, "precondition-session"));
        var target = CreateDevice("precondition-target", key, provider, hooks: hook);

        var result = await target.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);

        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, provider.PublishCalls);
        Assert.True(File.Exists(path));
        Assert.False(Assert.Single((await target.State.LoadAsync("repository", CancellationToken.None)).Objects).IsDeleted);
    }

    [Fact]
    public async Task RepositoryMutex_SerializesEnginesSharingLocalState()
    {
        var provider = new ConcurrencyProbeProvider();
        var key = RandomNumberGenerator.GetBytes(32);
        var first = CreateDevice("shared-mutex", key, provider);
        var second = CreateDevice("shared-mutex", key, provider);

        await Task.WhenAll(
            first.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None),
            second.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(1, provider.MaximumConcurrentReads);
    }

    [Fact]
    public async Task RepositoryFileLock_WaitsForExclusiveHandleAndReleasesOnCancellation()
    {
        var provider = new MemoryProvider();
        var device = CreateDevice("machine-lock", RandomNumberGenerator.GetBytes(32), provider);
        var stateDirectory = Path.GetDirectoryName(device.State.GetStatePath("repository"))!;
        Directory.CreateDirectory(stateDirectory);
        var lockPath = Path.Combine(stateDirectory, ".sync.lock");
        await using (var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.Engine.SynchronizeAsync(SyncMode.Pull, cancellation.Token));
            Assert.Equal(0, provider.ReadCalls);
        }

        await device.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(1, provider.ReadCalls);
    }

    [Fact]
    public async Task RepositoryFileLock_ExistingUnlockedFileDoesNotBlock()
    {
        var provider = new MemoryProvider();
        var device = CreateDevice("stale-machine-lock", RandomNumberGenerator.GetBytes(32), provider);
        var stateDirectory = Path.GetDirectoryName(device.State.GetStatePath("repository"))!;
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(Path.Combine(stateDirectory, ".sync.lock"), "stale");

        await device.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(1, provider.ReadCalls);
    }

    [Fact]
    public async Task WrongKey_DoesNotImportOrAdvanceBaseline()
    {
        var provider = new MemoryProvider();
        var source = CreateDevice("source", RandomNumberGenerator.GetBytes(32), provider);
        await WriteSessionAsync(source.Paths.Sessions, "remote-session");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("wrong-key", RandomNumberGenerator.GetBytes(32), provider);

        await Assert.ThrowsAsync<InvalidDataException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Empty(await ScanAsync(target));
        Assert.False(File.Exists(target.State.GetStatePath("repository")));
    }

    [Fact]
    public async Task MissingIndexAfterInitialization_IsRejectedWithoutDeletingOrAdvancingBaseline()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("missing-index-source", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "missing-index-session");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("missing-index-target", key, provider);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var path = Path.Combine(target.Paths.Sessions, "missing-index-session.jsonl");
        var before = await File.ReadAllTextAsync(path);
        var baseline = await target.State.LoadAsync("repository", CancellationToken.None);
        provider.ClearRepository();

        await Assert.ThrowsAsync<InvalidDataException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal(baseline.Objects.ToArray(), (await target.State.LoadAsync("repository", CancellationToken.None)).Objects.ToArray());
    }

    [Fact]
    public async Task TransientEmptyProviderResponse_IsRejectedWithoutChangingInitializedHistory()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("empty-response-source", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "empty-response-session");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var initialTarget = CreateDevice("empty-response-target", key, provider);
        await initialTarget.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var path = Path.Combine(initialTarget.Paths.Sessions, "empty-response-session.jsonl");
        var before = await File.ReadAllTextAsync(path);
        var baseline = await initialTarget.State.LoadAsync("repository", CancellationToken.None);
        var target = CreateDevice("empty-response-target", key, new EmptySnapshotProvider(provider));

        await Assert.ThrowsAsync<InvalidDataException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal(baseline.Objects.ToArray(), (await target.State.LoadAsync("repository", CancellationToken.None)).Objects.ToArray());
    }

    [Fact]
    public async Task MissingIndexAfterAuthenticatedEmptyIndex_IsRejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        await WriteIndexAsync(provider, key, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["repositoryId"] = "repository",
            ["objects"] = new JsonArray()
        });
        var device = CreateDevice("missing-empty-index", key, provider);
        await device.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        Assert.Empty((await device.State.LoadAsync("repository", CancellationToken.None)).Objects);
        provider.ClearRepository();

        await Assert.ThrowsAsync<InvalidDataException>(() => device.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Empty((await device.State.LoadAsync("repository", CancellationToken.None)).Objects);
    }

    [Fact]
    public async Task CorruptCiphertext_DoesNotImportOrAdvanceBaseline()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-corrupt", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "remote-corrupt");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        provider.CorruptFirstObject();
        var target = CreateDevice("target-corrupt", key, provider);

        await Assert.ThrowsAsync<InvalidDataException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Empty(await ScanAsync(target));
        Assert.False(File.Exists(target.State.GetStatePath("repository")));
    }

    [Fact]
    public async Task PublicationRace_RetriesAndCountsOnlyCommittedUpload()
    {
        var provider = new MemoryProvider { RejectionsRemaining = 1 };
        var device = CreateDevice("race", RandomNumberGenerator.GetBytes(32), provider);
        await WriteSessionAsync(device.Paths.Sessions, "race-session");

        var result = await device.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);

        Assert.True(result.RemoteChangedDuringAttempt);
        Assert.Equal(2, provider.PublishCalls);
        Assert.Equal(1, result.Uploaded);
        Assert.True(File.Exists(device.State.GetStatePath("repository")));
    }

    [Fact]
    public async Task InterruptedImport_PreservesHistoryAndBaseline()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-interrupt", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "shared-interrupt");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var initialTarget = CreateDevice("target-interrupt", key, provider);
        await initialTarget.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var targetPath = Path.Combine(initialTarget.Paths.Sessions, "shared-interrupt.jsonl");
        var original = await File.ReadAllTextAsync(targetPath);
        var baseline = await initialTarget.State.LoadAsync("repository", CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(source.Paths.Sessions, "shared-interrupt.jsonl"), "{\"type\":\"message\",\"payload\":{\"text\":\"remote change\"}}\n");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("target-interrupt", key, provider, fileSystem: new FailingWriteFileSystem());

        await Assert.ThrowsAsync<IOException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllTextAsync(targetPath));
        Assert.Equal(baseline.Objects.ToArray(), (await target.State.LoadAsync("repository", CancellationToken.None)).Objects.ToArray());
    }

    [Fact]
    public async Task CodexStartsBeforeReplacement_PreservesHistoryAndBaseline()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-process", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "shared-process");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var initialTarget = CreateDevice("target-process", key, provider);
        await initialTarget.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var targetPath = Path.Combine(initialTarget.Paths.Sessions, "shared-process.jsonl");
        var original = await File.ReadAllTextAsync(targetPath);
        var baseline = await initialTarget.State.LoadAsync("repository", CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(source.Paths.Sessions, "shared-process.jsonl"), "{\"type\":\"message\",\"payload\":{\"text\":\"remote change\"}}\n");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("target-process", key, provider, detector: new StartsDuringImportDetector());

        await Assert.ThrowsAsync<InvalidOperationException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllTextAsync(targetPath));
        Assert.Equal(baseline.Objects.ToArray(), (await target.State.LoadAsync("repository", CancellationToken.None)).Objects.ToArray());
    }

    [Fact]
    public async Task LocalEditAfterScan_IsPreservedAndDefersDownloadedBaseline()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-late-local-edit", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "late-local-edit");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var initialTarget = CreateDevice("target-late-local-edit", key, provider);
        await initialTarget.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var targetPath = Path.Combine(initialTarget.Paths.Sessions, "late-local-edit.jsonl");
        var baseline = await initialTarget.State.LoadAsync("repository", CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(source.Paths.Sessions, "late-local-edit.jsonl"),
            "{\"type\":\"message\",\"payload\":{\"text\":\"remote change\"}}\n");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var fileSystem = new MutatingPublishFileSystem(targetPath);
        var target = CreateDevice("target-late-local-edit", key, provider, fileSystem: fileSystem);

        var result = await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(0, result.Downloaded);
        Assert.Equal(1, result.Conflicts);
        Assert.Contains("concurrent local edit", await File.ReadAllTextAsync(targetPath));
        Assert.Equal(baseline.Objects.ToArray(), (await target.State.LoadAsync("repository", CancellationToken.None)).Objects.ToArray());
        Assert.Single(await target.Conflicts.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TombstoneRace_PreservesConcurrentLocalVersionAsConflict()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-tombstone-race", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "tombstone-race");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var initialTarget = CreateDevice("target-tombstone-race", key, provider);
        await initialTarget.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        File.Delete(Path.Combine(source.Paths.Sessions, "tombstone-race.jsonl"));
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("target-tombstone-race", key, provider, fileSystem: new MutatingDeleteFileSystem());

        var result = await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(1, result.Conflicts);
        Assert.Contains("concurrent local change", await File.ReadAllTextAsync(Path.Combine(target.Paths.Sessions, "tombstone-race.jsonl")));
        Assert.Single(await target.Conflicts.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FivePublicationRaces_ThrowsWithoutAdvancingBaseline()
    {
        var provider = new MemoryProvider { RejectionsRemaining = 5 };
        var device = CreateDevice("exhausted", RandomNumberGenerator.GetBytes(32), provider);
        await WriteSessionAsync(device.Paths.Sessions, "exhausted-session");

        await Assert.ThrowsAsync<SyncConcurrencyException>(() => device.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None));

        Assert.Equal(5, provider.PublishCalls);
        Assert.False(File.Exists(device.State.GetStatePath("repository")));
        Assert.Single(await ScanAsync(device));
    }

    [Fact]
    public async Task FivePublicationRaces_DoNotApplyStagedDownloads()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-race-download", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "remote-session");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("target-race-download", key, provider);
        await WriteSessionAsync(target.Paths.Sessions, "local-session");
        provider.RejectionsRemaining = 5;

        await Assert.ThrowsAsync<SyncConcurrencyException>(() => target.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None));

        Assert.Equal(["local-session"], (await ScanAsync(target)).Select(item => item.Id.Value));
        Assert.False(File.Exists(target.State.GetStatePath("repository")));
    }

    [Fact]
    public async Task AppliedTombstone_WithUnadvancedBaseline_IsAcceptedOnRecovery()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-tombstone-recovery", key, provider);
        var target = CreateDevice("target-tombstone-recovery", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "deleted-remotely");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var statePath = target.State.GetStatePath("repository");
        var oldBaseline = await File.ReadAllBytesAsync(statePath);
        File.Delete(Path.Combine(source.Paths.Sessions, "deleted-remotely.jsonl"));
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        await File.WriteAllBytesAsync(statePath, oldBaseline);

        var result = await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(0, result.Conflicts);
        Assert.Empty(await ScanAsync(target));
        Assert.True(Assert.Single((await target.State.LoadAsync("repository", CancellationToken.None)).Objects).IsDeleted);
    }

    [Fact]
    public async Task InvalidLaterDownload_IsRejectedBeforeAnyEarlierDownloadIsApplied()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-staging", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "a-valid");
        await WriteSessionAsync(source.Paths.Sessions, "z-invalid");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await ReplaceObjectPlaintextAsync(provider, key, "z-invalid", "{\"type\":\"message\",\"payload\":{}}\n"u8.ToArray());
        var target = CreateDevice("target-staging", key, provider);

        await Assert.ThrowsAsync<InvalidDataException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Empty(await ScanAsync(target));
        Assert.False(File.Exists(target.State.GetStatePath("repository")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "target-staging", "local"), "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidDownload_IsRejectedBeforeConflictEvidenceIsPublished()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-conflict-staging", key, provider);
        var target = CreateDevice("target-conflict-staging", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "shared-conflict-staging");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(source.Paths.Sessions, "shared-conflict-staging.jsonl"), "{\"type\":\"message\",\"payload\":{\"text\":\"remote change\"}}\n");
        await WriteSessionAsync(source.Paths.Sessions, "z-invalid-staging");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await ReplaceObjectPlaintextAsync(provider, key, "z-invalid-staging", "{\"type\":\"message\",\"payload\":{}}\n"u8.ToArray());
        await File.AppendAllTextAsync(Path.Combine(target.Paths.Sessions, "shared-conflict-staging.jsonl"), "{\"type\":\"message\",\"payload\":{\"text\":\"local change\"}}\n");

        await Assert.ThrowsAsync<InvalidDataException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Empty(await target.Conflicts.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FivePublicationRaces_DoNotPublishPendingConflictEvidence()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("failed-cas-conflict-source", key, provider);
        var target = CreateDevice("failed-cas-conflict-target", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "failed-cas-conflict");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(source.Paths.Sessions, "failed-cas-conflict.jsonl"),
            "{\"type\":\"message\",\"payload\":{\"text\":\"remote\"}}\n");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(target.Paths.Sessions, "failed-cas-conflict.jsonl"),
            "{\"type\":\"message\",\"payload\":{\"text\":\"local\"}}\n");
        await WriteSessionAsync(target.Paths.Sessions, "forces-cas");
        provider.RejectionsRemaining = 5;

        await Assert.ThrowsAsync<SyncConcurrencyException>(() => target.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None));

        Assert.Empty(await target.Conflicts.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("unknown-schema")]
    [InlineData("repository-mismatch")]
    [InlineData("invalid-hash")]
    [InlineData("invalid-kind")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-opaque")]
    [InlineData("missing-object")]
    [InlineData("extra-object")]
    [InlineData("tampered-index")]
    [InlineData("index-metadata")]
    [InlineData("object-metadata")]
    [InlineData("opaque-id-mismatch")]
    public async Task InvalidAuthenticatedSnapshot_IsRejectedBeforeImportOrBaseline(string mutation)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-index-" + mutation, key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "remote-index");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        if (mutation == "unknown-schema") await RewriteIndexAsync(provider, key, json => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal));
        if (mutation == "repository-mismatch") await RewriteIndexAsync(provider, key, json => json.Replace("\"repositoryId\":\"repository\"", "\"repositoryId\":\"other\"", StringComparison.Ordinal));
        if (mutation == "invalid-hash") await RewriteIndexAsync(provider, key, json => ReplaceField(json, "plaintextHash", new string('z', 64)));
        if (mutation == "invalid-kind") await RewriteIndexAsync(provider, key, json => json.Replace("\"kind\":0", "\"kind\":4", StringComparison.Ordinal));
        if (mutation == "duplicate-id") await RewriteIndexAsync(provider, key, json => DuplicateEntry(json, null));
        if (mutation == "duplicate-opaque") await RewriteIndexAsync(provider, key, json => DuplicateEntry(json, "copy-index"));
        if (mutation == "missing-object") provider.RemoveFirstObject();
        if (mutation == "extra-object") provider.AddExtraObject();
        if (mutation == "tampered-index") provider.CorruptIndex();
        if (mutation == "index-metadata") await ReplaceIndexMetadataAsync(provider, key);
        if (mutation == "object-metadata") await ReplaceWithMetadataMismatchAsync(provider, key, source.Paths);
        if (mutation == "opaque-id-mismatch") await ReplaceWithOpaqueIdMismatchAsync(provider, key);
        var target = CreateDevice("target-index-" + mutation, key, provider);

        await Assert.ThrowsAsync<InvalidDataException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Empty(await ScanAsync(target));
        Assert.False(File.Exists(target.State.GetStatePath("repository")));
    }

    [Fact]
    public async Task UnsortedAuthenticatedIndex_IsRejectedBeforeImportOrBaseline()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("source-unsorted-index", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "a-session");
        await WriteSessionAsync(source.Paths.Sessions, "z-session");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await ReverseIndexEntriesAsync(provider, key);
        var target = CreateDevice("target-unsorted-index", key, provider);

        await Assert.ThrowsAsync<InvalidDataException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Empty(await ScanAsync(target));
        Assert.False(File.Exists(target.State.GetStatePath("repository")));
    }

    [Fact]
    public async Task Pull_DoesNotUploadLocalOnlyHistory()
    {
        var provider = new MemoryProvider();
        var device = CreateDevice("pull-only", RandomNumberGenerator.GetBytes(32), provider);
        await WriteSessionAsync(device.Paths.Sessions, "pull-local");

        var result = await device.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(0, result.Uploaded);
        Assert.Equal(0, provider.PublishCalls);
        Assert.Single(await ScanAsync(device));
    }

    [Fact]
    public async Task Push_DoesNotImportRemoteOnlyHistory()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("push-source", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "push-remote");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("push-target", key, provider);

        var result = await target.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);

        Assert.Equal(0, result.Downloaded);
        Assert.Equal(1, provider.PublishCalls);
        Assert.Empty(await ScanAsync(target));
    }

    [Fact]
    public async Task Pull_DetectsConflictWithoutUploadingOrReplacingLocalHistory()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("conflict-source", key, provider);
        var target = CreateDevice("conflict-target", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "shared");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var targetPath = Path.Combine(target.Paths.Sessions, "shared.jsonl");
        await File.AppendAllTextAsync(targetPath, "{\"type\":\"message\",\"payload\":{\"text\":\"target-change\"}}\n");
        await File.AppendAllTextAsync(Path.Combine(source.Paths.Sessions, "shared.jsonl"), "{\"type\":\"message\",\"payload\":{\"text\":\"source-change\"}}\n");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var localBefore = await File.ReadAllTextAsync(targetPath);

        var result = await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.Downloaded);
        Assert.Equal(2, provider.PublishCalls);
        Assert.Equal(localBefore, await File.ReadAllTextAsync(targetPath));
        Assert.Single(await target.Conflicts.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PublicationRetry_PreservesOneRecordForTheSameConflict()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("retry-conflict-source", key, provider);
        var target = CreateDevice("retry-conflict-target", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "shared-retry-conflict");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(source.Paths.Sessions, "shared-retry-conflict.jsonl"), "{\"type\":\"message\",\"payload\":{\"text\":\"source change\"}}\n");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(target.Paths.Sessions, "shared-retry-conflict.jsonl"), "{\"type\":\"message\",\"payload\":{\"text\":\"target change\"}}\n");
        await WriteSessionAsync(target.Paths.Sessions, "local-upload");
        provider.RejectionsRemaining = 1;

        var result = await target.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.True(result.RemoteChangedDuringAttempt);
        Assert.Equal(1, result.Conflicts);
        Assert.Single(await target.Conflicts.ListAsync(CancellationToken.None));

        var repeated = await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(1, repeated.Conflicts);
        Assert.Single(await target.Conflicts.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemoteRemovalVersusLocalModification_PreservesBothConflictSides()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("remote-removal-source", key, provider);
        var target = CreateDevice("remote-removal-target", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "removed-remotely");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(target.Paths.Sessions, "removed-remotely.jsonl"), "{\"type\":\"message\",\"payload\":{\"text\":\"local change\"}}\n");
        await RemoveIndexEntryAsync(provider, key, "removed-remotely");

        var result = await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(1, result.Conflicts);
        var conflict = Assert.Single(await target.Conflicts.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(conflict.LocalEncryptedPath));
        Assert.True(File.Exists(conflict.RemoteEncryptedPath));
    }

    [Fact]
    public async Task AuthenticatedRemoteRemoval_DeletesUnchangedLocalWithoutNullBaseline()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("remote-removal-unchanged-source", key, provider);
        var target = CreateDevice("remote-removal-unchanged-target", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "removed-unchanged");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        await RemoveIndexEntryAsync(provider, key, "removed-unchanged");

        var result = await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Empty(await ScanAsync(target));
        var baseline = Assert.Single((await target.State.LoadAsync("repository", CancellationToken.None)).Objects);
        Assert.NotNull(baseline);
        Assert.True(baseline.IsDeleted);
    }

    [Fact]
    public async Task ReservedRepositoryIndexId_IsRejectedAsLocalHistory()
    {
        var provider = new MemoryProvider();
        var device = CreateDevice("reserved-id", RandomNumberGenerator.GetBytes(32), provider);
        await WriteSessionAsync(device.Paths.Sessions, "__repository_index__");

        await Assert.ThrowsAsync<InvalidDataException>(() => device.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None));

        Assert.Equal(0, provider.PublishCalls);
        Assert.False(File.Exists(device.State.GetStatePath("repository")));
    }

    [Fact]
    public async Task StateSaveFailure_RollsBackEveryAppliedLocalMutation()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("batch-rollback-source", key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "batch-first");
        await WriteSessionAsync(source.Paths.Sessions, "batch-second");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var initialTarget = CreateDevice("batch-rollback-target", key, provider);
        await initialTarget.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);
        var firstPath = Path.Combine(initialTarget.Paths.Sessions, "batch-first.jsonl");
        var secondPath = Path.Combine(initialTarget.Paths.Sessions, "batch-second.jsonl");
        var firstBefore = await File.ReadAllTextAsync(firstPath);
        var secondBefore = await File.ReadAllTextAsync(secondPath);
        var baseline = await initialTarget.State.LoadAsync("repository", CancellationToken.None);
        await File.AppendAllTextAsync(Path.Combine(source.Paths.Sessions, "batch-first.jsonl"),
            "{\"type\":\"message\",\"payload\":{\"text\":\"remote first\"}}\n");
        File.Delete(Path.Combine(source.Paths.Sessions, "batch-second.jsonl"));
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var target = CreateDevice("batch-rollback-target", key, provider, stateReplacer: new FailingStateFileReplacer());

        await Assert.ThrowsAsync<IOException>(() => target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(firstBefore, await File.ReadAllTextAsync(firstPath));
        Assert.Equal(secondBefore, await File.ReadAllTextAsync(secondPath));
        Assert.Equal(baseline.Objects.ToArray(), (await target.State.LoadAsync("repository", CancellationToken.None)).Objects.ToArray());
    }

    [Fact]
    public async Task Restart_RecoversInterruptedMutationBeforeReadingProvider()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var device = CreateDevice("restart-recovery", key, provider);
        await WriteSessionAsync(device.Paths.Sessions, "restart-session");
        var path = Path.Combine(device.Paths.Sessions, "restart-session.jsonl");
        var before = await File.ReadAllTextAsync(path);
        var beforeHash = await BackupStore.HashFileAsync(path, CancellationToken.None);
        var changed = before + "{\"type\":\"message\",\"payload\":{\"text\":\"interrupted\"}}\n";
        var afterHash = new ContentHash(Hash(Encoding.UTF8.GetBytes(changed)));
        var local = Assert.Single(await ScanAsync(device));
        var operationDirectory = Path.Combine(device.StagingRoot, "interrupted-operation");
        Directory.CreateDirectory(operationDirectory);
        var interrupted = await HistoryMutationBatch.PrepareAsync(device.Writer, operationDirectory, "interrupted-operation",
            [new HistoryMutationPlan(local, ExpectedHistoryState.Present(beforeHash), ExpectedHistoryState.Present(afterHash))],
            CancellationToken.None);
        await interrupted.BeginApplyAsync(local.Id, CancellationToken.None);
        await File.WriteAllTextAsync(path, changed, new UTF8Encoding(false));
        var restarted = CreateDevice("restart-recovery", key, new OfflineProvider());

        await Assert.ThrowsAsync<IOException>(() => restarted.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(operationDirectory));
    }

    [Fact]
    public async Task Restart_DoesNotRollbackMutationWhoseBaselineWasAlreadySaved()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var device = CreateDevice("restart-committed", key, new MemoryProvider());
        await WriteSessionAsync(device.Paths.Sessions, "committed-session");
        var path = Path.Combine(device.Paths.Sessions, "committed-session.jsonl");
        var beforeHash = await BackupStore.HashFileAsync(path, CancellationToken.None);
        var changed = await File.ReadAllTextAsync(path) + "{\"type\":\"message\",\"payload\":{\"text\":\"committed\"}}\n";
        var afterHash = new ContentHash(Hash(Encoding.UTF8.GetBytes(changed)));
        var local = Assert.Single(await ScanAsync(device));
        var operationDirectory = Path.Combine(device.StagingRoot, "committed-operation");
        Directory.CreateDirectory(operationDirectory);
        var batch = await HistoryMutationBatch.PrepareAsync(device.Writer, operationDirectory, "committed-operation",
            [new HistoryMutationPlan(local, ExpectedHistoryState.Present(beforeHash), ExpectedHistoryState.Present(afterHash))],
            CancellationToken.None);
        await batch.BeginApplyAsync(local.Id, CancellationToken.None);
        await File.WriteAllTextAsync(path, changed, new UTF8Encoding(false));
        await batch.MarkAppliedAsync(local.Id, CancellationToken.None);
        await device.State.SaveAsync(new DeviceState(LocalStateStore.CurrentSchemaVersion, "repository",
            [new ObjectVersion(local.Id, local.Kind, afterHash, "committed-version", false)]), CancellationToken.None);
        var restarted = CreateDevice("restart-committed", key, new OfflineProvider());

        await Assert.ThrowsAsync<IOException>(() => restarted.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(changed, await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(operationDirectory));
    }

    [Fact]
    public async Task Restart_RejectsInvalidMutationStatusWithoutDiscardingRecoveryEvidence()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var device = CreateDevice("restart-invalid-marker", key, new MemoryProvider());
        await WriteSessionAsync(device.Paths.Sessions, "invalid-marker-session");
        var path = Path.Combine(device.Paths.Sessions, "invalid-marker-session.jsonl");
        var beforeHash = await BackupStore.HashFileAsync(path, CancellationToken.None);
        var changed = await File.ReadAllTextAsync(path) + "{\"type\":\"message\",\"payload\":{\"text\":\"uncertain\"}}\n";
        var afterHash = new ContentHash(Hash(Encoding.UTF8.GetBytes(changed)));
        var local = Assert.Single(await ScanAsync(device));
        var operationDirectory = Path.Combine(device.StagingRoot, "invalid-marker-operation");
        Directory.CreateDirectory(operationDirectory);
        var batch = await HistoryMutationBatch.PrepareAsync(device.Writer, operationDirectory, "invalid-marker-operation",
            [new HistoryMutationPlan(local, ExpectedHistoryState.Present(beforeHash), ExpectedHistoryState.Present(afterHash))],
            CancellationToken.None);
        await batch.BeginApplyAsync(local.Id, CancellationToken.None);
        await File.WriteAllTextAsync(path, changed, new UTF8Encoding(false));
        var markerPath = Path.Combine(operationDirectory, HistoryMutationBatch.MarkerFileName);
        var marker = await File.ReadAllTextAsync(markerPath);
        await File.WriteAllTextAsync(markerPath, marker.Replace("\"status\":1", "\"status\":99", StringComparison.Ordinal));
        var restarted = CreateDevice("restart-invalid-marker", key, new OfflineProvider());

        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Equal(changed, await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(markerPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedCleanupFailure_DoesNotFailAndRestartRemovesOperationPlaintext(bool failAtMarker)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var provider = new MemoryProvider();
        var source = CreateDevice("cleanup-source-" + failAtMarker, key, provider);
        await WriteSessionAsync(source.Paths.Sessions, "cleanup-session");
        await source.Engine.SynchronizeAsync(SyncMode.Push, CancellationToken.None);
        var cleaner = new FailingOperationDirectoryCleaner(failAtMarker);
        var target = CreateDevice("cleanup-target-" + failAtMarker, key, provider, cleaner: cleaner);

        var result = await target.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None);

        Assert.Equal(1, result.Downloaded);
        Assert.Single((await target.State.LoadAsync("repository", CancellationToken.None)).Objects);
        var operation = Assert.Single(Directory.EnumerateDirectories(target.StagingRoot));
        Assert.True(File.Exists(Path.Combine(operation, HistoryMutationBatch.MarkerFileName)));
        Assert.True(File.Exists(HistoryMutationBatch.CleanupEvidencePath(operation)));
        var restarted = CreateDevice("cleanup-target-" + failAtMarker, key, new OfflineProvider());

        await Assert.ThrowsAsync<IOException>(() => restarted.Engine.SynchronizeAsync(SyncMode.Pull, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(restarted.StagingRoot));
    }

    private Device CreateDevice(string name, byte[] key, IStorageProvider provider, ICodexProcessDetector? detector = null,
        IAtomicFileSystem? fileSystem = null, IStateFileReplacer? stateReplacer = null, ISyncEngineHooks? hooks = null,
        IOperationDirectoryCleaner? cleaner = null)
    {
        var home = Path.Combine(_root, name, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        Directory.CreateDirectory(paths.Sessions);
        var local = Path.Combine(_root, name, "local");
        var state = stateReplacer is null ? new LocalStateStore(local) : new LocalStateStore(local, stateReplacer);
        var backups = new BackupStore("repository", local, paths, fileSystem);
        var writer = new CodexHistoryWriter(paths, backups, detector ?? new StoppedDetector(), fileSystem);
        var conflicts = new ConflictStore("repository", local, paths);
        var engine = hooks is null && cleaner is null
            ? new SyncEngine("repository", name, paths, key, new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(), state,
                writer, conflicts, provider, Path.Combine(local, "staging"))
            : new SyncEngine("repository", name, paths, key, new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(), state,
                writer, conflicts, provider, Path.Combine(local, "staging"), hooks ?? NoopTestSyncEngineHooks.Instance,
                cleaner ?? new OperationDirectoryCleaner());
        return new(paths, state, conflicts, writer, Path.Combine(local, "staging"), engine);
    }

    private static async Task WriteSessionAsync(string directory, string id)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".jsonl"),
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}\n{{\"type\":\"message\",\"payload\":{{\"text\":\"{id}\"}}}}\n", new UTF8Encoding(false));
    }

    private static Task<IReadOnlyList<LocalObject>> ScanAsync(Device device) => new SessionScanner(TimeSpan.Zero).ScanAsync(device.Paths, CancellationToken.None);

    private static async Task RewriteIndexAsync(MemoryProvider provider, byte[] key, Func<string, string> rewrite)
    {
        var crypto = new RepositoryCrypto();
        await using var input = new MemoryStream(provider.Index, false);
        await using var plaintext = new MemoryStream();
        await crypto.DecryptAsync(input, plaintext, key, new EnvelopeMetadata(1, new LogicalObjectId("__repository_index__"), ObjectKind.RepositoryIndex), CancellationToken.None);
        var rewritten = Encoding.UTF8.GetBytes(rewrite(Encoding.UTF8.GetString(plaintext.ToArray())));
        await using var rewrittenInput = new MemoryStream(rewritten, false);
        await using var output = new MemoryStream();
        await crypto.EncryptAsync(rewrittenInput, output, key, new EnvelopeMetadata(1, new LogicalObjectId("__repository_index__"), ObjectKind.RepositoryIndex), CancellationToken.None);
        provider.SetIndex(output.ToArray());
    }

    private static string ReplaceField(string json, string name, string value)
    {
        var marker = $"\"{name}\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = json.IndexOf('"', start);
        return json[..start] + value + json[end..];
    }

    private static string DuplicateEntry(string json, string? replacementId)
    {
        var start = json.IndexOf("\"objects\":[", StringComparison.Ordinal) + "\"objects\":[".Length;
        var end = json.LastIndexOf(']');
        var entry = json[start..end];
        if (replacementId is not null) entry = entry.Replace("\"id\":\"remote-index\"", $"\"id\":\"{replacementId}\"", StringComparison.Ordinal);
        return json[..end] + "," + entry + json[end..];
    }

    private static async Task ReplaceWithMetadataMismatchAsync(MemoryProvider provider, byte[] key, CodexPaths paths)
    {
        var plaintext = await File.ReadAllBytesAsync(Path.Combine(paths.Sessions, "remote-index.jsonl"));
        var crypto = new RepositoryCrypto();
        await using var input = new MemoryStream(plaintext, false);
        await using var output = new MemoryStream();
        await crypto.EncryptAsync(input, output, key, new EnvelopeMetadata(1, new LogicalObjectId("wrong-id"), ObjectKind.ActiveSession), CancellationToken.None);
        var ciphertext = output.ToArray();
        var opaque = Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant();
        var oldOpaque = provider.SingleObjectId.Value;
        provider.ReplaceSingleObject(new LogicalObjectId(opaque), ciphertext);
        await RewriteIndexAsync(provider, key, json => json.Replace(oldOpaque, opaque, StringComparison.Ordinal));
    }

    private static async Task ReplaceIndexMetadataAsync(MemoryProvider provider, byte[] key)
    {
        var crypto = new RepositoryCrypto();
        await using var encrypted = new MemoryStream(provider.Index, false);
        await using var plaintext = new MemoryStream();
        await crypto.DecryptAsync(encrypted, plaintext, key, new EnvelopeMetadata(1, new LogicalObjectId("__repository_index__"), ObjectKind.RepositoryIndex), CancellationToken.None);
        plaintext.Position = 0;
        await using var output = new MemoryStream();
        await crypto.EncryptAsync(plaintext, output, key, new EnvelopeMetadata(1, new LogicalObjectId("__repository_index__"), ObjectKind.ActiveSession), CancellationToken.None);
        provider.SetIndex(output.ToArray());
    }

    private static async Task ReplaceWithOpaqueIdMismatchAsync(MemoryProvider provider, byte[] key)
    {
        var oldOpaque = provider.SingleObjectId.Value;
        var replacement = (oldOpaque[0] == '0' ? "1" : "0") + oldOpaque[1..];
        provider.RenameSingleObject(new LogicalObjectId(replacement));
        await RewriteIndexAsync(provider, key, json => json.Replace(oldOpaque, replacement, StringComparison.Ordinal));
    }

    private static async Task ReplaceObjectPlaintextAsync(MemoryProvider provider, byte[] key, string id, byte[] plaintext)
    {
        var index = await ReadIndexAsync(provider, key);
        var entry = index["objects"]!.AsArray().Select(item => item!.AsObject())
            .Single(item => item["id"]!.GetValue<string>() == id);
        var kind = (ObjectKind)entry["kind"]!.GetValue<int>();
        var oldOpaque = entry["opaqueObjectId"]!.GetValue<string>();
        var crypto = new RepositoryCrypto();
        await using var input = new MemoryStream(plaintext, false);
        await using var output = new MemoryStream();
        await crypto.EncryptAsync(input, output, key, new EnvelopeMetadata(1, new LogicalObjectId(id), kind), CancellationToken.None);
        var ciphertext = output.ToArray();
        var opaque = Hash(ciphertext);
        entry["plaintextHash"] = Hash(plaintext);
        entry["opaqueObjectId"] = opaque;
        provider.ReplaceObject(new LogicalObjectId(oldOpaque), new LogicalObjectId(opaque), ciphertext);
        await WriteIndexAsync(provider, key, index);
    }

    private static async Task ReverseIndexEntriesAsync(MemoryProvider provider, byte[] key)
    {
        var index = await ReadIndexAsync(provider, key);
        var objects = index["objects"]!.AsArray();
        var entries = objects.Select(item => item!.DeepClone()).Reverse().ToArray();
        objects.Clear();
        foreach (var entry in entries) objects.Add(entry);
        await WriteIndexAsync(provider, key, index);
    }

    private static async Task RemoveIndexEntryAsync(MemoryProvider provider, byte[] key, string id)
    {
        var index = await ReadIndexAsync(provider, key);
        var objects = index["objects"]!.AsArray();
        var entry = objects.Select(item => item!.AsObject()).Single(item => item["id"]!.GetValue<string>() == id);
        var opaque = entry["opaqueObjectId"]!.GetValue<string>();
        objects.Remove(entry);
        provider.RemoveObject(new LogicalObjectId(opaque));
        await WriteIndexAsync(provider, key, index);
    }

    private static async Task<JsonObject> ReadIndexAsync(MemoryProvider provider, byte[] key)
    {
        var crypto = new RepositoryCrypto();
        await using var input = new MemoryStream(provider.Index, false);
        await using var plaintext = new MemoryStream();
        await crypto.DecryptAsync(input, plaintext, key, new EnvelopeMetadata(1, new LogicalObjectId("__repository_index__"), ObjectKind.RepositoryIndex), CancellationToken.None);
        return JsonNode.Parse(plaintext.ToArray())!.AsObject();
    }

    private static async Task WriteIndexAsync(MemoryProvider provider, byte[] key, JsonObject index)
    {
        var crypto = new RepositoryCrypto();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(index.ToJsonString()), false);
        await using var output = new MemoryStream();
        await crypto.EncryptAsync(input, output, key, new EnvelopeMetadata(1, new LogicalObjectId("__repository_index__"), ObjectKind.RepositoryIndex), CancellationToken.None);
        provider.SetIndex(output.ToArray());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed record Device(CodexPaths Paths, LocalStateStore State, ConflictStore Conflicts, CodexHistoryWriter Writer,
        string StagingRoot, SyncEngine Engine);

    private sealed class FailingStateFileReplacer : IStateFileReplacer
    {
        public void Replace(string sourcePath, string destinationPath) => throw new IOException("state save failed");
    }

    private sealed class ReappearingPublicationHook(Func<Task> action) : ISyncEngineHooks
    {
        private int _invoked;
        public void OnBeforeLocalPublicationPrecondition()
        {
            if (Interlocked.Exchange(ref _invoked, 1) == 0) action().GetAwaiter().GetResult();
        }
    }

    private sealed class NoopTestSyncEngineHooks : ISyncEngineHooks
    {
        internal static readonly NoopTestSyncEngineHooks Instance = new();
        public void OnBeforeLocalPublicationPrecondition() { }
    }

    private sealed class FailingOperationDirectoryCleaner(bool failAtMarker) : IOperationDirectoryCleaner
    {
        public void Delete(string operationDirectory, string markerFileName)
        {
            if (!failAtMarker) throw new IOException("directory cleanup failed");
            foreach (var file in Directory.EnumerateFiles(operationDirectory, "*", SearchOption.AllDirectories)
                         .Where(path => !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(path), markerFileName)))
                File.Delete(file);
            throw new IOException("marker cleanup failed");
        }
    }
    private sealed class StoppedDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class StartsDuringImportDetector : ICodexProcessDetector
    {
        private int _checks;
        public bool IsRunning() => Interlocked.Increment(ref _checks) > 1;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class OfflineProvider : IStorageProvider
    {
        public Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct) => throw new IOException("offline");
        public Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct) => throw new IOException("offline");
    }

    private sealed class ReadHookProvider(IStorageProvider inner, Func<Task> hook) : IStorageProvider
    {
        private int _invoked;
        public async Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct)
        {
            var snapshot = await inner.ReadSnapshotAsync(ct);
            if (Interlocked.Exchange(ref _invoked, 1) == 0) await hook();
            return snapshot;
        }

        public Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct) => inner.TryPublishAsync(request, ct);
    }

    private sealed class EmptySnapshotProvider(IStorageProvider inner) : IStorageProvider
    {
        public async Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct)
        {
            var snapshot = await inner.ReadSnapshotAsync(ct);
            return new RemoteSnapshot(snapshot.Revision, null, []);
        }

        public Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct) => inner.TryPublishAsync(request, ct);
    }

    private sealed class ConcurrencyProbeProvider : IStorageProvider
    {
        private int _activeReads;
        private int _maximumConcurrentReads;
        public int MaximumConcurrentReads => Volatile.Read(ref _maximumConcurrentReads);

        public async Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _activeReads);
            var observed = Volatile.Read(ref _maximumConcurrentReads);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref _maximumConcurrentReads, active, observed);
                if (previous == observed) break;
                observed = previous;
            }
            try
            {
                await Task.Delay(100, ct);
                return new RemoteSnapshot(string.Empty, null, []);
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }

        public Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct) => throw new InvalidOperationException("No publication expected.");
    }

    private sealed class MemoryProvider : IStorageProvider
    {
        private readonly Dictionary<LogicalObjectId, byte[]> _objects = [];
        private byte[]? _index;
        private int _revision;
        public int RejectionsRemaining { get; set; }
        public int PublishCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public byte[] Index => _index!.ToArray();
        public LogicalObjectId SingleObjectId => _objects.Keys.Single();
        public Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct)
        {
            ReadCalls++;
            return Task.FromResult(new RemoteSnapshot(_revision == 0 ? string.Empty : _revision.ToString(), _index?.ToArray(),
                _objects.Select(pair => new EncryptedRemoteObject(pair.Key, pair.Value.ToArray())).ToArray()));
        }
        public async Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct)
        {
            PublishCalls++;
            if (RejectionsRemaining-- > 0) return new(false, _revision.ToString());
            if (!StringComparer.Ordinal.Equals(request.ExpectedRevision, _revision == 0 ? string.Empty : _revision.ToString())) return new(false, _revision.ToString());
            if (request.Index is { Delete: false } index) _index = await File.ReadAllBytesAsync(index.CiphertextPath, ct);
            foreach (var change in request.Changes)
                if (change.Delete) _objects.Remove(change.ObjectId);
                else _objects[change.ObjectId] = await File.ReadAllBytesAsync(change.CiphertextPath, ct);
            _revision++;
            return new(true, _revision.ToString());
        }
        public void CorruptFirstObject() { var key = _objects.Keys.First(); _objects[key][^1] ^= 0x80; }
        public void CorruptIndex() => _index![^1] ^= 0x80;
        public void SetIndex(byte[] ciphertext) => _index = ciphertext.ToArray();
        public void RemoveFirstObject() => _objects.Remove(_objects.Keys.First());
        public void RemoveObject(LogicalObjectId id) => _objects.Remove(id);
        public void AddExtraObject() => _objects[new LogicalObjectId(new string('0', 64))] = [1, 2, 3];
        public void ReplaceSingleObject(LogicalObjectId id, byte[] ciphertext) { _objects.Clear(); _objects[id] = ciphertext; }
        public void RenameSingleObject(LogicalObjectId id) { var ciphertext = _objects.Values.Single(); _objects.Clear(); _objects[id] = ciphertext; }
        public void ReplaceObject(LogicalObjectId oldId, LogicalObjectId newId, byte[] ciphertext) { _objects.Remove(oldId); _objects[newId] = ciphertext; }
        public void ClearRepository() { _index = null; _objects.Clear(); }
    }

    private sealed class FailingWriteFileSystem : IAtomicFileSystem
    {
        public Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct) => throw new IOException("interrupted import");
        public Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct) => throw new NotSupportedException();
        public Task PublishAsync(string temporaryPath, string destinationPath, ContentHash expectedSourceHash, ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MutatingDeleteFileSystem : IAtomicFileSystem
    {
        private readonly AtomicFileSystem _inner = new();
        public Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct) => _inner.WriteTemporaryAsync(path, content, ct);
        public Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct) => _inner.ReplaceAsync(temporaryPath, destinationPath, ct);
        public Task PublishAsync(string temporaryPath, string destinationPath, ContentHash expectedSourceHash, ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) =>
            _inner.PublishAsync(temporaryPath, destinationPath, expectedSourceHash, expectedDestinationHash, mutationAllowed, ct);
        public Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct) =>
            _inner.ReplaceIfUnchangedAsync(temporaryPath, destinationPath, expectedDestinationHash, mutationAllowed, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => _inner.DeleteAsync(path, ct);
        public async Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct)
        {
            await File.AppendAllTextAsync(path, "{\"type\":\"message\",\"payload\":{\"text\":\"concurrent local change\"}}\n", ct);
            return await _inner.DeleteIfUnchangedAsync(path, expectedHash, mutationAllowed, ct);
        }
    }

    private sealed class MutatingPublishFileSystem(string destination) : IAtomicFileSystem
    {
        private readonly AtomicFileSystem _inner = new();
        private int _mutated;
        public Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct) => _inner.WriteTemporaryAsync(path, content, ct);
        public Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct) => _inner.ReplaceAsync(temporaryPath, destinationPath, ct);
        public async Task PublishAsync(string temporaryPath, string destinationPath, ContentHash expectedSourceHash,
            ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(destination, destinationPath) && Interlocked.Exchange(ref _mutated, 1) == 0)
                await File.AppendAllTextAsync(destinationPath, "{\"type\":\"message\",\"payload\":{\"text\":\"concurrent local edit\"}}\n", ct);
            await _inner.PublishAsync(temporaryPath, destinationPath, expectedSourceHash, expectedDestinationHash, mutationAllowed, ct);
        }
        public Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash,
            Func<bool>? mutationAllowed, CancellationToken ct) => _inner.ReplaceIfUnchangedAsync(temporaryPath, destinationPath, expectedDestinationHash, mutationAllowed, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => _inner.DeleteAsync(path, ct);
        public Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct) =>
            _inner.DeleteIfUnchangedAsync(path, expectedHash, mutationAllowed, ct);
    }
}
