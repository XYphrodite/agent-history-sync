using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Model;
using Konscious.Security.Cryptography;

namespace CodexHistorySync.Core.Crypto;

public sealed class RepositoryCrypto
{
    public const int MasterKeySize = 32;
    public const int RepositorySaltSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int ObjectKeySize = 32;
    public const int DefaultMemoryKiB = 65_536;
    public const int DefaultIterations = 3;
    public const int DefaultParallelism = 2;

    private const byte FormatVersion = 1;
    private const int FixedHeaderSize = 37;
    private const int MaximumObjectIdBytes = 1_024;
    // Long Codex VS Code sessions can exceed 128 MiB; keep a hard ceiling for memory safety.
    private const int MaximumPayloadBytes = 512 * 1024 * 1024;
    private static readonly byte[] Magic = "CHS1"u8.ToArray();
    private static readonly byte[] ObjectKeyLabel = "CodexHistorySync/object-key/v1"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<byte[]> DeriveMasterKeyAsync(
        ReadOnlyMemory<char> passphrase,
        Argon2Parameters parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateArgon2Parameters(parameters);

        cancellationToken.ThrowIfCancellationRequested();
        var passphraseBytes = new byte[StrictUtf8.GetByteCount(passphrase.Span)];
        byte[]? derivedKey = null;

        try
        {
            StrictUtf8.GetBytes(passphrase.Span, passphraseBytes);
            using var argon2 = new Argon2id(passphraseBytes)
            {
                Salt = parameters.Salt.ToArray(),
                MemorySize = parameters.MemoryKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism
            };

            derivedKey = await argon2.GetBytesAsync(MasterKeySize).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = derivedKey;
            derivedKey = null;
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
            if (derivedKey is not null)
            {
                CryptographicOperations.ZeroMemory(derivedKey);
            }
        }
    }

    public async Task EncryptAsync(
        Stream plaintext,
        Stream destination,
        ReadOnlyMemory<byte> masterKey,
        EnvelopeMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateMasterKey(masterKey);
        ValidateMetadata(metadata);

        byte[]? plaintextBytes = null;
        byte[]? objectKey = null;

        try
        {
            plaintextBytes = await ReadBoundedPlaintextAsync(plaintext, cancellationToken).ConfigureAwait(false);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var header = BuildHeader(metadata, plaintextBytes.Length, nonce);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];
            objectKey = DeriveObjectKey(masterKey.Span, metadata);

            using (var aes = new AesGcm(objectKey, TagSize))
            {
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, header);
            }

