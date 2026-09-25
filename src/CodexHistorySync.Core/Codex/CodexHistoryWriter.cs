using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Hermes;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Kimi;
using CodexHistorySync.Core.Muse;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Codex;

public enum TombstoneApplyResult { Applied, Conflict }
public enum ImportApplyResult { Applied, Conflict }

public sealed class CodexBecameActiveException : InvalidOperationException
{
    public CodexBecameActiveException() : base("Codex became active before a local history mutation.") { }
}

public readonly record struct ExpectedHistoryState(bool Exists, ContentHash? ContentHash)
{
    public static ExpectedHistoryState Absent => new(false, null);
    public static ExpectedHistoryState Present(ContentHash hash) => new(true, hash);
}

public sealed class CodexHistoryWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly CodexPaths _paths;
    private readonly GrokPaths? _grokPaths;
    private readonly ClaudePaths? _claudePaths;
    private readonly ContinuePaths? _continuePaths;
    private readonly KimiPaths? _kimiPaths;
    private readonly MusePaths? _musePaths;
    private readonly HermesPaths? _hermesPaths;
    private readonly string? _annotationsDirectory;
    private readonly BackupStore _backups;
    private readonly ICodexProcessDetector _processDetector;
    private readonly IAtomicFileSystem _fileSystem;

    internal readonly record struct RollbackCapture(string Path, string? BackupId);

    public CodexHistoryWriter(CodexPaths paths, BackupStore backups, ICodexProcessDetector processDetector,
        IAtomicFileSystem? fileSystem = null, GrokPaths? grokPaths = null, ClaudePaths? claudePaths = null,
        ContinuePaths? continuePaths = null, string? annotationsDirectory = null, KimiPaths? kimiPaths = null, MusePaths? musePaths = null,
        HermesPaths? hermesPaths = null)
    {
        _annotationsDirectory = annotationsDirectory;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _processDetector = processDetector ?? throw new ArgumentNullException(nameof(processDetector));
        _fileSystem = fileSystem ?? new AtomicFileSystem();
        _grokPaths = grokPaths;
        _claudePaths = claudePaths;
        _continuePaths = continuePaths;
        _kimiPaths = kimiPaths;
        _musePaths = musePaths;
        _hermesPaths = hermesPaths;
    }

    public async Task ImportAsync(LocalObject incoming, Stream plaintext, string operationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var destination = PathSafety.EnsureSessionDestination(incoming.SourcePath, incoming.Kind, _paths, nameof(incoming), _grokPaths, _claudePaths, _continuePaths, _annotationsDirectory, _kimiPaths, _musePaths, _hermesPaths);
        var expected = await CurrentStateAsync(destination, incoming.Kind, ct).ConfigureAwait(false);
        if (await ImportAsync(incoming, plaintext, operationId, expected, ct).ConfigureAwait(false) == ImportApplyResult.Conflict)
            throw new IOException("The destination changed before the staged import could be published.");
    }

    public async Task<ImportApplyResult> ImportAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (expected.Exists != (expected.ContentHash is not null)) throw new ArgumentException("Expected history state is inconsistent.", nameof(expected));
        var destination = PathSafety.EnsureSessionDestination(incoming.SourcePath, incoming.Kind, _paths, nameof(incoming), _grokPaths, _claudePaths, _continuePaths, _annotationsDirectory, _kimiPaths, _musePaths, _hermesPaths);
        PathSafety.RejectReparsePoints(destination, nameof(incoming));
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        ct.ThrowIfCancellationRequested();
        EnsureAgentInactive(incoming.Kind);

        if (incoming.Kind == ObjectKind.GrokSession)
            return await ImportGrokPackageAsync(incoming, plaintext, operationId, expected, destination, ct)
                .ConfigureAwait(false);
        if (incoming.Kind == ObjectKind.ClaudeSession)
            return await ImportClaudePackageAsync(incoming, plaintext, operationId, expected, destination, ct)
                .ConfigureAwait(false);
        if (incoming.Kind == ObjectKind.ClaudeMemory)
            return await ImportClaudeMemoryPackageAsync(incoming, plaintext, operationId, expected, destination, ct)
                .ConfigureAwait(false);
        if (incoming.Kind == ObjectKind.ContinueSession)
            return await ImportContinuePackageAsync(incoming, plaintext, operationId, expected, destination, ct)
                .ConfigureAwait(false);
        if (incoming.Kind == ObjectKind.MuseSession)
            return await ImportMusePackageAsync(incoming, plaintext, operationId, expected, destination, ct);
        if (incoming.Kind == ObjectKind.KimiSession)
            return await ImportKimiPackageAsync(incoming, plaintext, operationId, expected, destination, ct)
                .ConfigureAwait(false);
        if (incoming.Kind == ObjectKind.HermesSession)
            return await ImportHermesPackageAsync(incoming, plaintext, operationId, expected, destination, ct)
                .ConfigureAwait(false);
        if (incoming.Kind == ObjectKind.SessionAnnotations)
            return await ImportAnnotationAsync(incoming, plaintext, operationId, expected, destination, ct)
                .ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = BackupStore.SiblingTemporaryPath(destination);
        try
        {
            await _fileSystem.WriteTemporaryAsync(temporary, plaintext, ct).ConfigureAwait(false);
            await ValidateJsonlAsync(temporary, incoming.Id, ct).ConfigureAwait(false);
            var stagedHash = await BackupStore.HashFileAsync(temporary, ct).ConfigureAwait(false);
            if (!BackupStore.HashEquals(stagedHash, incoming.Hash)) throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
            if (!await MatchesExpectedStateAsync(destination, incoming.Kind, expected, ct).ConfigureAwait(false)) return ImportApplyResult.Conflict;
            if (expected.Exists)
            {
                var displaced = await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
                if (!BackupStore.HashEquals(displaced.ContentHash, expected.ContentHash!.Value)) return ImportApplyResult.Conflict;
            }
            try
            {
                await _fileSystem.PublishAsync(temporary, destination, incoming.Hash, expected.ContentHash,
                    EnsureCodexInactive, ct).ConfigureAwait(false);
            }
            catch (IOException exception) when (exception is not AtomicMutationException)
            {
                return ImportApplyResult.Conflict;
            }
            return ImportApplyResult.Applied;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<TombstoneApplyResult> ApplyTombstoneAsync(LocalObject local, ContentHash baselineHash, string operationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(local);
        var destination = PathSafety.EnsureSessionDestination(local.SourcePath, local.Kind, _paths, nameof(local), _grokPaths, _claudePaths, _continuePaths, _annotationsDirectory, _kimiPaths, _musePaths, _hermesPaths);
        PathSafety.RejectReparsePoints(destination, nameof(local));
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        if (local.Kind == ObjectKind.HermesSession)
            return await ApplyHermesTombstoneAsync(local, baselineHash, operationId, destination, ct).ConfigureAwait(false);
        if (!File.Exists(destination)) return TombstoneApplyResult.Applied;
        ct.ThrowIfCancellationRequested();
        EnsureAgentInactive(local.Kind);
        var current = await ContentHashAsync(destination, local.Kind, ct).ConfigureAwait(false);
        if (current is null || !BackupStore.HashEquals(current.Value, baselineHash)) return TombstoneApplyResult.Conflict;
        if (local.Kind == ObjectKind.GrokSession)
        {
            // Package hash is not the raw chat_history file hash; best-effort backup of chat text only.
            if (File.Exists(destination))
                await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
            File.Delete(destination);
            var summary = Path.Combine(Path.GetDirectoryName(destination)!, "summary.json");
            if (File.Exists(summary)) File.Delete(summary);
            return TombstoneApplyResult.Applied;
        }

        if (local.Kind == ObjectKind.MuseSession)
        {
            if (_musePaths is null) throw new InvalidOperationException("Muse paths are not configured.");
            if (File.Exists(destination))
                await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
            var museSessionDirectory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(museSessionDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(museSessionDirectory, "*",
                             new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    await _backups.CreateAsync(file, operationId, ct).ConfigureAwait(false);
                }
            }
            if (Directory.Exists(museSessionDirectory))
                Directory.Delete(museSessionDirectory, recursive: true);
            return TombstoneApplyResult.Applied;
        }
        if (local.Kind == ObjectKind.KimiSession)
        {
            // Package hash is not the raw state.json hash. Every synchronizable file is backed up
            // so a tombstone is fully recoverable from the backup store; automatic rollback
            // restores the anchor state.json (journal granularity), as it does for Grok.
            if (_kimiPaths is null) throw new InvalidOperationException("Kimi paths are not configured.");
            if (File.Exists(destination))
                await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
            var sessionDirectory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(sessionDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(sessionDirectory, "*",
                             new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    await _backups.CreateAsync(file, operationId, ct).ConfigureAwait(false);
                }
            }
            var indexPath = _kimiPaths.IndexFilePath;
            if (File.Exists(indexPath))
            {
                await _backups.CreateAsync(indexPath, operationId, ct).ConfigureAwait(false);
                var indexContent = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
                var sessionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDirectory));
                var removed = KimiSessionIndex.Remove(indexContent, sessionName);
                if (!string.Equals(indexContent, removed, StringComparison.Ordinal))
                {
                    var temporary = BackupStore.SiblingTemporaryPath(indexPath);
                    try
                    {
                        await using (var staged = new MemoryStream(
                            System.Text.Encoding.UTF8.GetBytes(removed), writable: false))
                            await _fileSystem.WriteTemporaryAsync(temporary, staged, ct).ConfigureAwait(false);
                        var stagedHash = await BackupStore.HashFileAsync(temporary, ct).ConfigureAwait(false);
                        var currentHash = await BackupStore.HashFileAsync(indexPath, ct).ConfigureAwait(false);
                        await _fileSystem.PublishAsync(temporary, indexPath, stagedHash, currentHash, null, ct)
                            .ConfigureAwait(false);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            if (Directory.Exists(sessionDirectory)) Directory.Delete(sessionDirectory, recursive: true);
            var bucketDirectory = Path.GetDirectoryName(sessionDirectory);
            if (bucketDirectory is not null && Directory.Exists(bucketDirectory) &&
                !Directory.EnumerateFileSystemEntries(bucketDirectory).Any())
            {
                Directory.Delete(bucketDirectory);
            }
            return TombstoneApplyResult.Applied;
        }

        var backup = await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
        if (!BackupStore.HashEquals(backup.ContentHash, baselineHash)) return TombstoneApplyResult.Conflict;
        return await _fileSystem.DeleteIfUnchangedAsync(destination, baselineHash, () => EnsureAgentInactive(local.Kind), ct).ConfigureAwait(false)
            ? TombstoneApplyResult.Applied
            : TombstoneApplyResult.Conflict;
    }

    private async Task<ImportApplyResult> ImportGrokPackageAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, string destination, CancellationToken ct)
    {
        if (_grokPaths is null) throw new InvalidOperationException("Grok paths are not configured.");
        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var packageBytes = buffer.ToArray();
        var stagedHash = GrokSessionPackage.HashPackage(packageBytes);
        if (!BackupStore.HashEquals(stagedHash, incoming.Hash))
            throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
        var package = GrokSessionPackage.Parse(packageBytes);
        if (!string.Equals(GrokSessionPackage.ToLogicalId(package.SessionId), incoming.Id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Grok session package id does not match the logical object id.");

        if (!await MatchesExpectedStateAsync(destination, ObjectKind.GrokSession, expected, ct).ConfigureAwait(false))
            return ImportApplyResult.Conflict;
        if (expected.Exists && File.Exists(destination))
            await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);

        try
        {
            GrokSessionPackage.Materialize(package, _grokPaths);
            var after = await ContentHashAsync(destination, ObjectKind.GrokSession, ct).ConfigureAwait(false);
            if (after is null || !BackupStore.HashEquals(after.Value, incoming.Hash))
                throw new IOException("Grok session materialization did not produce the authenticated package hash.");
            return ImportApplyResult.Applied;
        }
        catch (IOException)
        {
            return ImportApplyResult.Conflict;
        }
    }

    /// <summary>
    /// Imports one annotation. It is the only import that touches no agent home at all: the
    /// destination was already pinned to the annotations directory, and the bytes are written
    /// there as they arrived. Codex is never asked to stand still for it, because nothing an
    /// agent reads is being changed.
    /// </summary>
    private async Task<ImportApplyResult> ImportAnnotationAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, string destination, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (!BackupStore.HashEquals(SessionAnnotationPackage.HashPackage(bytes), incoming.Hash))
            throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");

        if (!SessionAnnotationPackage.TryReadPackage(bytes, out var key, out _))
            throw new InvalidDataException("The incoming object is not a readable session annotation.");
        if (!string.Equals(SessionAnnotationPackage.ToLogicalId(key), incoming.Id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Annotation id does not match the logical object id.");
        // The file name is derived from the annotation, never from the incoming path.
        if (!string.Equals(SessionAnnotationStore.FileName(key), Path.GetFileName(destination),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Annotation destination does not match the annotation it carries.");

        if (!await MatchesExpectedStateAsync(destination, ObjectKind.SessionAnnotations, expected, ct).ConfigureAwait(false))
            return ImportApplyResult.Conflict;
        if (expected.Exists && File.Exists(destination))
            await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = BackupStore.SiblingTemporaryPath(destination);
        try
        {
            await using (var staged = new MemoryStream(bytes, writable: false))
            {
                await _fileSystem.WriteTemporaryAsync(temporary, staged, ct).ConfigureAwait(false);
            }

            await _fileSystem.ReplaceAsync(temporary, destination, ct).ConfigureAwait(false);
            var after = await ContentHashAsync(destination, ObjectKind.SessionAnnotations, ct).ConfigureAwait(false);
            if (after is null || !BackupStore.HashEquals(after.Value, incoming.Hash))
                throw new IOException("The annotation on disk does not match the authenticated object hash.");
            return ImportApplyResult.Applied;
        }
        catch (IOException)
        {
            return ImportApplyResult.Conflict;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<ImportApplyResult> ImportClaudePackageAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, string destination, CancellationToken ct)
    {
        if (_claudePaths is null) throw new InvalidOperationException("Claude paths are not configured.");
        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var packageBytes = buffer.ToArray();
        var stagedHash = ClaudeSessionPackage.HashPackage(packageBytes);
        if (!BackupStore.HashEquals(stagedHash, incoming.Hash))
            throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
        var package = ClaudeSessionPackage.Parse(packageBytes);
        if (!string.Equals(ClaudeSessionPackage.ToLogicalId(package.SessionId), incoming.Id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Claude session package id does not match the logical object id.");

        if (!await MatchesExpectedStateAsync(destination, ObjectKind.ClaudeSession, expected, ct).ConfigureAwait(false))
            return ImportApplyResult.Conflict;
        if (expected.Exists && File.Exists(destination))
            await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);

        try
        {
            ClaudeSessionPackage.Materialize(package, _claudePaths);
            var after = await ContentHashAsync(destination, ObjectKind.ClaudeSession, ct).ConfigureAwait(false);
            if (after is null || !BackupStore.HashEquals(after.Value, incoming.Hash))
                throw new IOException("Claude session materialization did not produce the authenticated package hash.");
            return ImportApplyResult.Applied;
        }
        catch (IOException)
        {
            return ImportApplyResult.Conflict;
        }
    }

    private async Task<ImportApplyResult> ImportClaudeMemoryPackageAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, string destination, CancellationToken ct)
    {
        if (_claudePaths is null) throw new InvalidOperationException("Claude paths are not configured.");
        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var packageBytes = buffer.ToArray();
        var stagedHash = ClaudeMemoryPackage.HashPackage(packageBytes);
        if (!BackupStore.HashEquals(stagedHash, incoming.Hash))
            throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
        var package = ClaudeMemoryPackage.Parse(packageBytes, incoming.Id.Value);
        if (!string.Equals(ClaudeMemoryPackage.ToLogicalId(package.Project, package.Name), incoming.Id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Claude memory package id does not match the logical object id.");
        if (!string.Equals(_claudePaths.MemoryFilePath(package.Project, package.Name), destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Claude memory destination does not match the file it carries.");

        if (!await MatchesExpectedStateAsync(destination, ObjectKind.ClaudeMemory, expected, ct).ConfigureAwait(false))
            return ImportApplyResult.Conflict;
        if (expected.Exists && File.Exists(destination))
            await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);

        try
        {
            ClaudeMemoryPackage.Materialize(package, _claudePaths);
            var after = await ContentHashAsync(destination, ObjectKind.ClaudeMemory, ct).ConfigureAwait(false);
            if (after is null || !BackupStore.HashEquals(after.Value, incoming.Hash))
                throw new IOException("Claude memory materialization did not produce the authenticated package hash.");
            return ImportApplyResult.Applied;
        }
        catch (IOException)
        {
            return ImportApplyResult.Conflict;
        }
    }

    /// <summary>
    /// Imports a Continue session. Unlike the other three agents this touches a file the import
    /// does not own — the shared session index — so the index is backed up alongside the session,
    /// and an index that does not parse stops the import instead of being replaced (design C5).
    /// </summary>
    private async Task<ImportApplyResult> ImportContinuePackageAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, string destination, CancellationToken ct)
    {
        if (_continuePaths is null) throw new InvalidOperationException("Continue paths are not configured.");
        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var packageBytes = buffer.ToArray();
        var stagedHash = ContinueSessionPackage.HashPackage(packageBytes);
        if (!BackupStore.HashEquals(stagedHash, incoming.Hash))
            throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
        var package = ContinueSessionPackage.Parse(packageBytes);
        if (!string.Equals(ContinueSessionPackage.ToLogicalId(package.SessionId), incoming.Id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Continue session package id does not match the logical object id.");

        // Read before writing anything: a malformed index means Continue itself cannot create a
        // session, and replacing it would destroy the list of every session on this machine.
        var indexPath = _continuePaths.IndexFilePath;
        if (File.Exists(indexPath))
            ContinueSessionIndex.Parse(await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));

        if (!await MatchesExpectedStateAsync(destination, ObjectKind.ContinueSession, expected, ct).ConfigureAwait(false))
            return ImportApplyResult.Conflict;
        if (expected.Exists && File.Exists(destination))
            await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
        if (File.Exists(indexPath))
            await _backups.CreateAsync(indexPath, operationId, ct).ConfigureAwait(false);

        try
        {
            ContinueSessionPackage.Materialize(package, _continuePaths);
            var after = await ContentHashAsync(destination, ObjectKind.ContinueSession, ct).ConfigureAwait(false);
            if (after is null || !BackupStore.HashEquals(after.Value, incoming.Hash))
                throw new IOException("Continue session materialization did not produce the authenticated package hash.");
            return ImportApplyResult.Applied;
        }
        catch (IOException)
        {
            return ImportApplyResult.Conflict;
        }
    }

    /// <summary>
    /// Imports a Kimi Code session. Like Continue this touches a file the import does not own —
    /// the shared session index — so the index is backed up alongside the session, and an index
    /// that does not parse stops the import instead of being replaced.
    /// </summary>
    private async Task<ImportApplyResult> ImportKimiPackageAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, string destination, CancellationToken ct)
    {
        if (_kimiPaths is null) throw new InvalidOperationException("Kimi paths are not configured.");
        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var packageBytes = buffer.ToArray();
        var stagedHash = KimiSessionPackage.HashPackage(packageBytes);
        if (!BackupStore.HashEquals(stagedHash, incoming.Hash))
            throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
        var package = KimiSessionPackage.Parse(packageBytes);
        if (!string.Equals(KimiSessionPackage.ToLogicalId(package.SessionId), incoming.Id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Kimi session package id does not match the logical object id.");

        // Read before writing anything: a malformed index must not be replaced, because it lists
        // sessions this import has never heard of.
        var indexPath = _kimiPaths.IndexFilePath;
        if (File.Exists(indexPath))
            KimiSessionIndex.Parse(await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false));

        if (!await MatchesExpectedStateAsync(destination, ObjectKind.KimiSession, expected, ct).ConfigureAwait(false))
            return ImportApplyResult.Conflict;
        if (expected.Exists && File.Exists(destination))
            await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
        if (File.Exists(indexPath))
            await _backups.CreateAsync(indexPath, operationId, ct).ConfigureAwait(false);

        try
        {
            KimiSessionPackage.Materialize(package, _kimiPaths);
            var after = await ContentHashAsync(destination, ObjectKind.KimiSession, ct).ConfigureAwait(false);
            if (after is null || !BackupStore.HashEquals(after.Value, incoming.Hash))
                throw new IOException("Kimi session materialization did not produce the authenticated package hash.");
            return ImportApplyResult.Applied;
        }
        catch (IOException)
        {
            return ImportApplyResult.Conflict;
        }
    }

    // The process detector cannot identify which Codex thread is open. Defer Codex mutations
    // conservatively, but never wait for a long-lived app-server or block other agents on it.
    internal bool IsMutationBlocked(ObjectKind kind) =>
        (kind is ObjectKind.ActiveSession or ObjectKind.ArchivedSession) && _processDetector.IsRunning();

    /// <summary>
    /// Kinds whose journal hash describes an assembled package rather than the anchor file the
    /// backup holds. For those, the backup is validated against itself and the journal state is
    /// re-read from disk through <see cref="ContentHashAsync"/> instead of being compared to the
    /// raw file hash.
    /// </summary>
    private static bool IsPackageBackedKind(ObjectKind kind) =>
        kind is ObjectKind.GrokSession or ObjectKind.ClaudeSession or ObjectKind.ClaudeMemory
            or ObjectKind.ContinueSession or ObjectKind.KimiSession or ObjectKind.MuseSession
            or ObjectKind.HermesSession;

    private bool EnsureAgentInactive(ObjectKind kind)
    {
        if (IsMutationBlocked(kind)) throw new CodexBecameActiveException();
        return true;
    }

    internal async Task<RollbackCapture> CaptureRollbackAsync(HistoryMutationPlan plan, string operationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        var destination = PathSafety.EnsureSessionDestination(plan.Target.SourcePath, plan.Target.Kind, _paths, nameof(plan), _grokPaths, _claudePaths, _continuePaths, _annotationsDirectory, _kimiPaths, _musePaths, _hermesPaths);
        PathSafety.RejectReparsePoints(destination, nameof(plan));
        ct.ThrowIfCancellationRequested();
        EnsureAgentInactive(plan.Target.Kind);
        if (!await MatchesExpectedStateAsync(destination, plan.Target.Kind, plan.Before, ct).ConfigureAwait(false))
            throw new IOException("Local history changed before the mutation batch could be captured.");
        if (plan.Target.Kind == ObjectKind.HermesSession && plan.Before.Exists)
            HermesSessionPackage.WriteAnchorSnapshot(destination);
        if (!plan.Before.Exists) return new RollbackCapture(destination, null);
        var backup = await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
        var backupIntact = BackupStore.HashEquals(
            await BackupStore.HashFileAsync(backup.ContentPath, ct).ConfigureAwait(false), backup.ContentHash);
        var backupMatchesJournal = IsPackageBackedKind(plan.Target.Kind) ||
            BackupStore.HashEquals(backup.ContentHash, plan.Before.ContentHash!.Value);
        if (!backupIntact || !backupMatchesJournal ||
            !await MatchesExpectedStateAsync(destination, plan.Target.Kind, plan.Before, ct).ConfigureAwait(false))
            throw new IOException("Local history changed while the mutation batch was being captured.");
        return new RollbackCapture(destination, backup.Id);
    }

    internal void ValidateJournalTarget(string path, ObjectKind kind)
    {
        var destination = PathSafety.EnsureSessionDestination(path, kind, _paths, nameof(path), _grokPaths, _claudePaths, _continuePaths, _annotationsDirectory, _kimiPaths, _musePaths, _hermesPaths);
        PathSafety.RejectReparsePoints(destination, nameof(path));
    }

    internal async Task RollbackAsync(string path, ObjectKind kind, ExpectedHistoryState before, ExpectedHistoryState after,
        string? backupId, string operationId, CancellationToken ct)
    {
        var destination = PathSafety.EnsureSessionDestination(path, kind, _paths, nameof(path), _grokPaths, _claudePaths, _continuePaths, _annotationsDirectory, _kimiPaths, _musePaths, _hermesPaths);
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        PathSafety.RejectReparsePoints(destination, nameof(path));
        ct.ThrowIfCancellationRequested();
        if (await MatchesExpectedStateAsync(destination, kind, before, ct).ConfigureAwait(false)) return;
        EnsureAgentInactive(kind);
        if (!await MatchesExpectedStateAsync(destination, kind, after, ct).ConfigureAwait(false))
            throw new IOException("Local history changed after an interrupted synchronized mutation; automatic rollback was refused.");
        if (!before.Exists)
        {
            if (kind == ObjectKind.HermesSession)
            {
                HermesSessionPackage.DeleteAnchor(destination);
                if (File.Exists(destination)) File.Delete(destination);
                return;
            }
            if (after.Exists && !await _fileSystem.DeleteIfUnchangedAsync(destination, after.ContentHash!.Value,
                    () => EnsureAgentInactive(kind), ct).ConfigureAwait(false))
                throw new IOException("The synchronized file changed before rollback deletion.");
            return;
        }
        if (backupId is null) throw new InvalidDataException("A rollback journal has no backup for prior history.");
        var backup = await _backups.LoadAsync(backupId, ct).ConfigureAwait(false);
        var packageBacked = IsPackageBackedKind(kind);
        // For package-backed kinds the journal hash describes the assembled package, not the
        // anchor file the backup holds: the backup is validated against itself, and publication
        // below expects the backup's own hash. (Restoring the anchor alone leaves an incomplete
        // session on disk; the next run plans it again from the repository.)
        if (!StringComparer.OrdinalIgnoreCase.Equals(backup.OriginalPath, destination) ||
            !StringComparer.Ordinal.Equals(backup.OperationId, operationId) ||
            (!packageBacked && !BackupStore.HashEquals(backup.ContentHash, before.ContentHash!.Value)) ||
            !BackupStore.HashEquals(await BackupStore.HashFileAsync(backup.ContentPath, ct).ConfigureAwait(false), backup.ContentHash))
            throw new InvalidDataException("The rollback backup does not match its durable journal.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = BackupStore.SiblingTemporaryPath(destination);
        try
        {
            await using var content = new FileStream(backup.ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await _fileSystem.WriteTemporaryAsync(temporary, content, ct).ConfigureAwait(false);
            await _fileSystem.PublishAsync(temporary, destination,
                packageBacked ? backup.ContentHash : before.ContentHash!.Value,
                after.Exists ? after.ContentHash : null, () => EnsureAgentInactive(kind), ct).ConfigureAwait(false);
            if (kind == ObjectKind.HermesSession) HermesSessionPackage.MaterializeAnchor(destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool EnsureCodexInactive()
    {
        if (_processDetector.IsRunning()) throw new CodexBecameActiveException();
        return true;
    }

    private async Task<ExpectedHistoryState> CurrentStateAsync(string destination, ObjectKind kind, CancellationToken ct)
    {
        var hash = await ContentHashAsync(destination, kind, ct).ConfigureAwait(false);
        return hash is null ? ExpectedHistoryState.Absent : ExpectedHistoryState.Present(hash.Value);
    }

    private async Task<bool> MatchesExpectedStateAsync(string destination, ObjectKind kind, ExpectedHistoryState expected,
        CancellationToken ct)
    {
        var current = await ContentHashAsync(destination, kind, ct).ConfigureAwait(false);
        if (!expected.Exists) return current is null;
        return current is not null && BackupStore.HashEquals(current.Value, expected.ContentHash!.Value);
    }

    private static async Task<ContentHash?> ContentHashAsync(string destination, ObjectKind kind, CancellationToken ct)
    {
        if (kind == ObjectKind.HermesSession)
            return HermesSessionPackage.HashAnchor(destination);

        if (kind == ObjectKind.ContinueSession)
        {
            if (!File.Exists(destination)) return null;
            try
            {
                // The hash covers the session together with its index entry, so the index has to
                // be read back here too — it is the sibling of every session file.
                var indexPath = Path.Combine(Path.GetDirectoryName(destination)!, ContinuePaths.IndexFileName);
                var index = File.Exists(indexPath) ? File.ReadAllText(indexPath) : null;
                return ContinueSessionPackage.HashPackage(ContinueSessionPackage.BuildFromFile(destination, index));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                                  or JsonException or DecoderFallbackException or ArgumentException)
            {
                return null;
            }
        }

        if (kind == ObjectKind.SessionAnnotations)
        {
            if (!File.Exists(destination)) return null;
            try
            {
                return SessionAnnotationPackage.HashPackage(await File.ReadAllBytesAsync(destination, ct).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (kind == ObjectKind.ClaudeMemory)
        {
            if (!File.Exists(destination)) return null;
            try
            {
                return ClaudeMemoryPackage.HashPackage(ClaudeMemoryPackage.BuildFromFile(destination));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                                  or JsonException or DecoderFallbackException or ArgumentException)
            {
                return null;
            }
        }

        if (kind == ObjectKind.ClaudeSession)
        {
            if (!File.Exists(destination)) return null;
            try
            {
                return ClaudeSessionPackage.HashPackage(ClaudeSessionPackage.BuildFromFile(destination));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                                  or JsonException or DecoderFallbackException or ArgumentException)
            {
                return null;
            }
        }

        if (kind == ObjectKind.GrokSession)
        {
            if (!File.Exists(destination)) return null;
            var directory = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException("Grok chat_history path has no directory.");
            try
            {
                var package = GrokSessionPackage.BuildFromDirectory(directory);
                return GrokSessionPackage.HashPackage(package);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                                  or JsonException or DecoderFallbackException or ArgumentException)
            {
                return null;
            }
        }

        if (kind == ObjectKind.KimiSession)
        {
            if (!File.Exists(destination)) return null;
            var sessionDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException("Kimi state.json path has no directory.");
            var workDirKey = Path.GetFileName(Path.TrimEndingDirectorySeparator(
                Path.GetDirectoryName(sessionDirectory)
                ?? Path.GetPathRoot(sessionDirectory)
                ?? sessionDirectory));
            try
            {
                var package = KimiSessionPackage.BuildFromDirectory(sessionDirectory, workDirKey);
                return KimiSessionPackage.HashPackage(package);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                                  or JsonException or DecoderFallbackException or ArgumentException)
            {
                return null;
            }
        }

        if (!File.Exists(destination)) return null;
        return await BackupStore.HashFileAsync(destination, ct).ConfigureAwait(false);
    }

    private static async Task ValidateJsonlAsync(string path, LogicalObjectId expectedId, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n') throw new InvalidDataException("Session JSONL must be complete through its final newline.");
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
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Every JSONL record must be an object.");
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta") continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Session metadata has no valid ID.");
                var value = id.GetString();
                if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('/') || value.Contains('\\') || value is "." or "..") throw new InvalidDataException("Session ID is unsafe.");
                var parsed = new LogicalObjectId(value);
                if (found is not null && found.Value != parsed) throw new InvalidDataException("Session JSONL contains inconsistent IDs.");
                found = parsed;
            }
        }
        catch (JsonException exception) { throw new InvalidDataException("Session JSONL contains malformed JSON.", exception); }
        if (found is null || found.Value != expectedId) throw new InvalidDataException("Session JSONL ID does not match its logical object ID.");
    }
    private async Task<ImportApplyResult> ImportMusePackageAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, string destination, CancellationToken ct)
    {
        if (_musePaths is null) throw new InvalidOperationException("Muse paths are not configured.");
        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var packageBytes = buffer.ToArray();
        var stagedHash = MuseSessionPackage.HashPackage(packageBytes);
        if (!BackupStore.HashEquals(stagedHash, incoming.Hash))
            throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
        var package = MuseSessionPackage.Parse(packageBytes);
        if (!string.Equals(MuseSessionPackage.ToLogicalId(package.SessionId), incoming.Id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Muse session package id does not match the logical object id.");

        if (!await MatchesExpectedStateAsync(destination, ObjectKind.MuseSession, expected, ct).ConfigureAwait(false))
            return ImportApplyResult.Conflict;
        if (expected.Exists && File.Exists(destination))
            await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);

        try
        {
            MuseSessionPackage.Materialize(package, _musePaths);
            var after = await ContentHashAsync(destination, ObjectKind.MuseSession, ct).ConfigureAwait(false);
            if (after is null || !BackupStore.HashEquals(after.Value, incoming.Hash))
                throw new IOException("Muse session materialization did not produce the authenticated package hash.");
            return ImportApplyResult.Applied;
        }
        catch (IOException)
        {
            return ImportApplyResult.Conflict;
        }
    }

    private async Task<ImportApplyResult> ImportHermesPackageAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, string destination, CancellationToken ct)
    {
        if (_hermesPaths is null) throw new InvalidOperationException("Hermes paths are not configured.");
        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var packageBytes = buffer.ToArray();
        var stagedHash = HermesSessionPackage.HashPackage(packageBytes);
        if (!BackupStore.HashEquals(stagedHash, incoming.Hash))
            throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
        if (!HermesPaths.TryParseAnchor(destination, out _, out var profile, out var sessionId) ||
            !string.Equals(HermesSessionPackage.ToLogicalId(profile, sessionId), incoming.Id.Value, StringComparison.Ordinal) ||
            !string.Equals(HermesSessionPackage.LogicalIdOf(packageBytes), incoming.Id.Value, StringComparison.Ordinal))
            throw new InvalidDataException("Hermes session package id does not match the logical object id.");

        if (!await MatchesExpectedStateAsync(destination, ObjectKind.HermesSession, expected, ct).ConfigureAwait(false))
            return ImportApplyResult.Conflict;
        if (expected.Exists)
        {
            HermesSessionPackage.WriteAnchorSnapshot(destination);
            await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
        }

        try
        {
            HermesSessionPackage.Materialize(_hermesPaths, packageBytes);
            HermesSessionPackage.WriteAnchorSnapshot(destination);
            var after = await ContentHashAsync(destination, ObjectKind.HermesSession, ct).ConfigureAwait(false);
            if (after is null || !BackupStore.HashEquals(after.Value, incoming.Hash))
                throw new IOException("Hermes session materialization did not produce the authenticated package hash.");
            return ImportApplyResult.Applied;
        }
        catch (IOException)
        {
            return ImportApplyResult.Conflict;
        }
    }

    private async Task<TombstoneApplyResult> ApplyHermesTombstoneAsync(LocalObject local, ContentHash baselineHash,
        string operationId, string destination, CancellationToken ct)
    {
        if (_hermesPaths is null) throw new InvalidOperationException("Hermes paths are not configured.");
        ct.ThrowIfCancellationRequested();
        EnsureAgentInactive(local.Kind);
        var current = await ContentHashAsync(destination, ObjectKind.HermesSession, ct).ConfigureAwait(false);
        if (current is null) return TombstoneApplyResult.Applied;
        if (!BackupStore.HashEquals(current.Value, baselineHash)) return TombstoneApplyResult.Conflict;
        HermesSessionPackage.WriteAnchorSnapshot(destination);
        await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
        HermesSessionPackage.DeleteAnchor(destination);
        if (File.Exists(destination)) File.Delete(destination);
        return TombstoneApplyResult.Applied;
    }

}
