using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Codex;

public enum TombstoneApplyResult { Applied, Conflict }

public sealed class CodexHistoryWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly CodexPaths _paths;
    private readonly BackupStore _backups;
    private readonly ICodexProcessDetector _processDetector;
    private readonly IAtomicFileSystem _fileSystem;

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
        ArgumentNullException.ThrowIfNull(plaintext);
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
            var displaced = File.Exists(destination)
                ? await _backups.CreateAsync(destination, operationId, ct).ConfigureAwait(false)
                : null;
            if (displaced is null)
            {
                if (_processDetector.IsRunning()) throw new InvalidOperationException("Codex became active before history replacement; import was deferred.");
                await _fileSystem.ReplaceAsync(temporary, destination, ct).ConfigureAwait(false);
            }
            else if (!await _fileSystem.ReplaceIfUnchangedAsync(temporary, destination, displaced.ContentHash, () => !_processDetector.IsRunning(), ct).ConfigureAwait(false))
                throw new IOException("The destination changed after it was backed up; import was not applied.");
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
        return await _fileSystem.DeleteIfUnchangedAsync(destination, baselineHash, () => !_processDetector.IsRunning(), ct).ConfigureAwait(false)
            ? TombstoneApplyResult.Applied
            : TombstoneApplyResult.Conflict;
    }

    private async Task WaitIfRunningAsync(CancellationToken ct)
    {
        if (_processDetector.IsRunning()) await _processDetector.WaitForExitAsync(ct).ConfigureAwait(false);
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
