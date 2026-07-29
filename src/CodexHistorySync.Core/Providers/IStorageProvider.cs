using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Providers;

public interface IStorageProvider
{
    Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct);

    Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct);
}

public sealed record PublishResult(bool Published, string CurrentRevision);

public sealed record RemoteSnapshot(
    string Revision,
    byte[]? IndexCiphertext,
    IReadOnlyList<EncryptedRemoteObject> Objects);

public sealed record EncryptedRemoteObject(
    LogicalObjectId ObjectId,
    byte[] Ciphertext);

public sealed record PublishRequest(
    string ExpectedRevision,
    EncryptedIndexChange? Index,
    IReadOnlyList<EncryptedObjectChange> Changes,
    string CommitMessage);

public sealed record EncryptedIndexChange(
    string CiphertextPath,
    bool Delete);

public sealed record EncryptedObjectChange(
    LogicalObjectId ObjectId,
    string CiphertextPath,
    bool Delete);
