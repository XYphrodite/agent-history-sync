using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;

namespace CodexHistorySync.Core.Sync;

public enum SyncMode { Pull, Push, Bidirectional }

public enum SyncProgressPhase { WaitingForLock, ScanningLocal, ReadingRemote, AuthenticatingIndex, Planning, StagingChanges, Publishing, ApplyingLocalChanges, SavingState }
public sealed record SyncProgress(SyncProgressPhase Phase, string Message);

/// <summary>How many local objects of one kind were scanned, and how many bytes they occupy.</summary>
public readonly record struct SessionKindTotals(int Count, long Bytes);

public sealed record SyncResult(
    string RemoteRevision,
    int Uploaded,
    int Downloaded,
    int Deleted,
    int Conflicts,
    bool RemoteChangedDuringAttempt,
    int SkippedOversized = 0)
{
    /// <summary>
    /// Local count and byte size per kind, taken from the same scan the plan was built on, so
    /// the reported totals describe exactly what this run compared.
    /// </summary>
    public IReadOnlyDictionary<ObjectKind, SessionKindTotals> LocalByKind { get; init; } =
        new Dictionary<ObjectKind, SessionKindTotals>();

    /// <summary>
    /// Sessions found on disk and deliberately not synchronized. Reported so the local totals
    /// above cannot read as everything this machine holds.
    /// </summary>
    public int LocalIgnored { get; init; }
}
public sealed record SyncPreview(string RemoteRevision, int LocalObjects, int RemoteObjects, int PendingChanges,
    IReadOnlySet<string> ConflictIdentities)
{
    public int Conflicts => ConflictIdentities.Count;

    /// <summary>Local session count per kind, taken from the same scan the plan was built on.</summary>
    public IReadOnlyDictionary<ObjectKind, int> LocalByKind { get; init; } = new Dictionary<ObjectKind, int>();

    /// <summary>Kinds whose local absence could not be confirmed, so their counts may be short.</summary>
    public IReadOnlySet<ObjectKind> UncertainKinds { get; init; } = new HashSet<ObjectKind>();
}
public sealed record SyncConflictResolutionResult(int RemainingConflicts, bool Exported);

public sealed class SyncConcurrencyException : InvalidOperationException
{
    public SyncConcurrencyException() : base("The remote repository changed during all five synchronization attempts.") { }
}

internal interface ISyncEngineHooks
{
    void OnBeforeLocalPublicationPrecondition();
}

public sealed class SyncEngine : IDisposable, IAsyncDisposable
{
    private const int IndexSchemaVersion = 1;
    private const string IndexObjectId = "__repository_index__";
    // GitHub hard-rejects blobs over 100 MiB; leave headroom for CHS1 framing/overhead.
    internal const long MaximumGitHubPlaintextBytes = 95L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly EnvelopeMetadata IndexMetadata = new(IndexSchemaVersion, new LogicalObjectId(IndexObjectId), ObjectKind.RepositoryIndex);
    private static readonly Regex HashPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepositoryMutexes = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _repositoryId;
    private readonly string _deviceId;
    private readonly CodexPaths _paths;
    private readonly GrokPaths? _grokPaths;
    private readonly GrokSessionScanner? _grokScanner;
    private readonly ClaudePaths? _claudePaths;
    private readonly ClaudeSessionScanner? _claudeScanner;
    private readonly ContinuePaths? _continuePaths;
    private readonly ContinueSessionScanner? _continueScanner;
    private readonly string? _annotationsDirectory;
    private readonly SessionAnnotationScanner? _annotationScanner;
    private readonly byte[] _masterKey;
    private readonly SessionScanner _scanner;
    private readonly RepositoryCrypto _crypto;
    private readonly LocalStateStore _stateStore;
    private readonly CodexHistoryWriter _historyWriter;
    private readonly ConflictStore _conflictStore;
    private readonly IStorageProvider _provider;
    private readonly string _stagingRoot;
    private readonly SemaphoreSlim _mutex;
    private readonly ISyncEngineHooks _hooks;
    private readonly IOperationDirectoryCleaner _operationCleaner;
    private readonly Action<SyncProgress>? _progress;
    private int _disposeState;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SyncEngine(string repositoryId, string deviceId, CodexPaths paths, ReadOnlyMemory<byte> masterKey,
        SessionScanner scanner, RepositoryCrypto crypto, LocalStateStore stateStore, CodexHistoryWriter historyWriter,
        ConflictStore conflictStore, IStorageProvider provider, string stagingDirectory,
        GrokPaths? grokPaths = null, GrokSessionScanner? grokScanner = null, Action<SyncProgress>? progress = null,
        ClaudePaths? claudePaths = null, ClaudeSessionScanner? claudeScanner = null,
        ContinuePaths? continuePaths = null, ContinueSessionScanner? continueScanner = null,
        string? annotationsDirectory = null, SessionAnnotationScanner? annotationScanner = null)
        : this(repositoryId, deviceId, paths, masterKey, scanner, crypto, stateStore, historyWriter, conflictStore,
            provider, stagingDirectory, NoopSyncEngineHooks.Instance, new OperationDirectoryCleaner(), grokPaths, grokScanner, progress,
            claudePaths, claudeScanner, continuePaths, continueScanner, annotationsDirectory, annotationScanner) { }

