using System.Text.Json;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Codex;

internal sealed record HistoryMutationPlan(LocalObject Target, ExpectedHistoryState Before, ExpectedHistoryState After);

internal sealed class HistoryMutationBatch
{
    internal const string MarkerFileName = "local-mutation.json";
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
        if (File.Exists(markerPath)) throw new IOException("A local mutation marker already exists for this operation.");
        var ids = new HashSet<LogicalObjectId>();
        var entries = new List<MutationEntry>(plans.Count);
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            if (!ids.Add(plan.Target.Id)) throw new InvalidDataException("A local mutation batch contains duplicate object IDs.");
            var captured = await writer.CaptureRollbackAsync(plan, operationId, ct).ConfigureAwait(false);
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
        if (!File.Exists(markerPath)) return false;
        var journal = await ReadAsync(markerPath, ct).ConfigureAwait(false);
        var batch = new HistoryMutationBatch(writer, markerPath, journal);
        batch.ValidateJournal();
        if (batch.IsCommitted(baseline))
        {
            await batch.CommitAsync().ConfigureAwait(false);
            return true;
        }
        await batch.RollbackAsync(ct).ConfigureAwait(false);
        return true;
    }

    internal Task BeginApplyAsync(LogicalObjectId id, CancellationToken ct) => SetStatusAsync(id, MutationStatus.Applying, ct);
    internal Task MarkAppliedAsync(LogicalObjectId id, CancellationToken ct) => SetStatusAsync(id, MutationStatus.Applied, ct);
    internal Task MarkSkippedAsync(LogicalObjectId id, CancellationToken ct) => SetStatusAsync(id, MutationStatus.Skipped, ct);

    internal Task CommitAsync()
    {
        if (File.Exists(_markerPath)) File.Delete(_markerPath);
        return Task.CompletedTask;
    }

    internal async Task RollbackAsync(CancellationToken ct)
    {
        foreach (var entry in _journal.Entries.AsEnumerable().Reverse()
                     .Where(item => item.Status is MutationStatus.Applying or MutationStatus.Applied))
        {
            ct.ThrowIfCancellationRequested();
            await _writer.RollbackAsync(entry.Path, entry.Kind, State(entry.BeforeExists, entry.BeforeHash),
                State(entry.AfterExists, entry.AfterHash), entry.BackupId, _journal.OperationId, ct).ConfigureAwait(false);
        }
        if (File.Exists(_markerPath)) File.Delete(_markerPath);
    }

    private async Task SetStatusAsync(LogicalObjectId id, MutationStatus status, CancellationToken ct)
    {
        var index = _journal.Entries.FindIndex(item => StringComparer.Ordinal.Equals(item.Id, id.Value));
        if (index < 0) throw new InvalidDataException("The local mutation is absent from its durable journal.");
        _journal.Entries[index] = _journal.Entries[index] with { Status = status };
        await WriteDurableAsync(_markerPath, _journal, ct).ConfigureAwait(false);
    }

    private bool IsCommitted(IReadOnlyDictionary<LogicalObjectId, ObjectVersion> baseline)
    {
        var active = _journal.Entries.Where(item => item.Status is MutationStatus.Applying or MutationStatus.Applied).ToArray();
        if (active.Length == 0) return true;
        return active.All(entry => baseline.TryGetValue(new LogicalObjectId(entry.Id), out var version) &&
            (entry.AfterExists
                ? !version.IsDeleted && StringComparer.OrdinalIgnoreCase.Equals(version.PlaintextHash.Hex, entry.AfterHash)
                : version.IsDeleted));
    }

    private void ValidateJournal()
    {
        PathSafety.ValidateFileComponent(_journal.OperationId, nameof(_journal.OperationId));
        if (_journal.Entries.Count == 0) throw new InvalidDataException("The local mutation journal contains no entries.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _journal.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || Path.IsPathRooted(entry.Id) || entry.Id.Contains('/') ||
                entry.Id.Contains('\\') || entry.Id is "." or ".." || !ids.Add(entry.Id))
                throw new InvalidDataException("The local mutation journal contains an invalid or duplicate object ID.");
            if (entry.Kind is not (ObjectKind.ActiveSession or ObjectKind.ArchivedSession) || !Enum.IsDefined(entry.Status))
                throw new InvalidDataException("The local mutation journal contains invalid object metadata.");
            ValidateState(entry.BeforeExists, entry.BeforeHash);
            ValidateState(entry.AfterExists, entry.AfterHash);
            if (entry.BeforeExists != (entry.BackupId is not null))
                throw new InvalidDataException("The local mutation journal has inconsistent backup metadata.");
            if (entry.BackupId is not null) PathSafety.ValidateFileComponent(entry.BackupId, nameof(entry.BackupId));
            _writer.ValidateJournalTarget(entry.Path, entry.Kind);
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
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record MutationJournal(int SchemaVersion, string OperationId, List<MutationEntry> Entries);
    private sealed record MutationEntry(string Id, ObjectKind Kind, string Path, bool BeforeExists, string? BeforeHash,
        bool AfterExists, string? AfterHash, string? BackupId, MutationStatus Status);
    private enum MutationStatus { Pending, Applying, Applied, Skipped }
}
