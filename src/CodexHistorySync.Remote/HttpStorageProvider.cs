using System.Security.Cryptography;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;

namespace CodexHistorySync.Remote;

public sealed class HttpStorageProvider : IStorageProvider, IDisposable
{
    private readonly StoreClient client;
    private readonly byte[] transportKey = new byte[RepositoryCrypto.MasterKeySize];
    private readonly RepositoryCrypto crypto = new();
    private readonly Dictionary<string, Dictionary<string, string>> snapshots = new(StringComparer.Ordinal);
    private bool protocolChecked;

    public HttpStorageProvider(StoreClient client, ReadOnlyMemory<byte> repositoryKey)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (repositoryKey.Length != RepositoryCrypto.MasterKeySize) throw new ArgumentException("A repository key is required.", nameof(repositoryKey));
        this.client = client;
        HKDF.DeriveKey(HashAlgorithmName.SHA256, repositoryKey.Span, transportKey, ReadOnlySpan<byte>.Empty,
            "agent-sync/http-blob-envelope/v1"u8);
    }

    // The legacy CHS1 header contains a native logical ID (including Claude memory paths).
    // Seal the entire legacy envelope under a domain-separated key. The outer header only
    // carries the opaque reference and a fixed generic kind, never an agent/session identifier.
    private static EnvelopeMetadata TransportMetadata(LogicalObjectId id) => new(1, id, ObjectKind.Attachment);

    public async Task<RemoteSnapshot> ReadSnapshotMetadataAsync(CancellationToken ct)
    {
        if (!protocolChecked)
        {
            await client.CheckInfoAsync(ct).ConfigureAwait(false);
            protocolChecked = true;
        }
        var snapshot = await client.ReadSnapshotAsync(ct).ConfigureAwait(false);
        snapshots[snapshot.Revision] = snapshot.Objects.ToDictionary(x => x.ObjectId, x => x.Hash, StringComparer.Ordinal);
        return new RemoteSnapshot(snapshot.Revision, snapshot.Index, [], snapshot.Objects.Select(x => new LogicalObjectId(x.ObjectId)).ToArray());
    }

    public async Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await ReadSnapshotMetadataAsync(ct).ConfigureAwait(false);
        var objects = new List<EncryptedRemoteObject>();
        foreach (var id in snapshot.EffectiveObjectReferences)
            objects.Add(new EncryptedRemoteObject(id, await ReadObjectAsync(snapshot, id, ct).ConfigureAwait(false)));
        return snapshot with { Objects = objects };
    }

    public async Task<byte[]> ReadObjectAsync(RemoteSnapshot snapshot, LogicalObjectId objectId, CancellationToken ct)
    {
        if (!snapshots.TryGetValue(snapshot.Revision, out var references) || !references.TryGetValue(objectId.Value, out var hash))
            throw new InvalidDataException("The object is not part of the pinned snapshot.");
        using var encrypted = new MemoryStream(await client.ReadBlobAsync(hash, ct).ConfigureAwait(false), writable: false);
        using var inner = new MemoryStream();
        await crypto.DecryptAsync(encrypted, inner, transportKey, TransportMetadata(objectId), ct).ConfigureAwait(false);
        var bytes = inner.ToArray();
        StoreProtocol.RequireEnvelope(bytes, StoreProtocol.MaximumBlobBytes);
        return bytes;
    }

    public async Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StoreProtocol.IsRevision(request.ExpectedRevision) || request.Changes is null || request.Changes.Count > StoreProtocol.MaximumObjects)
            throw new InvalidDataException("Invalid publication.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in request.Changes)
            if (change is null || !StoreProtocol.IsHash(change.ObjectId.Value) || !ids.Add(change.ObjectId.Value))
                throw new InvalidDataException("Invalid publication references.");
        if (request.Index?.Delete == true) throw new InvalidDataException("The repository index cannot be removed.");
        byte[]? index = null;
        if (request.Index is not null)
        {
            await using var stream = File.OpenRead(request.Index.CiphertextPath);
            index = await StoreProtocol.ReadBoundedAsync(stream, StoreProtocol.MaximumIndexBytes, ct).ConfigureAwait(false);
            StoreProtocol.RequireEnvelope(index, StoreProtocol.MaximumIndexBytes);
        }
        var changes = new List<StoreChange>();
        foreach (var change in request.Changes)
            changes.Add(new StoreChange(change.ObjectId.Value, change.Delete ? null :
                await UploadSealedAsync(change, ct).ConfigureAwait(false)));
        var result = await client.PublishAsync(new StorePublication(request.ExpectedRevision, index, changes), ct).ConfigureAwait(false);
        return new PublishResult(result.Published, result.CurrentRevision);
    }

    private async Task<string> UploadSealedAsync(EncryptedObjectChange change, CancellationToken ct)
    {
        await using var input = File.OpenRead(change.CiphertextPath);
        var bytes = await StoreProtocol.ReadBoundedAsync(input, StoreProtocol.MaximumBlobBytes, ct).ConfigureAwait(false);
        StoreProtocol.RequireEnvelope(bytes, StoreProtocol.MaximumBlobBytes);
        using var inner = new MemoryStream(bytes, writable: false);
        using var encrypted = new MemoryStream();
        await crypto.EncryptAsync(inner, encrypted, transportKey, TransportMetadata(change.ObjectId), ct).ConfigureAwait(false);
        return await client.UploadBlobAsync(encrypted.ToArray(), ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(transportKey);
        client.Dispose();
    }
}
