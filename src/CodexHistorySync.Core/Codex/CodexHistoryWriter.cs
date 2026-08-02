using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.IO;
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
    private readonly BackupStore _backups;
    private readonly ICodexProcessDetector _processDetector;
    private readonly IAtomicFileSystem _fileSystem;

    internal readonly record struct RollbackCapture(string Path, string? BackupId);

    public CodexHistoryWriter(CodexPaths paths, BackupStore backups, ICodexProcessDetector processDetector, IAtomicFileSystem? fileSystem = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _processDetector = processDetector ?? throw new ArgumentNullException(nameof(processDetector));
        _fileSystem = fileSystem ?? new AtomicFileSystem();
    }

    public async Task ImportAsync(LocalObject incoming, Stream plaintext, string operationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var destination = PathSafety.EnsureSessionDestination(incoming.SourcePath, incoming.Kind, _paths, nameof(incoming));
        var expected = File.Exists(destination)
            ? ExpectedHistoryState.Present(await BackupStore.HashFileAsync(destination, ct).ConfigureAwait(false))
            : ExpectedHistoryState.Absent;
        if (await ImportAsync(incoming, plaintext, operationId, expected, ct).ConfigureAwait(false) == ImportApplyResult.Conflict)
            throw new IOException("The destination changed before the staged import could be published.");
    }

    public async Task<ImportApplyResult> ImportAsync(LocalObject incoming, Stream plaintext, string operationId,
        ExpectedHistoryState expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (expected.Exists != (expected.ContentHash is not null)) throw new ArgumentException("Expected history state is inconsistent.", nameof(expected));
        var destination = PathSafety.EnsureSessionDestination(incoming.SourcePath, incoming.Kind, _paths, nameof(incoming));
        PathSafety.RejectReparsePoints(destination, nameof(incoming));
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        await WaitIfRunningAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = BackupStore.SiblingTemporaryPath(destination);
        try
        {
            await _fileSystem.WriteTemporaryAsync(temporary, plaintext, ct).ConfigureAwait(false);
            await ValidateJsonlAsync(temporary, incoming.Id, ct).ConfigureAwait(false);
            var stagedHash = await BackupStore.HashFileAsync(temporary, ct).ConfigureAwait(false);
            if (!BackupStore.HashEquals(stagedHash, incoming.Hash)) throw new InvalidDataException("Incoming plaintext hash does not match the authenticated object hash.");
            if (!await MatchesExpectedStateAsync(destination, expected, ct).ConfigureAwait(false)) return ImportApplyResult.Conflict;
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
        var destination = PathSafety.EnsureSessionDestination(local.SourcePath, local.Kind, _paths, nameof(local));
        PathSafety.RejectReparsePoints(destination, nameof(local));
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        if (!File.Exists(destination)) return TombstoneApplyResult.Applied;
        await WaitIfRunningAsync(ct).ConfigureAwait(false);
        if (!BackupStore.HashEquals(await BackupStore.HashFileAsync(destination, ct).ConfigureAwait(false), baselineHash)) return TombstoneApplyResult.Conflict;
        var backup = await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
        if (!BackupStore.HashEquals(backup.ContentHash, baselineHash)) return TombstoneApplyResult.Conflict;
        return await _fileSystem.DeleteIfUnchangedAsync(destination, baselineHash, EnsureCodexInactive, ct).ConfigureAwait(false)
            ? TombstoneApplyResult.Applied
            : TombstoneApplyResult.Conflict;
    }

    private async Task WaitIfRunningAsync(CancellationToken ct)
    {
        if (_processDetector.IsRunning()) await _processDetector.WaitForExitAsync(ct).ConfigureAwait(false);
    }

    internal async Task<RollbackCapture> CaptureRollbackAsync(HistoryMutationPlan plan, string operationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        var destination = PathSafety.EnsureSessionDestination(plan.Target.SourcePath, plan.Target.Kind, _paths, nameof(plan));
        PathSafety.RejectReparsePoints(destination, nameof(plan));
        await WaitIfRunningAsync(ct).ConfigureAwait(false);
        if (!await MatchesExpectedStateAsync(destination, plan.Before, ct).ConfigureAwait(false))
            throw new IOException("Local history changed before the mutation batch could be captured.");
        if (!plan.Before.Exists) return new RollbackCapture(destination, null);
        var backup = await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false);
        if (!BackupStore.HashEquals(backup.ContentHash, plan.Before.ContentHash!.Value) ||
            !await MatchesExpectedStateAsync(destination, plan.Before, ct).ConfigureAwait(false))
            throw new IOException("Local history changed while the mutation batch was being captured.");
        return new RollbackCapture(destination, backup.Id);
    }

    internal void ValidateJournalTarget(string path, ObjectKind kind)
    {
        var destination = PathSafety.EnsureSessionDestination(path, kind, _paths, nameof(path));
        PathSafety.RejectReparsePoints(destination, nameof(path));
    }

    internal async Task RollbackAsync(string path, ObjectKind kind, ExpectedHistoryState before, ExpectedHistoryState after,
        string? backupId, string operationId, CancellationToken ct)
    {
        var destination = PathSafety.EnsureSessionDestination(path, kind, _paths, nameof(path));
        PathSafety.ValidateFileComponent(operationId, nameof(operationId));
        PathSafety.RejectReparsePoints(destination, nameof(path));
        await WaitIfRunningAsync(ct).ConfigureAwait(false);
        if (await MatchesExpectedStateAsync(destination, before, ct).ConfigureAwait(false)) return;
        if (!await MatchesExpectedStateAsync(destination, after, ct).ConfigureAwait(false))
            throw new IOException("Local history changed after an interrupted synchronized mutation; automatic rollback was refused.");
        if (!before.Exists)
        {
            if (after.Exists && !await _fileSystem.DeleteIfUnchangedAsync(destination, after.ContentHash!.Value,
                    EnsureCodexInactive, ct).ConfigureAwait(false))
                throw new IOException("The synchronized file changed before rollback deletion.");
            return;
        }
        if (backupId is null) throw new InvalidDataException("A rollback journal has no backup for prior history.");
        var backup = await _backups.LoadAsync(backupId, ct).ConfigureAwait(false);
        if (!StringComparer.OrdinalIgnoreCase.Equals(backup.OriginalPath, destination) ||
            !StringComparer.Ordinal.Equals(backup.OperationId, operationId) ||
            !BackupStore.HashEquals(backup.ContentHash, before.ContentHash!.Value) ||
            !BackupStore.HashEquals(await BackupStore.HashFileAsync(backup.ContentPath, ct).ConfigureAwait(false), backup.ContentHash))
            throw new InvalidDataException("The rollback backup does not match its durable journal.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = BackupStore.SiblingTemporaryPath(destination);
        try
        {
            await using var content = new FileStream(backup.ContentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await _fileSystem.WriteTemporaryAsync(temporary, content, ct).ConfigureAwait(false);
            await _fileSystem.PublishAsync(temporary, destination, before.ContentHash.Value,
                after.Exists ? after.ContentHash : null, EnsureCodexInactive, ct).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private bool EnsureCodexInactive()
    {
        if (_processDetector.IsRunning()) throw new CodexBecameActiveException();
        return true;
    }

    private static async Task<bool> MatchesExpectedStateAsync(string destination, ExpectedHistoryState expected, CancellationToken ct)
    {
        if (!expected.Exists) return !File.Exists(destination);
        return File.Exists(destination) && BackupStore.HashEquals(
            await BackupStore.HashFileAsync(destination, ct).ConfigureAwait(false), expected.ContentHash!.Value);
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
}
