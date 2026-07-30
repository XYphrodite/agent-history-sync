using System.Text.Json;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Codex;

internal sealed record HistoryMutationPlan(LocalObject Target, ExpectedHistoryState Before, ExpectedHistoryState After);

internal sealed class HistoryMutationBatch
{
    internal const string MarkerFileName = "local-mutation.json";
    internal const string CleanupEvidenceExtension = ".cleanup";
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CodexHistoryWriter _writer;
    private readonly string _markerPath;
    private MutationJournal _journal;

    private HistoryMutationBatch(CodexHistoryWriter writer, string markerPath, MutationJournal journal)
    {
        _writer = writer;
        _markerPath = markerPath;
        _journal = journal;
    }

    internal static async Task<HistoryMutationBatch> PrepareAsync(CodexHistoryWriter writer, string operationDirectory,
        string operationId, IReadOnlyList<HistoryMutationPlan> plans, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(plans);
        if (string.IsNullOrWhiteSpace(operationDirectory)) throw new ArgumentException("An operation directory is required.", nameof(operationDirectory));
        Directory.CreateDirectory(operationDirectory);
        var markerPath = Path.Combine(Path.GetFullPath(operationDirectory), MarkerFileName);
        if (!IsPathConfirmedAbsent(markerPath)) throw new IOException("A local mutation marker already exists for this operation.");
        await EnsureCleanupEvidenceAsync(operationDirectory, ct).ConfigureAwait(false);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<MutationEntry>(plans.Count);
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            var captured = await writer.CaptureRollbackAsync(plan, operationId, ct).ConfigureAwait(false);
            if (!targets.Add($"{plan.Target.Id.Value}\0{(int)plan.Target.Kind}\0{captured.Path}"))
                throw new InvalidDataException("A local mutation batch contains a duplicate target.");
            entries.Add(new MutationEntry(plan.Target.Id.Value, plan.Target.Kind, captured.Path,
                plan.Before.Exists, plan.Before.ContentHash?.Hex, plan.After.Exists, plan.After.ContentHash?.Hex,
                captured.BackupId, MutationStatus.Pending));
        }
        var journal = new MutationJournal(SchemaVersion, operationId, entries);
        await WriteDurableAsync(markerPath, journal, ct).ConfigureAwait(false);
        return new HistoryMutationBatch(writer, markerPath, journal);
    }

    internal static async Task<bool> RecoverAsync(CodexHistoryWriter writer, string operationDirectory,
        IReadOnlyDictionary<LogicalObjectId, ObjectVersion> baseline, CancellationToken ct)
    {
        var markerPath = Path.Combine(Path.GetFullPath(operationDirectory), MarkerFileName);
        MutationJournal journal;
        try { journal = await ReadAsync(markerPath, ct).ConfigureAwait(false); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        var batch = new HistoryMutationBatch(writer, markerPath, journal);
        batch.ValidateJournal();
        if (batch.IsCommitted(baseline))
            return true;
        await batch.RollbackAsync(ct).ConfigureAwait(false);
        return true;
    }

    internal static string CleanupEvidencePath(string operationDirectory)
    {
        var directory = Path.GetFullPath(operationDirectory);
        return Path.Combine(Path.GetDirectoryName(directory)!, "." + Path.GetFileName(directory) + CleanupEvidenceExtension);
    }

    internal static async Task EnsureCleanupEvidenceAsync(string operationDirectory, CancellationToken ct)
    {
        var path = CleanupEvidencePath(operationDirectory);
        try
        {
            await WriteCleanupEvidenceAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException creationFailure)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("The cleanup evidence path is not a regular file.");
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != 0) throw new IOException("The cleanup evidence file is invalid.");
            }
            catch (FileNotFoundException) { throw creationFailure; }
            catch (DirectoryNotFoundException) { throw creationFailure; }
        }
    }

    internal Task BeginApplyAsync(LogicalObjectId id, CancellationToken ct) => SetStatusAsync(id, MutationStatus.Applying, ct);
    internal Task MarkAppliedAsync(LogicalObjectId id, CancellationToken ct) => SetStatusAsync(id, MutationStatus.Applied, ct);
    internal Task MarkSkippedAsync(LogicalObjectId id, CancellationToken ct) => SetStatusAsync(id, MutationStatus.Skipped, ct);
    internal Task BeginApplyAsync(LocalObject target, CancellationToken ct) =>
        SetStatusAsync(target, MutationStatus.Applying, ct);
    internal Task MarkAppliedAsync(LocalObject target, CancellationToken ct) =>
        SetStatusAsync(target, MutationStatus.Applied, ct);

    internal async Task RollbackAsync(CancellationToken ct)
    {
        foreach (var entry in _journal.Entries.AsEnumerable().Reverse()
                     .Where(item => item.Status is MutationStatus.Applying or MutationStatus.Applied))
        {
            ct.ThrowIfCancellationRequested();
            await _writer.RollbackAsync(entry.Path, entry.Kind, State(entry.BeforeExists, entry.BeforeHash),
                State(entry.AfterExists, entry.AfterHash), entry.BackupId, _journal.OperationId, ct).ConfigureAwait(false);
        }
        File.Delete(_markerPath);
    }

    private async Task SetStatusAsync(LogicalObjectId id, MutationStatus status, CancellationToken ct)
    {
        var matches = _journal.Entries.Select((item, index) => (item, index))
            .Where(pair => StringComparer.Ordinal.Equals(pair.item.Id, id.Value)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The local mutation is absent or ambiguous in its durable journal.");
        var index = matches[0].index;
        _journal.Entries[index] = _journal.Entries[index] with { Status = status };
        await WriteDurableAsync(_markerPath, _journal, ct).ConfigureAwait(false);
    }

    private async Task SetStatusAsync(LocalObject target, MutationStatus status, CancellationToken ct)
    {
        var path = Path.GetFullPath(target.SourcePath);
        var index = _journal.Entries.FindIndex(item => StringComparer.Ordinal.Equals(item.Id, target.Id.Value) &&
            item.Kind == target.Kind && StringComparer.OrdinalIgnoreCase.Equals(item.Path, path));
        if (index < 0) throw new InvalidDataException("The local mutation target is absent from its durable journal.");
        _journal.Entries[index] = _journal.Entries[index] with { Status = status };
        await WriteDurableAsync(_markerPath, _journal, ct).ConfigureAwait(false);
    }

    private bool IsCommitted(IReadOnlyDictionary<LogicalObjectId, ObjectVersion> baseline)
    {
        var active = _journal.Entries.Where(item => item.Status is MutationStatus.Applying or MutationStatus.Applied).ToArray();
        if (active.Length == 0) return true;
        return active.All(entry => baseline.TryGetValue(new LogicalObjectId(entry.Id), out var version) &&
            (entry.AfterExists
                ? !version.IsDeleted && version.Kind == entry.Kind &&
                  StringComparer.OrdinalIgnoreCase.Equals(version.PlaintextHash.Hex, entry.AfterHash)
                : version.IsDeleted || version.Kind != entry.Kind));
    }

    private void ValidateJournal()
    {
        PathSafety.ValidateFileComponent(_journal.OperationId, nameof(_journal.OperationId));
        if (_journal.Entries.Count == 0) throw new InvalidDataException("The local mutation journal contains no entries.");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _journal.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || Path.IsPathRooted(entry.Id) || entry.Id.Contains('/') ||
                entry.Id.Contains('\\') || entry.Id is "." or "..")
                throw new InvalidDataException("The local mutation journal contains an invalid or duplicate object ID.");
            if (entry.Kind is not (ObjectKind.ActiveSession or ObjectKind.ArchivedSession) || !Enum.IsDefined(entry.Status))
                throw new InvalidDataException("The local mutation journal contains invalid object metadata.");
            ValidateState(entry.BeforeExists, entry.BeforeHash);
            ValidateState(entry.AfterExists, entry.AfterHash);
            if (entry.BeforeExists != (entry.BackupId is not null))
                throw new InvalidDataException("The local mutation journal has inconsistent backup metadata.");
            if (entry.BackupId is not null) PathSafety.ValidateFileComponent(entry.BackupId, nameof(entry.BackupId));
            _writer.ValidateJournalTarget(entry.Path, entry.Kind);
            if (!targets.Add($"{entry.Id}\0{(int)entry.Kind}\0{Path.GetFullPath(entry.Path)}"))
                throw new InvalidDataException("The local mutation journal contains a duplicate target.");
        }
    }

    private static void ValidateState(bool exists, string? hash)
    {
        if (exists != (hash is not null) || hash is not null &&
            (hash.Length != 64 || hash.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            throw new InvalidDataException("The local mutation journal contains an invalid content hash.");
    }

    private static ExpectedHistoryState State(bool exists, string? hash) => exists
        ? ExpectedHistoryState.Present(new ContentHash(hash ?? throw new InvalidDataException("A present journal state has no hash.")))
        : hash is null ? ExpectedHistoryState.Absent : throw new InvalidDataException("An absent journal state has a hash.");

    private static async Task<MutationJournal> ReadAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var journal = await JsonSerializer.DeserializeAsync<MutationJournal>(input, JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The local mutation journal is empty.");
        if (journal.SchemaVersion != SchemaVersion || string.IsNullOrWhiteSpace(journal.OperationId) || journal.Entries is null)
            throw new InvalidDataException("The local mutation journal is invalid.");
        return journal;
    }

    private static async Task WriteDurableAsync(string path, MutationJournal journal, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, journal, JsonOptions, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(true);
            }
            if (!IsPathConfirmedAbsent(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteCleanupEvidenceAsync(string path, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.FlushAsync(ct).ConfigureAwait(false);
        output.Flush(true);
    }

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

    private sealed record MutationJournal(int SchemaVersion, string OperationId, List<MutationEntry> Entries);
    private sealed record MutationEntry(string Id, ObjectKind Kind, string Path, bool BeforeExists, string? BeforeHash,
        bool AfterExists, string? AfterHash, string? BackupId, MutationStatus Status);
    private enum MutationStatus { Pending, Applying, Applied, Skipped }
}