            await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (plaintextBytes is not null)
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }

            if (objectKey is not null)
            {
                CryptographicOperations.ZeroMemory(objectKey);
            }
        }
    }

    public async Task DecryptAsync(
        Stream ciphertext,
        Stream destination,
        ReadOnlyMemory<byte> masterKey,
        EnvelopeMetadata expectedMetadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateMasterKey(masterKey);
        ValidateMetadata(expectedMetadata);

        var envelope = await ReadEnvelopeAsync(ciphertext, cancellationToken).ConfigureAwait(false);
        byte[]? objectKey = null;
        byte[]? plaintext = null;

        try
        {
            objectKey = DeriveObjectKey(masterKey.Span, envelope.Metadata);
            plaintext = new byte[envelope.Ciphertext.Length];
            using (var aes = new AesGcm(objectKey, TagSize))
            {
                aes.Decrypt(
                    envelope.Nonce,
                    envelope.Ciphertext,
                    envelope.Tag,
                    plaintext,
                    envelope.Header);
            }

            if (envelope.Metadata != expectedMetadata)
            {
                throw new CryptographicException("The authenticated envelope metadata does not match the expected object.");
            }

            await destination.WriteAsync(plaintext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (objectKey is not null)
            {
                CryptographicOperations.ZeroMemory(objectKey);
            }

            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static void ValidateArgon2Parameters(Argon2Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters.Salt);
        if (parameters.Salt.Length != RepositorySaltSize)
        {
            throw new ArgumentException($"The repository salt must be exactly {RepositorySaltSize} bytes.", nameof(parameters));
        }

        if (parameters.MemoryKiB is < 8 or > 262_144)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Argon2 memory must be between 8 and 262144 KiB.");
        }

        if (parameters.Iterations is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Argon2 iterations must be between 1 and 20.");
        }

        if (parameters.Parallelism is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Argon2 parallelism must be between 1 and 64.");
        }

        if (parameters.MemoryKiB < 8 * parameters.Parallelism)
        {
            throw new ArgumentException("Argon2 memory must be at least 8 KiB per parallel lane.", nameof(parameters));
        }
    }

    private static void ValidateMasterKey(ReadOnlyMemory<byte> masterKey)
    {
        if (masterKey.Length != MasterKeySize)
        {
            throw new ArgumentException($"The master key must be exactly {MasterKeySize} bytes.", nameof(masterKey));
        }
    }

    private static void ValidateMetadata(EnvelopeMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.SchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "Schema version must be positive.");
        }

        if (!Enum.IsDefined(metadata.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(metadata), "Object kind is unknown.");
        }

        var objectId = metadata.ObjectId.Value;
        if (string.IsNullOrWhiteSpace(objectId))
        {
            throw new ArgumentException("Object ID must not be empty.", nameof(metadata));
        }

        var byteCount = StrictUtf8.GetByteCount(objectId);
        if (byteCount > MaximumObjectIdBytes)
        {
            throw new ArgumentException("Object ID is too long.", nameof(metadata));
        }
    }

    private static byte[] BuildHeader(EnvelopeMetadata metadata, int ciphertextLength, byte[] nonce)
    {
        var objectIdBytes = StrictUtf8.GetBytes(metadata.ObjectId.Value);
        var header = new byte[FixedHeaderSize + objectIdBytes.Length];
        Magic.CopyTo(header, 0);
        header[4] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5, 4), metadata.SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9, 4), (int)metadata.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(13, 4), objectIdBytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(17, 8), ciphertextLength);
        nonce.CopyTo(header, 25);
        objectIdBytes.CopyTo(header, FixedHeaderSize);
        return header;
    }

    internal static byte[] DeriveObjectKey(ReadOnlySpan<byte> masterKey, EnvelopeMetadata metadata)
    {
        var objectIdBytes = StrictUtf8.GetBytes(metadata.ObjectId.Value);
        var info = new byte[ObjectKeyLabel.Length + 4 + objectIdBytes.Length];
        ObjectKeyLabel.CopyTo(info, 0);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(ObjectKeyLabel.Length, 4), objectIdBytes.Length);
        objectIdBytes.CopyTo(info, ObjectKeyLabel.Length + 4);
        var objectKey = new byte[ObjectKeySize];
        var succeeded = false;

        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, objectKey, ReadOnlySpan<byte>.Empty, info);
            succeeded = true;
            return objectKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(info);
            if (!succeeded)
            {
                CryptographicOperations.ZeroMemory(objectKey);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedPlaintextAsync(Stream source, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        using var content = new MemoryStream();
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return content.ToArray();
                }

                if (content.Length + read > MaximumPayloadBytes)
                {
                    throw new CryptographicException("Plaintext exceeds the supported encrypted-object size.");
                }

                await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (content.TryGetBuffer(out var plaintextBuffer))
            {
                CryptographicOperations.ZeroMemory(plaintextBuffer.AsSpan());
            }

            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<EncryptedEnvelope> ReadEnvelopeAsync(Stream source, CancellationToken cancellationToken)
    {
        var fixedHeader = new byte[FixedHeaderSize];
        await ReadExactlyOrFormatAsync(source, fixedHeader, cancellationToken).ConfigureAwait(false);

        if (!fixedHeader.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("Encrypted object magic is invalid.");
        }

        if (fixedHeader[4] != FormatVersion)
        {
            throw new CryptographicException("Encrypted object format version is unsupported.");
        }

        var schemaVersion = BinaryPrimitives.ReadInt32LittleEndian(fixedHeader.AsSpan(5, 4));
        var rawKind = BinaryPrimitives.ReadInt32LittleEndian(fixedHeader.AsSpan(9, 4));
        var objectIdLength = BinaryPrimitives.ReadInt32LittleEndian(fixedHeader.AsSpan(13, 4));
        var ciphertextLength = BinaryPrimitives.ReadInt64LittleEndian(fixedHeader.AsSpan(17, 8));

        if (schemaVersion < 1 || !Enum.IsDefined(typeof(ObjectKind), rawKind))
        {
            throw new CryptographicException("Encrypted object metadata is invalid.");
        }

        if (objectIdLength is < 1 or > MaximumObjectIdBytes)
        {
            throw new CryptographicException("Encrypted object ID length is invalid.");
        }

        if (ciphertextLength is < 0 or > MaximumPayloadBytes)
        {
            throw new CryptographicException("Encrypted payload length is invalid.");
        }

        var objectIdBytes = new byte[objectIdLength];
        await ReadExactlyOrFormatAsync(source, objectIdBytes, cancellationToken).ConfigureAwait(false);
        string objectId;
        try
        {
            objectId = StrictUtf8.GetString(objectIdBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CryptographicException("Encrypted object ID is not valid UTF-8.", exception);
        }

        if (string.IsNullOrWhiteSpace(objectId))
        {
            throw new CryptographicException("Encrypted object ID is invalid.");
        }

        var header = new byte[FixedHeaderSize + objectIdBytes.Length];
        fixedHeader.CopyTo(header, 0);
        objectIdBytes.CopyTo(header, FixedHeaderSize);
        var nonce = fixedHeader.AsSpan(25, NonceSize).ToArray();
        var encryptedBytes = await ReadDeclaredPayloadAsync(
            source,
            (int)ciphertextLength,
            cancellationToken).ConfigureAwait(false);
        var tag = new byte[TagSize];
        await ReadExactlyOrFormatAsync(source, tag, cancellationToken).ConfigureAwait(false);

        var trailingByte = new byte[1];
        if (await source.ReadAsync(trailingByte, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new CryptographicException("Encrypted object contains trailing data.");
        }

        return new EncryptedEnvelope(
            new EnvelopeMetadata(schemaVersion, new LogicalObjectId(objectId), (ObjectKind)rawKind),
            header,
            nonce,
            encryptedBytes,
            tag);
    }

    private static async Task<byte[]> ReadDeclaredPayloadAsync(
        Stream source,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            using var payload = new MemoryStream();
            var remaining = length;
            while (remaining > 0)
            {
                var requested = Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new CryptographicException("Encrypted object is truncated.");
                }

                await payload.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }

            return payload.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadExactlyOrFormatAsync(
        Stream source,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new CryptographicException("Encrypted object is truncated.");
            }

            totalRead += read;
        }
    }
}
