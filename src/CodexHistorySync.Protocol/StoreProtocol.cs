using System.Security.Cryptography;

namespace CodexHistorySync.Remote;

public sealed record StoreInfo(int ProtocolVersion);
public sealed record StoreSetup(byte[] Manifest, byte[] Index, string Revision);
public sealed record StoreInitialization(byte[] Manifest, byte[] Index);
public sealed record StoreObject(string ObjectId, string Hash);
public sealed record StoreSnapshot(string Revision, byte[] Index, IReadOnlyList<StoreObject> Objects);
public sealed record StoreChange(string ObjectId, string? Hash);
public sealed record StorePublication(string ExpectedRevision, byte[]? Index, IReadOnlyList<StoreChange> Changes);
public sealed record StorePublicationResult(bool Published, string CurrentRevision);

public static class StoreProtocol
{
    public const int Version = 1;
    public const int MaximumBlobBytes = 100 * 1024 * 1024;
    public const int MaximumIndexBytes = 16 * 1024 * 1024;
    public const int MaximumJsonBytes = 64 * 1024 * 1024;
    public const int MaximumObjects = 100_000;
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsRevision(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsName(string? value) => value is { Length: >= 1 and <= 64 } && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static void RequireEnvelope(byte[]? bytes, int maximum)
    {
        // The server cannot authenticate ciphertext: only clients hold encryption keys.
        if (bytes is null || bytes.Length < 32 || bytes.Length > maximum || !bytes.AsSpan(0, 4).SequenceEqual("CHS1"u8))
            throw new InvalidDataException("Invalid encrypted payload.");
    }

    public static void Validate(StorePublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!IsRevision(publication.ExpectedRevision) || publication.Changes is null || publication.Changes.Count > MaximumObjects)
            throw new InvalidDataException("Invalid publication.");
        if (publication.Index is not null) RequireEnvelope(publication.Index, MaximumIndexBytes);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in publication.Changes)
            if (item is null || !IsHash(item.ObjectId) || (item.Hash is not null && !IsHash(item.Hash)) || !identities.Add(item.ObjectId))
                throw new InvalidDataException("Invalid object reference.");
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw new StorePayloadTooLargeException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}

public sealed class StorePayloadTooLargeException() : IOException("Payload exceeds the storage limit.");