    internal SyncEngine(string repositoryId, string deviceId, CodexPaths paths, ReadOnlyMemory<byte> masterKey,
        SessionScanner scanner, RepositoryCrypto crypto, LocalStateStore stateStore, CodexHistoryWriter historyWriter,
        ConflictStore conflictStore, IStorageProvider provider, string stagingDirectory, ISyncEngineHooks hooks,
        IOperationDirectoryCleaner operationCleaner, GrokPaths? grokPaths = null, GrokSessionScanner? grokScanner = null,
        Action<SyncProgress>? progress = null, ClaudePaths? claudePaths = null, ClaudeSessionScanner? claudeScanner = null,
        ContinuePaths? continuePaths = null, ContinueSessionScanner? continueScanner = null,
        string? annotationsDirectory = null, SessionAnnotationScanner? annotationScanner = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryId)) throw new ArgumentException("Repository ID is required.", nameof(repositoryId));
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId is "." or ".." || deviceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || deviceId.Contains('/') || deviceId.Contains('\\'))
            throw new ArgumentException("Device ID is invalid.", nameof(deviceId));
        if (masterKey.Length != RepositoryCrypto.MasterKeySize) throw new ArgumentException("The repository key has an invalid length.", nameof(masterKey));
        _repositoryId = repositoryId;
        _deviceId = deviceId;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _grokPaths = grokPaths;
        _grokScanner = grokScanner ?? (grokPaths is null ? null : new GrokSessionScanner());
        _claudePaths = claudePaths;
        _claudeScanner = claudeScanner ?? (claudePaths is null ? null : new ClaudeSessionScanner());
        _continuePaths = continuePaths;
        _continueScanner = continueScanner ?? (continuePaths is null ? null : new ContinueSessionScanner());
        _annotationsDirectory = string.IsNullOrWhiteSpace(annotationsDirectory) ? null : annotationsDirectory;
        _annotationScanner = annotationScanner ?? (_annotationsDirectory is null ? null : new SessionAnnotationScanner());
        _masterKey = masterKey.ToArray();
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _historyWriter = historyWriter ?? throw new ArgumentNullException(nameof(historyWriter));
        _conflictStore = conflictStore ?? throw new ArgumentNullException(nameof(conflictStore));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (string.IsNullOrWhiteSpace(stagingDirectory)) throw new ArgumentException("A staging directory is required.", nameof(stagingDirectory));
        _stagingRoot = PathSafety.Canonicalize(stagingDirectory, nameof(stagingDirectory));
        PathSafety.EnsureOutsideCodex(_stagingRoot, paths, nameof(stagingDirectory), grokPaths, claudePaths, continuePaths);
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _operationCleaner = operationCleaner ?? throw new ArgumentNullException(nameof(operationCleaner));
        _progress = progress;
        _mutex = RepositoryMutexes.GetOrAdd(RepositorySyncLock.CanonicalStateIdentity(_stateStore.GetStatePath(repositoryId)),
            _ => new SemaphoreSlim(1, 1));
    }

    private async Task<SessionScanResult> ScanLocalAsync(CancellationToken ct)
    {
        var codexTask = _scanner.ScanDetailedAsync(_paths, ct);
        var extraTasks = new List<Task<SessionScanResult>>(3);
        if (_grokPaths is not null && _grokScanner is not null) extraTasks.Add(_grokScanner.ScanDetailedAsync(_grokPaths, ct));
        if (_claudePaths is not null && _claudeScanner is not null) extraTasks.Add(_claudeScanner.ScanDetailedAsync(_claudePaths, ct));
        if (_continuePaths is not null && _continueScanner is not null) extraTasks.Add(_continueScanner.ScanDetailedAsync(_continuePaths, ct));
        if (_annotationsDirectory is not null && _annotationScanner is not null) extraTasks.Add(_annotationScanner.ScanDetailedAsync(_annotationsDirectory, ct));
        if (extraTasks.Count == 0) return await codexTask.ConfigureAwait(false);

        await Task.WhenAll(extraTasks.Prepend(codexTask)).ConfigureAwait(false);
        var merged = await codexTask.ConfigureAwait(false);
        var objects = new List<LocalObject>(merged.Objects);
        var byId = objects.ToDictionary(item => item.Id);
        var duplicates = new HashSet<LogicalObjectId>(merged.DuplicateIds);
        var uncertain = new HashSet<ObjectKind>(merged.UncertainKinds);
        var ignored = new HashSet<LogicalObjectId>(merged.IgnoredIds);
        foreach (var task in extraTasks)
        {
            var scan = await task.ConfigureAwait(false);
            foreach (var item in scan.DuplicateIds) duplicates.Add(item);
            foreach (var kind in scan.UncertainKinds) uncertain.Add(kind);
            foreach (var item in scan.IgnoredIds) ignored.Add(item);
            foreach (var item in scan.Objects)
            {
                if (byId.TryAdd(item.Id, item)) objects.Add(item);
                else
                {
                    // A collision across agents means one of the namespaces is not disjoint any more.
                    // Drop both sides and mark each kind uncertain rather than guessing which is real.
                    duplicates.Add(item.Id);
                    uncertain.Add(item.Kind);
                    uncertain.Add(byId[item.Id].Kind);
                    if (byId.Remove(item.Id, out var prior)) objects.Remove(prior);
                }
            }
        }
        return new SessionScanResult(objects, uncertain, duplicates) { IgnoredIds = ignored };
    }

    private static IReadOnlyDictionary<ObjectKind, SessionKindTotals> SummarizeByKind(SessionScanResult scan) =>
        scan.Objects.GroupBy(item => item.Kind)
            .ToDictionary(group => group.Key, group => new SessionKindTotals(group.Count(), group.Sum(item => item.Length)));

    private async Task<IReadOnlyList<LocalObject>> ScanLocalObjectsAsync(CancellationToken ct)
    {
        var result = await ScanLocalAsync(ct).ConfigureAwait(false);
        EnsureScanUsable(result);
        return result.Objects;
    }

    public async Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Report(SyncProgressPhase.WaitingForLock, "waiting for the repository lock");
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var repositoryLock = await RepositorySyncLock.AcquireAsync(_stateStore.GetStatePath(_repositoryId), ct).ConfigureAwait(false);
            await RecoverInterruptedMutationsAsync(ct).ConfigureAwait(false);
            var raced = false;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                Report(SyncProgressPhase.ScanningLocal, "scanning agent sessions in parallel");
                var scan = await ScanLocalAsync(ct).ConfigureAwait(false);
                EnsureScanUsable(scan);
                var locals = scan.Objects;
                var stateInitialized = File.Exists(_stateStore.GetStatePath(_repositoryId));
                var baseline = await LoadBaselineAsync(ct).ConfigureAwait(false);
                Report(SyncProgressPhase.ReadingRemote, "fetching remote metadata");
                var snapshot = await _provider.ReadSnapshotMetadataAsync(ct).ConfigureAwait(false);
                Report(SyncProgressPhase.AuthenticatingIndex, "authenticating the repository index");
                var remote = await AuthenticateSnapshotAsync(snapshot, stateInitialized, baseline.Count, ct).ConfigureAwait(false);
                Report(SyncProgressPhase.Planning,
                    $"comparing {locals.Count} local and {remote.Versions.Count} remote sessions");
                var localVersions = CreateLocalVersions(scan, baseline);
                var remoteVersions = CreateRemoteVersions(remote.Versions, baseline);
                var plan = ThreeWayPlanner.CreatePlan(localVersions, remoteVersions, baseline);
                var operationId = Guid.NewGuid().ToString("N");
                var directory = Path.Combine(_stagingRoot, operationId);
                var operationCommitted = false;
                try
                {
                    Directory.CreateDirectory(directory);
                    await HistoryMutationBatch.EnsureCleanupEvidenceAsync(directory, ct).ConfigureAwait(false);
                    var successful = new Dictionary<LogicalObjectId, ObjectVersion>();
                    var deferred = new HashSet<LogicalObjectId>();
                    var entries = remote.Entries.ToDictionary(entry => entry.Id);
                    var changes = new List<EncryptedObjectChange>();
                    var attemptUploads = 0;
                    var attemptDownloaded = 0;
                    var attemptDeleted = 0;
                    var skippedOversized = 0;
                    var stagedImports = new Dictionary<LogicalObjectId, StagedImport>();
                    var pendingConflicts = new List<PendingConflict>();
                    var actionable = plan.Actions.Count(action => IsApplicablePreviewChange(action.Kind, mode));
                    Report(SyncProgressPhase.StagingChanges,
                        actionable == 0 ? "no changes detected; finalizing" : $"validating and staging {actionable} changes");
                    foreach (var action in plan.Actions.Where(action => action.Kind == SyncActionKind.Download && mode != SyncMode.Push))
                        stagedImports.Add(action.ObjectId, await StageDownloadAsync(action, locals, remote, directory, ct).ConfigureAwait(false));

                    foreach (var action in plan.Actions.Where(action => action.Kind == SyncActionKind.Conflict))
                    {
                        pendingConflicts.Add(await PrepareConflictAsync(action, locals, remote, ct).ConfigureAwait(false));
                        deferred.Add(action.ObjectId);
                    }

                    foreach (var action in plan.Actions)
                    {
                        switch (action.Kind)
                        {
                            case SyncActionKind.Accept:
                                if ((action.Remote ?? action.Local) is { } accepted) successful[action.ObjectId] = accepted;
                                break;
                            case SyncActionKind.Download when mode != SyncMode.Push:
                                successful[action.ObjectId] = action.Remote!;
                                break;
                            case SyncActionKind.ApplyTombstone when mode != SyncMode.Push:
                                successful[action.ObjectId] = action.Remote!;
                                break;
                            case SyncActionKind.Upload when mode != SyncMode.Pull:
                            {
                                var source = locals.Single(item => item.Id == action.ObjectId);
                                // Length is already the normalized (sync) size from SessionScanner.
                                if (source.Length > MaximumGitHubPlaintextBytes)
                                {
                                    deferred.Add(action.ObjectId);
                                    skippedOversized++;
                                    break;
                                }
                                try
                                {
                                    var entry = await StageEncryptedObjectAsync(source.Id, source.Kind, source.Hash, false, source.SourcePath, directory, ct).ConfigureAwait(false);
                                    entries[entry.Id] = entry;
                                    changes.Add(new EncryptedObjectChange(new LogicalObjectId(entry.OpaqueObjectId), entry.StagedPath!, false));
                                    successful[action.ObjectId] = Version(entry);
                                    attemptUploads++;
                                }
                                catch (InvalidDataException exception) when (
                                    exception.Message.Contains("changed after stable scanning", StringComparison.Ordinal) ||
                                    exception.Message.Contains("Grok session", StringComparison.OrdinalIgnoreCase) ||
                                    exception.Message.Contains("Claude session", StringComparison.OrdinalIgnoreCase) ||
                                    exception.Message.Contains("Continue session", StringComparison.OrdinalIgnoreCase))
                                {
                                    // A live session of any agent can mutate between scan and stage; defer it.
                                    deferred.Add(action.ObjectId);
                                }
                                catch (IOException exception) when (IsTransientSharingViolation(exception))
                                {
                                    // A live process may lock a session after scanning; publish the rest and retry it later.
                                    deferred.Add(action.ObjectId);
                                }
                                break;
                            }
                            case SyncActionKind.PublishTombstone when mode != SyncMode.Pull:
                            {
                                var previous = action.Baseline ?? action.Remote ?? throw new InvalidDataException("A tombstone has no prior version.");
                                var entry = await StageEncryptedObjectAsync(previous.Id, previous.Kind, EmptyHash(), true, null, directory, ct).ConfigureAwait(false);
                                entries[entry.Id] = entry;
                                changes.Add(new EncryptedObjectChange(new LogicalObjectId(entry.OpaqueObjectId), entry.StagedPath!, false));
                                successful[action.ObjectId] = Version(entry);
                                attemptUploads++;
                                break;
                            }
                            case SyncActionKind.Conflict:
                                break;
                            default:
                                deferred.Add(action.ObjectId);
                                break;
                        }
                    }

                    var revision = snapshot.Revision;
                    if (changes.Count != 0)
                    {
                        Report(SyncProgressPhase.Publishing, "publishing encrypted changes");
                        var tombstones = plan.Actions.Where(action => action.Kind == SyncActionKind.PublishTombstone &&
                            mode != SyncMode.Pull && !deferred.Contains(action.ObjectId)).ToArray();
                        var referenced = entries.Values.Select(entry => entry.OpaqueObjectId).ToHashSet(StringComparer.Ordinal);
                        foreach (var old in remote.Entries.Where(entry => !referenced.Contains(entry.OpaqueObjectId)))
                            changes.Add(new EncryptedObjectChange(new LogicalObjectId(old.OpaqueObjectId), string.Empty, true));
                        var indexPath = await StageIndexAsync(entries.Values, directory, ct).ConfigureAwait(false);
                        if (tombstones.Length != 0)
                        {
                            _hooks.OnBeforeLocalPublicationPrecondition();
                            var publicationScan = await ScanLocalAsync(ct).ConfigureAwait(false);
                            EnsureScanUsable(publicationScan);
                            if (tombstones.Any(action => !IsAbsenceConfirmed(publicationScan, action))) continue;
                        }
                        var result = await _provider.TryPublishAsync(new PublishRequest(snapshot.Revision,
                            new EncryptedIndexChange(indexPath, false), changes, "Synchronize encrypted Codex history"), ct).ConfigureAwait(false);
                        if (!result.Published)
                        {
                            raced = true;
                            if (attempt == 5) throw new SyncConcurrencyException();
                            continue;
                        }
                        revision = result.CurrentRevision;
                    }

                    foreach (var conflict in pendingConflicts)
                        await PublishConflictAsync(conflict, ct).ConfigureAwait(false);

                    var mutationPlans = new List<HistoryMutationPlan>();
                    foreach (var action in plan.Actions)
                    {
                        if (action.Kind == SyncActionKind.Download && mode != SyncMode.Push)
                        {
                            var staged = stagedImports[action.ObjectId];
                            if (staged.RelocateFrom is not null)
                                mutationPlans.Add(new HistoryMutationPlan(staged.RelocateFrom,
                                    ExpectedHistoryState.Present(staged.RelocateFrom.Hash),
                                    ExpectedHistoryState.Absent));
                            mutationPlans.Add(new HistoryMutationPlan(staged.Incoming, staged.ExpectedState,
                                ExpectedHistoryState.Present(staged.Incoming.Hash)));
                        }
                        else if (action.Kind == SyncActionKind.ApplyTombstone && mode != SyncMode.Push && action.Local is not null)
                        {
                            var source = locals.Single(item => item.Id == action.ObjectId);
                            mutationPlans.Add(new HistoryMutationPlan(source, ExpectedHistoryState.Present(action.Baseline!.PlaintextHash),
                                ExpectedHistoryState.Absent));
                        }
                    }
                    HistoryMutationBatch? mutationBatch = null;
                    var stateSaved = false;
                    if (mutationPlans.Count != 0)
                        mutationBatch = await HistoryMutationBatch.PrepareAsync(_historyWriter, directory, operationId, mutationPlans, ct).ConfigureAwait(false);
                    try
                    {
                        if (mutationPlans.Count != 0)
                            Report(SyncProgressPhase.ApplyingLocalChanges, "applying authenticated remote changes locally");
                        foreach (var action in plan.Actions)
                        {
                            if (action.Kind == SyncActionKind.Download && mode != SyncMode.Push)
                            {
                                var staged = stagedImports[action.ObjectId];
                                if (staged.RelocateFrom is not null)
                                {
                                    await mutationBatch!.BeginApplyAsync(staged.RelocateFrom, ct).ConfigureAwait(false);
                                    if (await _historyWriter.ApplyTombstoneAsync(staged.RelocateFrom, staged.RelocateFrom.Hash,
                                            operationId, ct).ConfigureAwait(false) == TombstoneApplyResult.Conflict)
                                    {
                                        await mutationBatch.MarkSkippedAsync(staged.RelocateFrom, ct).ConfigureAwait(false);
                                        await mutationBatch.MarkSkippedAsync(staged.Incoming, ct).ConfigureAwait(false);
                                        successful.Remove(action.ObjectId);
                                        deferred.Add(action.ObjectId);
                                        var refreshed = await ScanLocalObjectsAsync(ct).ConfigureAwait(false);
                                        var current = refreshed.SingleOrDefault(item => item.Id == action.ObjectId);
                                        var conflict = action with
                                        {
                                            Kind = SyncActionKind.Conflict,
                                            Local = current is null
                                                ? new ObjectVersion(action.ObjectId, action.Remote!.Kind, EmptyHash(), "local:deleted", true)
                                                : new ObjectVersion(current.Id, current.Kind, current.Hash, "local:" + current.Hash.Hex, false)
                                        };
                                        await PublishConflictAsync(await PrepareConflictAsync(conflict, refreshed, remote, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                                        continue;
                                    }
                                    await mutationBatch.MarkAppliedAsync(staged.RelocateFrom, ct).ConfigureAwait(false);
                                    await mutationBatch.BeginApplyAsync(staged.Incoming, ct).ConfigureAwait(false);
                                    if (await ApplyStagedImportAsync(staged, operationId, ct).ConfigureAwait(false) == ImportApplyResult.Conflict)
                                    {
                                        // Old kind was already removed; roll the dual mutation back before converting.
                                        throw new IOException("Local history changed during cross-kind download.");
                                    }
                                    await mutationBatch.MarkAppliedAsync(staged.Incoming, ct).ConfigureAwait(false);
                                }
                                else
                                {
                                    await mutationBatch!.BeginApplyAsync(action.ObjectId, ct).ConfigureAwait(false);
                                    if (await ApplyStagedImportAsync(staged, operationId, ct).ConfigureAwait(false) == ImportApplyResult.Conflict)
                                    {
                                        await mutationBatch.MarkSkippedAsync(action.ObjectId, ct).ConfigureAwait(false);
                                        successful.Remove(action.ObjectId);
                                        deferred.Add(action.ObjectId);
                                        var refreshed = await ScanLocalObjectsAsync(ct).ConfigureAwait(false);
                                        var current = refreshed.SingleOrDefault(item => item.Id == action.ObjectId);
                                        var conflict = action with
                                        {
                                            Kind = SyncActionKind.Conflict,
                                            Local = current is null
                                                ? new ObjectVersion(action.ObjectId, action.Remote!.Kind, EmptyHash(), "local:deleted", true)
                                                : new ObjectVersion(current.Id, current.Kind, current.Hash, "local:" + current.Hash.Hex, false)
                                        };
                                        await PublishConflictAsync(await PrepareConflictAsync(conflict, refreshed, remote, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                                        continue;
                                    }
                                    await mutationBatch.MarkAppliedAsync(action.ObjectId, ct).ConfigureAwait(false);
                                }
                                attemptDownloaded++;
                            }
                            else if (action.Kind == SyncActionKind.ApplyTombstone && mode != SyncMode.Push && action.Local is not null)
                            {
                                await mutationBatch!.BeginApplyAsync(action.ObjectId, ct).ConfigureAwait(false);
                                var source = locals.Single(item => item.Id == action.ObjectId);
                                if (await _historyWriter.ApplyTombstoneAsync(source, action.Baseline!.PlaintextHash, operationId, ct).ConfigureAwait(false) == TombstoneApplyResult.Conflict)
                                {
                                    await mutationBatch.MarkSkippedAsync(action.ObjectId, ct).ConfigureAwait(false);
                                    successful.Remove(action.ObjectId);
                                    deferred.Add(action.ObjectId);
                                    var refreshed = await ScanLocalObjectsAsync(ct).ConfigureAwait(false);
                                    var current = refreshed.SingleOrDefault(item => item.Id == action.ObjectId)
                                        ?? throw new InvalidDataException("The concurrent local tombstone conflict could not be scanned stably.");
                                    var conflict = action with
                                    {
                                        Kind = SyncActionKind.Conflict,
                                        Local = new ObjectVersion(current.Id, current.Kind, current.Hash, "local:" + current.Hash.Hex, false)
                                    };
                                    await PublishConflictAsync(await PrepareConflictAsync(conflict, refreshed, remote, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                                    continue;
                                }
                                await mutationBatch.MarkAppliedAsync(action.ObjectId, ct).ConfigureAwait(false);
                                attemptDeleted++;
                            }
                        }

                        var next = baseline.ToDictionary(pair => pair.Key, pair => pair.Value);
                        foreach (var action in plan.Actions)
                            if (!deferred.Contains(action.ObjectId) && successful.TryGetValue(action.ObjectId, out var version)) next[action.ObjectId] = version;
                        if (!remote.HasAuthenticatedIndex && changes.Count == 0 && baseline.Count == 0)
                            return new SyncResult(revision, attemptUploads, attemptDownloaded, attemptDeleted,
                                await CountUnresolvedConflictsAsync(ct).ConfigureAwait(false), raced, skippedOversized)
                            {
                                LocalByKind = SummarizeByKind(scan),
                                LocalIgnored = scan.IgnoredIds.Count
                            };
                        Report(SyncProgressPhase.SavingState, "saving synchronization state");
                        await _stateStore.SaveAsync(new DeviceState(LocalStateStore.CurrentSchemaVersion, _repositoryId,
                            next.Values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray()), ct).ConfigureAwait(false);
                        stateSaved = true;
                        operationCommitted = true;
                        return new SyncResult(revision, attemptUploads, attemptDownloaded, attemptDeleted,
                            await CountUnresolvedConflictsAsync(ct).ConfigureAwait(false), raced, skippedOversized)
                        {
                            LocalByKind = SummarizeByKind(scan),
                            LocalIgnored = scan.IgnoredIds.Count
                        };
                    }
                    catch (Exception primary) when (mutationBatch is not null && !stateSaved)
                    {
                        try { await mutationBatch.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception rollback)
                        {
                            throw new AtomicMutationException("Synchronization failed and the local mutation batch could not be fully rolled back.",
                                new AggregateException(primary, rollback), [directory]);
                        }
                        throw;
                    }
                }
                finally
                {
                    if (operationCommitted) TryCleanupOperationBestEffort(directory);
                    else
                    {
                        try
                        {
                            if (!IsPathConfirmedAbsent(directory) &&
                                IsPathConfirmedAbsent(Path.Combine(directory, HistoryMutationBatch.MarkerFileName)))
                                TryCleanupOperationBestEffort(directory);
                        }
                        catch (Exception exception) when (IsExpectedCleanupFailure(exception)) { }
                    }
                }
            }
            throw new SyncConcurrencyException();
        }
        finally { _mutex.Release(); }
    }

    public async Task<SyncPreview> PreviewAsync(SyncMode mode, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var scan = await ScanLocalAsync(ct).ConfigureAwait(false);
            EnsureScanUsable(scan);
            var stateInitialized = File.Exists(_stateStore.GetStatePath(_repositoryId));
            var baseline = await LoadBaselineAsync(ct).ConfigureAwait(false);
            var snapshot = await _provider.ReadSnapshotMetadataAsync(ct).ConfigureAwait(false);
            var remote = await AuthenticateSnapshotAsync(snapshot, stateInitialized, baseline.Count, ct).ConfigureAwait(false);
            var plan = ThreeWayPlanner.CreatePlan(CreateLocalVersions(scan, baseline),
                CreateRemoteVersions(remote.Versions, baseline), baseline);
            var conflicts = plan.Actions.Where(action => action.Kind == SyncActionKind.Conflict)
                .Select(ConflictIdentity).ToHashSet(StringComparer.Ordinal);
            var pending = plan.Actions.Count(action => IsApplicablePreviewChange(action.Kind, mode));
            return new SyncPreview(snapshot.Revision, scan.Objects.Count,
                remote.Versions.Values.Count(value => !value.IsDeleted), pending, conflicts)
            {
                LocalByKind = scan.Objects.GroupBy(item => item.Kind)
                    .ToDictionary(group => group.Key, group => group.Count()),
                UncertainKinds = scan.UncertainKinds.ToHashSet()
            };
        }
        finally { _mutex.Release(); }
    }

    public async Task<SyncConflictResolutionResult> ResolveConflictAsync(string conflictId,
        ConflictResolution resolution, string? exportDirectory, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var repositoryLock = await RepositorySyncLock.AcquireAsync(
                _stateStore.GetStatePath(_repositoryId), ct).ConfigureAwait(false);
            await RecoverInterruptedMutationsAsync(ct).ConfigureAwait(false);
            var conflict = (await _conflictStore.ListAsync(ct).ConfigureAwait(false))
                .SingleOrDefault(item => StringComparer.Ordinal.Equals(item.Id, conflictId))
                ?? throw new FileNotFoundException("The requested conflict does not exist.", conflictId);
            var selected = await _conflictStore.ResolveAsync(conflictId, resolution, exportDirectory, _crypto,
                _masterKey, ct).ConfigureAwait(false);
            if (resolution == ConflictResolution.ExportBoth)
                return new SyncConflictResolutionResult(await CountUnresolvedConflictsAsync(ct).ConfigureAwait(false), true);

            var scan = await ScanLocalAsync(ct).ConfigureAwait(false);
            EnsureScanUsable(scan);
            ValidateConflictLocal(conflict, scan, resolution);
            var baseline = await LoadBaselineAsync(ct).ConfigureAwait(false);
            var snapshot = await _provider.ReadSnapshotMetadataAsync(ct).ConfigureAwait(false);
            var remote = await AuthenticateSnapshotAsync(snapshot,
                File.Exists(_stateStore.GetStatePath(_repositoryId)), baseline.Count, ct).ConfigureAwait(false);
            ValidateConflictRemote(conflict, remote, resolution);

            var chooseLocal = resolution == ConflictResolution.KeepLocal;
            var deleted = chooseLocal ? conflict.Provenance.LocalDeleted : conflict.Provenance.RemoteDeleted;
            var hash = chooseLocal ? conflict.Provenance.LocalHash : conflict.Provenance.RemoteHash;
            var selectedMetadata = chooseLocal ? ConflictStore.LocalMetadata(conflict.Provenance) :
                ConflictStore.RemoteMetadata(conflict.Provenance);
            var selectedPath = selected.SelectedEncryptedPath
                ?? throw new InvalidDataException("The selected conflict envelope is missing.");
            var plaintext = await AuthenticateConflictSelectionAsync(selectedPath, selectedMetadata,
                hash, deleted, ct).ConfigureAwait(false);
            var id = selectedMetadata.ObjectId;
            var kind = selectedMetadata.Kind;
            var operationId = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(_stagingRoot, operationId);
            Directory.CreateDirectory(directory);
            await HistoryMutationBatch.EnsureCleanupEvidenceAsync(directory, ct).ConfigureAwait(false);
            var operationCommitted = false;
            try
            {
                var currentRemote = remote.Versions.GetValueOrDefault(id);
                var remoteAlreadySelected = currentRemote is not null && currentRemote.IsDeleted == deleted &&
                    currentRemote.Kind == kind && BackupStore.HashEquals(currentRemote.PlaintextHash, hash);
                var selectedVersion = remoteAlreadySelected
                    ? currentRemote!
                    : await PublishResolvedRemoteAsync(snapshot, remote, selectedMetadata, selectedPath, hash, deleted,
                        directory, ct).ConfigureAwait(false);

                var currentLocal = scan.Objects.SingleOrDefault(item => item.Id == id);
                var localAlreadySelected = deleted
                    ? currentLocal is null && scan.IsAbsenceConfirmed(kind)
                    : currentLocal is not null && currentLocal.Kind == kind &&
                      BackupStore.HashEquals(currentLocal.Hash, hash);
                HistoryMutationBatch? batch = null;
                if (!localAlreadySelected)
                {
                    var destination = currentLocal is not null && currentLocal.Kind == kind
                        ? currentLocal.SourcePath
                        : kind == ObjectKind.GrokSession
                            ? ResolveGrokDestination(id, plaintext)
                            : kind == ObjectKind.SessionAnnotations
                            ? ResolveAnnotationDestination(id, plaintext)
                            : kind == ObjectKind.ClaudeSession
                            ? ResolveClaudeDestination(id, plaintext)
                            : kind == ObjectKind.ContinueSession
                            ? ResolveContinueDestination(id, plaintext)
                            : Path.Combine(kind switch
                    {
                        ObjectKind.ActiveSession => _paths.Sessions,
                        ObjectKind.ArchivedSession => _paths.ArchivedSessions,
                        _ => throw new InvalidDataException("Only session conflicts can be resolved.")
                    }, id.Value + ".jsonl");
                    var target = new LocalObject(id, kind, destination, hash, plaintext.LongLength, DateTimeOffset.UtcNow);
                    var before = currentLocal is null || currentLocal.Kind != kind
                        ? ExpectedHistoryState.Absent
                        : ExpectedHistoryState.Present(currentLocal.Hash);
                    var after = deleted ? ExpectedHistoryState.Absent : ExpectedHistoryState.Present(hash);
                    var plans = new List<HistoryMutationPlan>();
                    if (currentLocal is not null && currentLocal.Kind != kind)
                        plans.Add(new HistoryMutationPlan(currentLocal, ExpectedHistoryState.Present(currentLocal.Hash),
                            ExpectedHistoryState.Absent));
                    if (!deleted || currentLocal is not null && currentLocal.Kind == kind)
                        plans.Add(new HistoryMutationPlan(target, before, after));
                    batch = await HistoryMutationBatch.PrepareAsync(_historyWriter, directory, operationId,
                        plans, ct).ConfigureAwait(false);
                    try
                    {
                        if (currentLocal is not null && currentLocal.Kind != kind)
                        {
                            await batch.BeginApplyAsync(currentLocal, ct).ConfigureAwait(false);
                            if (await _historyWriter.ApplyTombstoneAsync(currentLocal, currentLocal.Hash, operationId,
                                    ct).ConfigureAwait(false) == TombstoneApplyResult.Conflict)
                                throw new IOException("Local history changed during conflict resolution.");
                            await batch.MarkAppliedAsync(currentLocal, ct).ConfigureAwait(false);
                        }
                        if (deleted && currentLocal is not null && currentLocal.Kind == kind)
                        {
                            await batch.BeginApplyAsync(target, ct).ConfigureAwait(false);
                            if (currentLocal is not null && await _historyWriter.ApplyTombstoneAsync(currentLocal,
                                    currentLocal.Hash, operationId, ct).ConfigureAwait(false) == TombstoneApplyResult.Conflict)
                                throw new IOException("Local history changed during conflict resolution.");
                            await batch.MarkAppliedAsync(target, ct).ConfigureAwait(false);
                        }
                        else if (!deleted)
                        {
                            await batch.BeginApplyAsync(target, ct).ConfigureAwait(false);
                            await using var input = new MemoryStream(plaintext, false);
                            if (await _historyWriter.ImportAsync(target, input, operationId, before, ct).ConfigureAwait(false) == ImportApplyResult.Conflict)
                                throw new IOException("Local history changed during conflict resolution.");
                            await batch.MarkAppliedAsync(target, ct).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        await batch.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                }

                try
                {
                    var finalScan = await ScanLocalAsync(ct).ConfigureAwait(false);
                    EnsureScanUsable(finalScan);
                    ValidateSelectedLocal(selectedMetadata, finalScan, deleted, hash);
                    var next = baseline.ToDictionary(pair => pair.Key, pair => pair.Value);
                    next[id] = selectedVersion;
                    await _stateStore.SaveAsync(new DeviceState(LocalStateStore.CurrentSchemaVersion, _repositoryId,
                        next.Values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToArray()), ct).ConfigureAwait(false);
                }
                catch
                {
                    if (batch is not null) await batch.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                operationCommitted = true;
                await _conflictStore.RetireAsync(conflictId, ct).ConfigureAwait(false);
                return new SyncConflictResolutionResult(await CountUnresolvedConflictsAsync(ct).ConfigureAwait(false), false);
            }
            finally
            {
                if (operationCommitted || IsPathConfirmedAbsent(Path.Combine(directory, HistoryMutationBatch.MarkerFileName)))
                    TryCleanupOperationBestEffort(directory);
            }
        }
        finally { _mutex.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) == 0)
        {
            _mutex.Wait();
            try
            {
                CryptographicOperations.ZeroMemory(_masterKey);
                Volatile.Write(ref _disposeState, 2);
                _disposeCompletion.TrySetResult();
            }
            finally { _mutex.Release(); }
        }
        else _disposeCompletion.Task.GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) == 0)
        {
            await _mutex.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                CryptographicOperations.ZeroMemory(_masterKey);
                Volatile.Write(ref _disposeState, 2);
                _disposeCompletion.TrySetResult();
            }
            finally { _mutex.Release(); }
        }
        else await _disposeCompletion.Task.ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposeState) != 0, this);

    private static bool IsApplicablePreviewChange(SyncActionKind kind, SyncMode mode) => kind switch
    {
        SyncActionKind.Upload or SyncActionKind.PublishTombstone => mode != SyncMode.Pull,
        SyncActionKind.Download or SyncActionKind.ApplyTombstone => mode != SyncMode.Push,
        _ => false
    };

    private static void ValidateConflictLocal(ConflictRecord conflict, SessionScanResult scan,
        ConflictResolution resolution)
    {
        var provenance = conflict.Provenance;
        var localMetadata = ConflictStore.LocalMetadata(provenance);
        var remoteMetadata = ConflictStore.RemoteMetadata(provenance);
        var current = scan.Objects.SingleOrDefault(item => item.Id == localMetadata.ObjectId);
        var matches = provenance.LocalDeleted
            ? current is null && scan.IsAbsenceConfirmed(localMetadata.Kind)
            : current is not null && current.Kind == localMetadata.Kind &&
              BackupStore.HashEquals(current.Hash, provenance.LocalHash);
        if (!matches && resolution == ConflictResolution.KeepRemote)
            matches = provenance.RemoteDeleted
                ? current is null && scan.IsAbsenceConfirmed(remoteMetadata.Kind)
                : current is not null && current.Kind == remoteMetadata.Kind &&
                  BackupStore.HashEquals(current.Hash, provenance.RemoteHash);
        if (!matches) throw new InvalidOperationException("Local history changed after the conflict was recorded.");
    }

    private static void ValidateConflictRemote(ConflictRecord conflict, AuthenticatedSnapshot remote,
        ConflictResolution resolution)
    {
        var provenance = conflict.Provenance;
        var localMetadata = ConflictStore.LocalMetadata(provenance);
        var remoteMetadata = ConflictStore.RemoteMetadata(provenance);
        var current = remote.Versions.GetValueOrDefault(remoteMetadata.ObjectId);
        var matches = provenance.RemoteDeleted
            ? current is null || current.IsDeleted && current.Kind == remoteMetadata.Kind &&
              BackupStore.HashEquals(current.PlaintextHash, provenance.RemoteHash)
            : current is not null && !current.IsDeleted && current.Kind == remoteMetadata.Kind &&
              BackupStore.HashEquals(current.PlaintextHash, provenance.RemoteHash);
        if (!matches && resolution == ConflictResolution.KeepLocal)
            matches = provenance.LocalDeleted
                ? current is null || current.IsDeleted && current.Kind == localMetadata.Kind &&
                  BackupStore.HashEquals(current.PlaintextHash, provenance.LocalHash)
                : current is not null && !current.IsDeleted && current.Kind == localMetadata.Kind &&
                  BackupStore.HashEquals(current.PlaintextHash, provenance.LocalHash);
        if (!matches) throw new InvalidOperationException("Remote history changed after the conflict was recorded.");
    }

    private static void ValidateSelectedLocal(EnvelopeMetadata selectedMetadata, SessionScanResult scan, bool deleted,
        ContentHash hash)
    {
        var current = scan.Objects.SingleOrDefault(item => item.Id == selectedMetadata.ObjectId);
        var matches = deleted
            ? current is null && scan.IsAbsenceConfirmed(selectedMetadata.Kind)
            : current is not null && current.Kind == selectedMetadata.Kind &&
              BackupStore.HashEquals(current.Hash, hash);
        if (!matches) throw new IOException("Local history changed before conflict resolution could be committed.");
    }

    private async Task<byte[]> AuthenticateConflictSelectionAsync(string path, EnvelopeMetadata metadata,
        ContentHash expectedHash, bool deleted, CancellationToken ct)
    {
        byte[] plaintext;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new MemoryStream();
            await _crypto.DecryptAsync(input, output, _masterKey, metadata, ct).ConfigureAwait(false);
            plaintext = output.ToArray();
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The selected conflict envelope could not be authenticated.", exception);
        }
        if (!BackupStore.HashEquals(Sha256(plaintext), expectedHash))
            throw new InvalidDataException("The selected conflict plaintext does not match its recorded hash.");
        if (deleted)
        {
            if (plaintext.Length != 0) throw new InvalidDataException("A deleted conflict side contains plaintext.");
        }
        else ValidateHistoryPayload(metadata.Kind, plaintext, metadata.ObjectId, ct);
        return plaintext;
    }

    /// <summary>
    /// Where one annotation belongs on this machine. The path is built from the annotation and
    /// the configured directory, never from anything the incoming object carries.
    /// </summary>
    private string ResolveAnnotationDestination(LogicalObjectId id, byte[] plaintext)
    {
        if (_annotationsDirectory is null)
            throw new InvalidOperationException("An annotations directory is not configured.");
        if (!SessionAnnotationPackage.TryReadPackage(plaintext, out var key, out _))
            throw new InvalidDataException("The conflict payload is not a readable session annotation.");
        if (!string.Equals(SessionAnnotationPackage.ToLogicalId(key), id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Annotation payload id does not match the logical object id.");
        return SessionAnnotationPackage.DestinationPath(_annotationsDirectory, key);
    }

    private string ResolveClaudeDestination(LogicalObjectId id, byte[] plaintext)
    {
        if (_claudePaths is null) throw new InvalidOperationException("Claude paths are not configured.");
        var package = ClaudeSessionPackage.Parse(plaintext);
        if (!string.Equals(ClaudeSessionPackage.ToLogicalId(package.SessionId), id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Claude conflict payload id does not match the logical object id.");
        return _claudePaths.SessionFilePath(package.Project, package.SessionId);
    }

    private string ResolveContinueDestination(LogicalObjectId id, byte[] plaintext)
    {
        if (_continuePaths is null) throw new InvalidOperationException("Continue paths are not configured.");
        var package = ContinueSessionPackage.Parse(plaintext);
        if (!string.Equals(ContinueSessionPackage.ToLogicalId(package.SessionId), id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Continue conflict payload id does not match the logical object id.");
        return _continuePaths.SessionFilePath(package.SessionId);
    }

    private string ResolveGrokDestination(LogicalObjectId id, byte[] plaintext)
    {
        if (_grokPaths is null) throw new InvalidOperationException("Grok paths are not configured.");
        var package = GrokSessionPackage.Parse(plaintext);
        if (!string.Equals(GrokSessionPackage.ToLogicalId(package.SessionId), id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Grok conflict payload id does not match the logical object id.");
        return GrokSessionPackage.ChatHistoryPath(_grokPaths.SessionDirectory(package.Cwd, package.SessionId));
    }

    private async Task<ObjectVersion> PublishResolvedRemoteAsync(RemoteSnapshot snapshot,
        AuthenticatedSnapshot remote, EnvelopeMetadata selectedMetadata, string selectedPath, ContentHash hash, bool deleted,
        string directory, CancellationToken ct)
    {
        var id = selectedMetadata.ObjectId;
        var ciphertext = await File.ReadAllBytesAsync(selectedPath, ct).ConfigureAwait(false);
        var opaque = Sha256(ciphertext).Hex;
        var resolved = new IndexEntry(id, selectedMetadata.Kind, hash, deleted, opaque,
            _deviceId + ":resolved:" + Guid.NewGuid().ToString("N"), selectedPath);
        var entries = remote.Entries.ToDictionary(entry => entry.Id);
        entries[id] = resolved;
        var changes = new List<EncryptedObjectChange>
        {
            new(new LogicalObjectId(opaque), selectedPath, false)
        };
        var referenced = entries.Values.Select(entry => entry.OpaqueObjectId).ToHashSet(StringComparer.Ordinal);
        foreach (var old in remote.Entries.Where(entry => !referenced.Contains(entry.OpaqueObjectId)))
            changes.Add(new EncryptedObjectChange(new LogicalObjectId(old.OpaqueObjectId), string.Empty, true));
        var indexPath = await StageIndexAsync(entries.Values, directory, ct).ConfigureAwait(false);
        var published = await _provider.TryPublishAsync(new PublishRequest(snapshot.Revision,
            new EncryptedIndexChange(indexPath, false), changes, "Resolve encrypted Codex history conflict"),
            ct).ConfigureAwait(false);
        if (!published.Published)
            throw new InvalidOperationException("Remote history changed during conflict resolution.");
        return Version(resolved);
    }

    private async Task<Dictionary<LogicalObjectId, ObjectVersion>> LoadBaselineAsync(CancellationToken ct)
    {
        try { return (await _stateStore.LoadAsync(_repositoryId, ct).ConfigureAwait(false)).Objects.ToDictionary(value => value.Id); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private async Task<int> CountUnresolvedConflictsAsync(CancellationToken ct) =>
        (await _conflictStore.ListAsync(ct).ConfigureAwait(false))
        .Select(record => ConflictStore.GetIdentity(record.Provenance)).Distinct(StringComparer.Ordinal).Count();

    private static string ConflictIdentity(SyncAction action)
    {
        var localKind = action.Local?.Kind ?? action.Baseline?.Kind ?? action.Remote?.Kind
            ?? throw new InvalidDataException("A conflict has no local object kind.");
        var remoteKind = action.Remote?.Kind ?? action.Baseline?.Kind ?? action.Local?.Kind
            ?? throw new InvalidDataException("A conflict has no remote object kind.");
        var empty = EmptyHash();
        return ConflictStore.GetIdentity(new ConflictProvenance(
            new EnvelopeMetadata(IndexSchemaVersion, action.ObjectId, localKind),
            action.Local?.PlaintextHash ?? empty, action.Remote?.PlaintextHash ?? empty,
            action.Baseline?.PlaintextHash ?? empty, "preview", "preview", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, action.Local?.IsDeleted ?? true, action.Remote?.IsDeleted ?? true,
            new EnvelopeMetadata(IndexSchemaVersion, action.ObjectId, localKind),
            new EnvelopeMetadata(IndexSchemaVersion, action.ObjectId, remoteKind)));
    }

    private async Task RecoverInterruptedMutationsAsync(CancellationToken ct)
    {
        string[] operationDirectories;
        string[] cleanupEvidence;
        try
        {
            operationDirectories = Directory.EnumerateDirectories(_stagingRoot).ToArray();
            cleanupEvidence = Directory.EnumerateFiles(_stagingRoot, ".*" + HistoryMutationBatch.CleanupEvidenceExtension).ToArray();
        }
        catch (DirectoryNotFoundException) { return; }
        var recoveryTargets = operationDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var evidence in cleanupEvidence)
        {
            var name = Path.GetFileName(evidence);
            var operationId = name[1..^HistoryMutationBatch.CleanupEvidenceExtension.Length];
            try { PathSafety.ValidateFileComponent(operationId, nameof(operationId)); }
            catch (ArgumentException exception)
            {
                throw new IOException("A cleanup evidence file has an invalid operation identifier.", exception);
            }
            recoveryTargets.Add(Path.Combine(_stagingRoot, operationId));
        }
        var baseline = await LoadBaselineAsync(ct).ConfigureAwait(false);
        foreach (var directory in recoveryTargets.OrderBy(path => path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (ProbeOperationDirectory(directory) &&
                ProbeMarker(Path.Combine(directory, HistoryMutationBatch.MarkerFileName)))
                await HistoryMutationBatch.RecoverAsync(_historyWriter, directory, baseline, ct).ConfigureAwait(false);
            CleanupOperationStrict(directory);
        }
    }

    private static bool ProbeOperationDirectory(string directory)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(directory); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
        {
            throw new IOException("The recovered synchronization operation target could not be classified safely.", exception);
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The recovered synchronization operation target is a reparse point.");
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("The recovered synchronization operation target is not a real directory.");
        return true;
    }

    private static bool ProbeMarker(string markerPath)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(markerPath); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
        {
            throw new IOException("The local mutation marker could not be confirmed absent or opened safely.", exception);
        }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The local mutation marker is not a regular file.");
        try
        {
            using var input = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
        {
            throw new IOException("The local mutation marker could not be opened safely.", exception);
        }
    }

    private void CleanupOperationStrict(string directory)
    {
        try
        {
            _operationCleaner.Delete(directory, HistoryMutationBatch.MarkerFileName);
            if (!IsPathConfirmedAbsent(directory))
                throw new IOException("Recovered synchronization operation cleanup did not remove its directory.");
        }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception))
        {
            throw new IOException("Recovered synchronization operation cleanup could not be completed before provider access.", exception);
        }
        TryDeleteCleanupEvidence(HistoryMutationBatch.CleanupEvidencePath(directory));
    }

    private void TryCleanupOperationBestEffort(string directory)
    {
        try
        {
            _operationCleaner.Delete(directory, HistoryMutationBatch.MarkerFileName);
            if (IsPathConfirmedAbsent(directory)) TryDeleteCleanupEvidence(HistoryMutationBatch.CleanupEvidencePath(directory));
        }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception)) { }
    }

    private static void TryDeleteCleanupEvidence(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (IsExpectedCleanupFailure(exception)) { }
    }

    private static bool IsExpectedCleanupFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.SecurityException;

    private static bool IsPathConfirmedAbsent(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private static Dictionary<LogicalObjectId, ObjectVersion> CreateLocalVersions(
        SessionScanResult scan, IReadOnlyDictionary<LogicalObjectId, ObjectVersion> baseline)
    {
        var objects = scan.Objects;
        if (objects.Any(item => StringComparer.Ordinal.Equals(item.Id.Value, IndexObjectId)))
            throw new InvalidDataException("Local history uses the reserved repository-index object ID.");
        var result = objects.ToDictionary(item => item.Id,
            item => new ObjectVersion(item.Id, item.Kind, item.Hash, "local:" + item.Hash.Hex, false));
        foreach (var missing in baseline.Values.Where(item => !result.ContainsKey(item.Id)))
            // An ignored session is still on disk, so it keeps its baseline version: reporting it
            // deleted would publish a tombstone and erase it everywhere else.
            result[missing.Id] = scan.IsAbsenceConfirmed(missing.Kind) && !scan.IsIgnored(missing.Id)
                ? new ObjectVersion(missing.Id, missing.Kind, EmptyHash(), "local:deleted", true)
                : missing;
        return result;
    }

    private static Dictionary<LogicalObjectId, ObjectVersion> CreateRemoteVersions(
        IReadOnlyDictionary<LogicalObjectId, ObjectVersion> authenticated,
        IReadOnlyDictionary<LogicalObjectId, ObjectVersion> baseline)
    {
        var result = authenticated.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var missing in baseline.Values.Where(item => !result.ContainsKey(item.Id)))
            result[missing.Id] = new ObjectVersion(missing.Id, missing.Kind, EmptyHash(), "remote:absent", true);
        return result;
    }

    private static bool IsAbsenceConfirmed(SessionScanResult scan, SyncAction action)
    {
        var kind = action.Local?.Kind ?? action.Baseline?.Kind ?? action.Remote?.Kind
            ?? throw new InvalidDataException("A tombstone has no object kind.");
        return scan.IsAbsenceConfirmed(kind) && !scan.IsIgnored(action.ObjectId)
            && scan.Objects.All(item => item.Id != action.ObjectId);
    }

    private async Task<AuthenticatedSnapshot> AuthenticateSnapshotAsync(RemoteSnapshot snapshot, bool stateInitialized,
        int baselineCount, CancellationToken ct)
    {
        if (snapshot.IndexCiphertext is null)
        {
            if (snapshot.EffectiveObjectReferences.Count != 0) throw new InvalidDataException("Remote ciphertext exists without an authenticated repository index.");
            if (stateInitialized || baselineCount != 0 || !string.IsNullOrEmpty(snapshot.Revision))
                throw new InvalidDataException("An initialized repository response is missing its authenticated index.");
            return new AuthenticatedSnapshot(false, [], new Dictionary<LogicalObjectId, ObjectVersion>(), snapshot);
        }
        byte[] plaintext;
        try
        {
            await using var input = new MemoryStream(snapshot.IndexCiphertext, false);
            await using var output = new MemoryStream();
            await _crypto.DecryptAsync(input, output, _masterKey, IndexMetadata, ct).ConfigureAwait(false);
            plaintext = output.ToArray();
        }
        catch (CryptographicException exception) { throw new InvalidDataException("The repository index could not be authenticated.", exception); }

        var entries = ParseIndex(plaintext);
        HashSet<string> encrypted;
        try { encrypted = snapshot.EffectiveObjectReferences.Select(item => item.Value).ToHashSet(StringComparer.Ordinal); }
        catch (ArgumentException exception) { throw new InvalidDataException("Remote snapshot contains duplicate opaque object references.", exception); }
        if (encrypted.Count != snapshot.EffectiveObjectReferences.Count)
            throw new InvalidDataException("Remote snapshot contains duplicate opaque object references.");
        var referenced = entries.Select(entry => entry.OpaqueObjectId).ToHashSet(StringComparer.Ordinal);
        if (referenced.Count != entries.Count) throw new InvalidDataException("Repository index contains duplicate opaque object references.");
        if (!referenced.SetEquals(encrypted)) throw new InvalidDataException("Remote ciphertext set does not exactly match the authenticated index.");

        var versions = new Dictionary<LogicalObjectId, ObjectVersion>();
        foreach (var entry in entries)
            versions.Add(entry.Id, Version(entry));
        return new AuthenticatedSnapshot(true, entries, versions, snapshot);
    }

    private async Task<RemotePayload> ReadAuthenticatedRemoteObjectAsync(
        AuthenticatedSnapshot remote, IndexEntry entry, CancellationToken ct)
    {
        if (remote.Payloads.TryGetValue(entry.Id, out var cached)) return cached;
        var ciphertext = await _provider.ReadObjectAsync(remote.Snapshot,
            new LogicalObjectId(entry.OpaqueObjectId), ct).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(Sha256(ciphertext).Hex, entry.OpaqueObjectId))
            throw new InvalidDataException("Encrypted object ID does not match its ciphertext hash.");
        byte[] plaintext;
        try
        {
            await using var input = new MemoryStream(ciphertext, false);
            await using var output = new MemoryStream();
            await _crypto.DecryptAsync(input, output, _masterKey,
                new EnvelopeMetadata(IndexSchemaVersion, entry.Id, entry.Kind), ct).ConfigureAwait(false);
            plaintext = output.ToArray();
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"Encrypted object '{entry.Id.Value}' could not be authenticated.", exception);
        }
        if (!StringComparer.Ordinal.Equals(Sha256(plaintext).Hex, entry.PlaintextHash.Hex))
            throw new InvalidDataException("Encrypted object plaintext hash does not match the authenticated index.");
        var payload = new RemotePayload(ciphertext, plaintext);
        remote.Payloads.Add(entry.Id, payload);
        return payload;
    }

    private IReadOnlyList<IndexEntry> ParseIndex(byte[] plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != IndexSchemaVersion)
                throw new InvalidDataException("Repository index schema is unsupported.");
            if (!StringComparer.Ordinal.Equals(root.GetProperty("repositoryId").GetString(), _repositoryId))
                throw new InvalidDataException("Repository index belongs to a different repository.");
            var result = new List<IndexEntry>();
            var ids = new HashSet<LogicalObjectId>();
            string? previousId = null;
            foreach (var value in root.GetProperty("objects").EnumerateArray())
            {
                var idText = value.GetProperty("id").GetString();
                var hash = value.GetProperty("plaintextHash").GetString();
                var opaque = value.GetProperty("opaqueObjectId").GetString();
                var version = value.GetProperty("version").GetString();
                var rawKind = value.GetProperty("kind").GetInt32();
                if (string.IsNullOrWhiteSpace(idText) || StringComparer.Ordinal.Equals(idText, IndexObjectId) || Path.IsPathRooted(idText) || idText.Contains('/') || idText.Contains('\\') || idText is "." or "..")
                    throw new InvalidDataException("Repository index contains an invalid logical object ID.");
                if (!Enum.IsDefined(typeof(ObjectKind), rawKind) || (ObjectKind)rawKind is ObjectKind.RepositoryIndex or ObjectKind.Tombstone)
                    throw new InvalidDataException("Repository index contains an invalid object kind.");
                if (hash is null || !HashPattern.IsMatch(hash) || opaque is null || !HashPattern.IsMatch(opaque) || string.IsNullOrWhiteSpace(version))
                    throw new InvalidDataException("Repository index contains invalid object metadata.");
                var entry = new IndexEntry(new LogicalObjectId(idText), (ObjectKind)rawKind, new ContentHash(hash),
                    value.GetProperty("deleted").GetBoolean(), opaque, version);
                if (!ids.Add(entry.Id)) throw new InvalidDataException("Repository index contains duplicate logical object IDs.");
                if (previousId is not null && StringComparer.Ordinal.Compare(previousId, entry.Id.Value) >= 0)
                    throw new InvalidDataException("Repository index entries are not in canonical logical-ID order.");
                previousId = entry.Id.Value;
                result.Add(entry);
            }
            return result;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("Repository index is malformed.", exception); }
    }

    private async Task<StagedImport> StageDownloadAsync(SyncAction action, IReadOnlyList<LocalObject> locals,
        AuthenticatedSnapshot remote, string directory, CancellationToken ct)
    {
        var version = action.Remote!;
        var existing = locals.SingleOrDefault(item => item.Id == action.ObjectId);
        var entry = remote.Entries.Single(item => item.Id == action.ObjectId);
        var plaintext = (await ReadAuthenticatedRemoteObjectAsync(remote, entry, ct).ConfigureAwait(false)).Plaintext;
        ValidateHistoryPayload(version.Kind, plaintext, action.ObjectId, ct);

        LocalObject? relocateFrom = null;
        string path;
        if (version.Kind == ObjectKind.ContinueSession)
        {
            if (_continuePaths is null) throw new InvalidOperationException("Continue paths are not configured.");
            var package = ContinueSessionPackage.Parse(plaintext);
            path = _continuePaths.SessionFilePath(package.SessionId);
            if (existing is not null && existing.Kind != ObjectKind.ContinueSession)
                relocateFrom = existing;
            else if (existing is not null)
                path = existing.SourcePath;
        }
        else if (version.Kind == ObjectKind.SessionAnnotations)
        {
            path = ResolveAnnotationDestination(action.ObjectId, plaintext);
            // A kind change is impossible here: an annotation id can name nothing else.
            if (existing is not null && existing.Kind == ObjectKind.SessionAnnotations) path = existing.SourcePath;
        }
        else if (version.Kind == ObjectKind.ClaudeSession)
        {
            if (_claudePaths is null) throw new InvalidOperationException("Claude paths are not configured.");
            var package = ClaudeSessionPackage.Parse(plaintext);
            path = _claudePaths.SessionFilePath(package.Project, package.SessionId);
            if (existing is not null && existing.Kind != ObjectKind.ClaudeSession)
                relocateFrom = existing;
            else if (existing is not null)
                path = existing.SourcePath;
        }
        else if (version.Kind == ObjectKind.GrokSession)
        {
            if (_grokPaths is null) throw new InvalidOperationException("Grok paths are not configured.");
            var package = GrokSessionPackage.Parse(plaintext);
            path = GrokSessionPackage.ChatHistoryPath(_grokPaths.SessionDirectory(package.Cwd, package.SessionId));
            if (existing is not null && existing.Kind != ObjectKind.GrokSession)
                relocateFrom = existing;
            else if (existing is not null)
                path = existing.SourcePath;
        }
        else
        {
            var root = version.Kind switch
            {
                ObjectKind.ActiveSession => _paths.Sessions,
                ObjectKind.ArchivedSession => _paths.ArchivedSessions,
                _ => throw new InvalidDataException("Only session history can be imported.")
            };
            // Destination always follows the remote kind root. A different local kind is tombstoned first
            // (RelocateFrom) so we never rewrite active bytes into the archived tree or the reverse.
            if (existing is null)
                path = Path.Combine(root, action.ObjectId.Value + ".jsonl");
            else if (existing.Kind == version.Kind)
                path = existing.SourcePath;
            else
            {
                relocateFrom = existing;
                path = Path.Combine(root, action.ObjectId.Value + ".jsonl");
            }
        }

        var stagedDirectory = Path.Combine(directory, "downloads");
        Directory.CreateDirectory(stagedDirectory);
        var stagedPath = Path.Combine(stagedDirectory, Guid.NewGuid().ToString("N") +
            version.Kind switch
            {
                ObjectKind.GrokSession => ".grokpkg",
                ObjectKind.ClaudeSession => ".claudepkg",
                ObjectKind.ContinueSession => ".continuepkg",
                ObjectKind.SessionAnnotations => ".annotation.json",
                _ => ".jsonl"
            });
        await using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(plaintext, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        var staged = await File.ReadAllBytesAsync(stagedPath, ct).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(Sha256(staged).Hex, version.PlaintextHash.Hex))
            throw new InvalidDataException("Staged download does not match its authenticated plaintext hash.");
        var incoming = new LocalObject(action.ObjectId, version.Kind, path, version.PlaintextHash,
            staged.LongLength, DateTimeOffset.UtcNow);
        var expected = relocateFrom is not null
            ? ExpectedHistoryState.Absent
            : action.Local is { IsDeleted: false } local
                ? ExpectedHistoryState.Present(local.PlaintextHash)
                : ExpectedHistoryState.Absent;
        return new StagedImport(incoming, stagedPath, expected, relocateFrom);
    }

    private static void EnsureScanUsable(SessionScanResult scan)
    {
        if (scan.HasFatalErrors)
            throw new InvalidDataException("Local history contains duplicate logical object IDs.");
    }

    private async Task<ImportApplyResult> ApplyStagedImportAsync(StagedImport staged, string operationId, CancellationToken ct)
    {
        await using var content = new FileStream(staged.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await _historyWriter.ImportAsync(staged.Incoming, content, operationId, staged.ExpectedState, ct).ConfigureAwait(false);
    }

    private static void ValidateHistoryPayload(ObjectKind kind, byte[] bytes, LogicalObjectId expectedId,
        CancellationToken ct)
    {
        if (kind == ObjectKind.ContinueSession)
        {
            var continuePackage = ContinueSessionPackage.Parse(bytes);
            if (!string.Equals(ContinueSessionPackage.ToLogicalId(continuePackage.SessionId), expectedId.Value, StringComparison.Ordinal))
                throw new InvalidDataException("Continue session package id does not match its logical object ID.");
            return;
        }

        if (kind == ObjectKind.SessionAnnotations)
        {
            if (!SessionAnnotationPackage.TryReadPackage(bytes, out var annotationKey, out _))
                throw new InvalidDataException("The object is not a readable session annotation.");
            if (!string.Equals(SessionAnnotationPackage.ToLogicalId(annotationKey), expectedId.Value, StringComparison.Ordinal))
                throw new InvalidDataException("Session annotation id does not match its logical object ID.");
            return;
        }

        if (kind == ObjectKind.ClaudeSession)
        {
            var claudePackage = ClaudeSessionPackage.Parse(bytes);
            if (!string.Equals(ClaudeSessionPackage.ToLogicalId(claudePackage.SessionId), expectedId.Value, StringComparison.Ordinal))
                throw new InvalidDataException("Claude session package id does not match its logical object ID.");
            return;
        }

        if (kind == ObjectKind.GrokSession)
        {
            var package = GrokSessionPackage.Parse(bytes);
            if (!string.Equals(GrokSessionPackage.ToLogicalId(package.SessionId), expectedId.Value, StringComparison.Ordinal))
                throw new InvalidDataException("Grok session package id does not match its logical object ID.");
            return;
        }

        ValidateSessionJsonl(bytes, expectedId, ct);
    }

    private static void ValidateSessionJsonl(byte[] bytes, LogicalObjectId expectedId, CancellationToken ct)
    {
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n')
            throw new InvalidDataException("Session JSONL must be complete through its final newline.");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("Session JSONL is not strict UTF-8.", exception); }
        LogicalObjectId? found = null;
        try
        {
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Every JSONL record must be an object.");
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta") continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                    !payload.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Session metadata has no valid ID.");
                var value = id.GetString();
                if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('/') || value.Contains('\\') || value is "." or "..")
                    throw new InvalidDataException("Session ID is unsafe.");
                var parsed = new LogicalObjectId(value);
                if (found is not null && found.Value != parsed)
                    throw new InvalidDataException("Session JSONL contains inconsistent IDs.");
                found = parsed;
            }
        }
        catch (JsonException exception) { throw new InvalidDataException("Session JSONL contains malformed JSON.", exception); }
        if (found is null || found.Value != expectedId)
            throw new InvalidDataException("Session JSONL ID does not match its logical object ID.");
    }

    /// <summary>
    /// The bytes that represent one local object — the plaintext that travels, and the only thing
    /// a scanner hash may be compared against. No kind is the raw file: Codex is the normalized
    /// JSONL, and the other agents are packages assembled from more than the one file on disk.
    /// Every caller that hashes or encrypts a local object goes through here, because a second
    /// way of reading the same object is a second answer to "did it change".
    /// </summary>
    private async Task<byte[]> BuildPlaintextAsync(ObjectKind kind, string sourcePath, CancellationToken ct)
    {
        if (kind == ObjectKind.ContinueSession)
        {
            if (_continuePaths is null) throw new InvalidOperationException("Continue paths are not configured.");
            // The entry belongs to the package, so the shared index is read here rather than at
            // import time; a session the index has lost still stages, with a synthesized entry.
            var index = File.Exists(_continuePaths.IndexFilePath)
                ? await File.ReadAllTextAsync(_continuePaths.IndexFilePath, ct).ConfigureAwait(false)
                : null;
            return ContinueSessionPackage.BuildFromFile(sourcePath, index);
        }

        if (kind == ObjectKind.SessionAnnotations)
            // Already canonical on disk: the bytes the store wrote are the bytes that travel.
            return await File.ReadAllBytesAsync(sourcePath, ct).ConfigureAwait(false);

        if (kind == ObjectKind.ClaudeSession) return ClaudeSessionPackage.BuildFromFile(sourcePath);

        if (kind == ObjectKind.GrokSession)
        {
            var sessionDirectory = Path.GetDirectoryName(sourcePath)
                ?? throw new InvalidDataException("Grok chat_history path has no directory.");
            return GrokSessionPackage.BuildFromDirectory(sessionDirectory);
        }

        var raw = await File.ReadAllBytesAsync(sourcePath, ct).ConfigureAwait(false);
        return SessionJsonlNormalizer.Normalize(raw);
    }

    private async Task<IndexEntry> StageEncryptedObjectAsync(LogicalObjectId id, ObjectKind kind, ContentHash hash,
        bool deleted, string? sourcePath, string directory, CancellationToken ct)
    {
        var plaintext = sourcePath is null
            ? Array.Empty<byte>()
            : await BuildPlaintextAsync(kind, sourcePath, ct).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(Sha256(plaintext).Hex, hash.Hex)) throw new InvalidDataException("Local object changed after stable scanning.");
        await using var input = new MemoryStream(plaintext, false);
        await using var output = new MemoryStream();
        await _crypto.EncryptAsync(input, output, _masterKey, new EnvelopeMetadata(IndexSchemaVersion, id, kind), ct).ConfigureAwait(false);
        var ciphertext = output.ToArray();
        var opaque = Sha256(ciphertext).Hex;
        var path = Path.Combine(directory, opaque + ".chs");
        await File.WriteAllBytesAsync(path, ciphertext, ct).ConfigureAwait(false);
        return new(id, kind, hash, deleted, opaque, _deviceId + ":" + Guid.NewGuid().ToString("N"), path);
    }

    private static bool IsTransientSharingViolation(IOException exception) =>
        OperatingSystem.IsWindows() && (exception.HResult & 0xFFFF) is 32 or 33;

    private void Report(SyncProgressPhase phase, string message) => _progress?.Invoke(new SyncProgress(phase, message));

    private async Task<string> StageIndexAsync(IEnumerable<IndexEntry> entries, string directory, CancellationToken ct)
    {
        await using var json = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", IndexSchemaVersion);
            writer.WriteString("repositoryId", _repositoryId);
            writer.WriteStartArray("objects");
            foreach (var entry in entries.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.Id.Value);
                writer.WriteNumber("kind", (int)entry.Kind);
                writer.WriteString("plaintextHash", entry.PlaintextHash.Hex);
                writer.WriteBoolean("deleted", entry.Deleted);
                writer.WriteString("opaqueObjectId", entry.OpaqueObjectId);
                writer.WriteString("version", entry.Version);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
        json.Position = 0;
        var path = Path.Combine(directory, "repository.chs");
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await _crypto.EncryptAsync(json, destination, _masterKey, IndexMetadata, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
        return path;
    }

    private async Task<PendingConflict> PrepareConflictAsync(SyncAction action, IReadOnlyList<LocalObject> locals,
        AuthenticatedSnapshot remote, CancellationToken ct)
    {
        var local = locals.SingleOrDefault(item => item.Id == action.ObjectId);
        // Built the way the scanner built the hash we are about to check it against, and the way
        // staging builds the bytes that travel. Reading the raw file here instead compared a plain
        // SHA-256 with a package hash: it could never match, so every conflict this touched died
        // as "changed after stable scanning" on a file nobody had changed.
        var localBytes = local is null
            ? Array.Empty<byte>()
            : await BuildPlaintextAsync(local.Kind, local.SourcePath, ct).ConfigureAwait(false);
        if (action.Local is not null && !StringComparer.Ordinal.Equals(Sha256(localBytes).Hex, action.Local.PlaintextHash.Hex))
            throw new InvalidDataException("Local conflict version changed after stable scanning.");
        await using var localPlaintext = new MemoryStream(localBytes, false);
        await using var localEncrypted = new MemoryStream();
        var localKind = action.Local?.Kind ?? action.Baseline?.Kind ?? action.Remote?.Kind
            ?? throw new InvalidDataException("A conflict has no local object kind.");
        var remoteKind = action.Remote?.Kind ?? action.Baseline?.Kind ?? action.Local?.Kind
            ?? throw new InvalidDataException("A conflict has no remote object kind.");
        var localMetadata = new EnvelopeMetadata(IndexSchemaVersion, action.ObjectId, localKind);
        var remoteMetadata = new EnvelopeMetadata(IndexSchemaVersion, action.ObjectId, remoteKind);
        await _crypto.EncryptAsync(localPlaintext, localEncrypted, _masterKey, localMetadata, ct).ConfigureAwait(false);
        localEncrypted.Position = 0;
        var empty = EmptyHash();
        await using var remoteEncrypted = new MemoryStream();
        string remoteDevice;
        var remoteEntry = remote.Entries.SingleOrDefault(entry => entry.Id == action.ObjectId);
        if (remoteEntry is not null)
        {
            var payload = await ReadAuthenticatedRemoteObjectAsync(remote, remoteEntry, ct).ConfigureAwait(false);
            await remoteEncrypted.WriteAsync(payload.Ciphertext, ct).ConfigureAwait(false);
            remoteDevice = DeviceFromVersion(remoteEntry.Version);
        }
        else
        {
            await using var remotePlaintext = new MemoryStream(Array.Empty<byte>(), false);
            await _crypto.EncryptAsync(remotePlaintext, remoteEncrypted, _masterKey, remoteMetadata, ct).ConfigureAwait(false);
            remoteDevice = "remote";
        }
        remoteEncrypted.Position = 0;
        var provenance = new ConflictProvenance(localMetadata, action.Local?.PlaintextHash ?? empty, action.Remote?.PlaintextHash ?? empty,
            action.Baseline?.PlaintextHash ?? empty, _deviceId, remoteDevice, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            action.Local?.IsDeleted ?? true, action.Remote?.IsDeleted ?? true, localMetadata, remoteMetadata);
        return new PendingConflict(provenance, localEncrypted.ToArray(), remoteEncrypted.ToArray());
    }

    private async Task PublishConflictAsync(PendingConflict conflict, CancellationToken ct)
    {
        await using var local = new MemoryStream(conflict.LocalEncrypted, writable: false);
        await using var remote = new MemoryStream(conflict.RemoteEncrypted, writable: false);
        await _conflictStore.PreserveAsync(conflict.Provenance, local, remote, ct).ConfigureAwait(false);
    }

    private static ObjectVersion Version(IndexEntry entry) => new(entry.Id, entry.Kind, entry.PlaintextHash, entry.Version, entry.Deleted);
    private static ContentHash Sha256(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    private static ContentHash EmptyHash() => Sha256([]);
    private static string DeviceFromVersion(string version)
    {
        var separator = version.IndexOf(':');
        var value = separator > 0 ? version[..separator] : "remote";
        return string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "remote" : value;
    }

    private sealed record IndexEntry(LogicalObjectId Id, ObjectKind Kind, ContentHash PlaintextHash, bool Deleted,
        string OpaqueObjectId, string Version, string? StagedPath = null);
    private sealed class AuthenticatedSnapshot(
        bool hasAuthenticatedIndex,
        IReadOnlyList<IndexEntry> entries,
        IReadOnlyDictionary<LogicalObjectId, ObjectVersion> versions,
        RemoteSnapshot snapshot)
    {
        public bool HasAuthenticatedIndex { get; } = hasAuthenticatedIndex;
        public IReadOnlyList<IndexEntry> Entries { get; } = entries;
        public IReadOnlyDictionary<LogicalObjectId, ObjectVersion> Versions { get; } = versions;
        public RemoteSnapshot Snapshot { get; } = snapshot;
        public Dictionary<LogicalObjectId, RemotePayload> Payloads { get; } = [];
    }
    private sealed record RemotePayload(byte[] Ciphertext, byte[] Plaintext);
    private sealed record StagedImport(
        LocalObject Incoming,
        string Path,
        ExpectedHistoryState ExpectedState,
        LocalObject? RelocateFrom = null);
    private sealed record PendingConflict(ConflictProvenance Provenance, byte[] LocalEncrypted, byte[] RemoteEncrypted);
    private sealed class NoopSyncEngineHooks : ISyncEngineHooks
    {
        internal static readonly NoopSyncEngineHooks Instance = new();
        public void OnBeforeLocalPublicationPrecondition() { }
    }
}
