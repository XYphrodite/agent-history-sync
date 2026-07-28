using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Providers;

public interface IStorageProvider
{
    Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct);

    Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct);
}

public sealed record PublishResult(bool Published, string CurrentRevision);

public sealed record RemoteSnapshot(
    string Revision,
    IReadOnlyDictionary<LogicalObjectId, ObjectVersion> Objects);

public sealed record PublishRequest(
    string ExpectedRevision,
    IReadOnlyList<EncryptedObjectChange> Changes,
    string CommitMessage);

public sealed record EncryptedObjectChange(
    LogicalObjectId ObjectId,
    string CiphertextPath,
    bool Delete);
