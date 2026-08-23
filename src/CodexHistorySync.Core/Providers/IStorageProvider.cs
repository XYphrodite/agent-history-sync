using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Providers;

public interface IStorageProvider
{
    Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct);

    Task<RemoteSnapshot> ReadSnapshotMetadataAsync(CancellationToken ct) => ReadSnapshotAsync(ct);

    Task<byte[]> ReadObjectAsync(RemoteSnapshot snapshot, LogicalObjectId objectId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = snapshot.Objects.SingleOrDefault(candidate => candidate.ObjectId == objectId)
            ?? throw new InvalidDataException("The requested encrypted object is missing from the remote snapshot.");
        return Task.FromResult(item.Ciphertext.ToArray());
    }

    Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct);
}

public sealed record PublishResult(bool Published, string CurrentRevision);

public sealed record RemoteSnapshot(
    string Revision,
    byte[]? IndexCiphertext,
    IReadOnlyList<EncryptedRemoteObject> Objects,
    IReadOnlyList<LogicalObjectId>? ObjectReferences = null)
{
    public IReadOnlyList<LogicalObjectId> EffectiveObjectReferences =>
        ObjectReferences ?? Objects.Select(item => item.ObjectId).ToArray();
}

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
