using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Model;
using Konscious.Security.Cryptography;

namespace CodexHistorySync.Core.Tests.Crypto;

public sealed class RepositoryCryptoTests
{
    private static readonly EnvelopeMetadata Metadata = new(
        SchemaVersion: 1,
        ObjectId: new LogicalObjectId("object-7fbd"),
        Kind: ObjectKind.ActiveSession);

    [Fact]
    public async Task DeriveMasterKeyAsync_ReturnsStable32ByteArgon2idKey()
    {
        var crypto = new RepositoryCrypto();
        var parameters = TestParameters();

        var first = await crypto.DeriveMasterKeyAsync("correct horse".AsMemory(), parameters, CancellationToken.None);
        var second = await crypto.DeriveMasterKeyAsync("correct horse".AsMemory(), parameters, CancellationToken.None);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        CryptographicOperations.ZeroMemory(first);
        CryptographicOperations.ZeroMemory(second);
    }

    [Fact]
    public async Task DeriveMasterKeyAsync_SeparatesSaltAndArgon2Parameters()
    {
        var crypto = new RepositoryCrypto();
        var baselineParameters = TestParameters();
        var differentSalt = TestParameters() with
        {
            Salt = Convert.FromHexString("10112233445566778899AABBCCDDEEFF")
        };
        var differentIterations = TestParameters() with { Iterations = 3 };

        var baseline = await crypto.DeriveMasterKeyAsync("correct horse".AsMemory(), baselineParameters, CancellationToken.None);
        var saltSeparated = await crypto.DeriveMasterKeyAsync("correct horse".AsMemory(), differentSalt, CancellationToken.None);
        var parameterSeparated = await crypto.DeriveMasterKeyAsync("correct horse".AsMemory(), differentIterations, CancellationToken.None);

        Assert.NotEqual(baseline, saltSeparated);
        Assert.NotEqual(baseline, parameterSeparated);
        CryptographicOperations.ZeroMemory(baseline);
        CryptographicOperations.ZeroMemory(saltSeparated);
        CryptographicOperations.ZeroMemory(parameterSeparated);
    }

    [Fact]
    public async Task Argon2idDependency_MatchesRfc9106KnownAnswerVector()
    {
        var password = Enumerable.Repeat((byte)0x01, 32).ToArray();
        var salt = Enumerable.Repeat((byte)0x02, 16).ToArray();
        var secret = Enumerable.Repeat((byte)0x03, 8).ToArray();
        var associatedData = Enumerable.Repeat((byte)0x04, 12).ToArray();
        var expected = Convert.FromHexString("0D640DF58D78766C08C037A34A8B53C9D01EF0452D75B65EB52520E96B01E659");
        using var argon2 = new Argon2id(password)
        {
            Salt = salt,
            KnownSecret = secret,
            AssociatedData = associatedData,
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4
        };

        var actual = await argon2.GetBytesAsync(32);

        Assert.Equal(expected, actual);
        CryptographicOperations.ZeroMemory(password);
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(actual);
    }

    [Fact]
    public async Task DeriveMasterKeyAsync_EnforcesRepositorySaltSize()
    {
        var crypto = new RepositoryCrypto();
        var parameters = TestParameters() with { Salt = new byte[15] };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            crypto.DeriveMasterKeyAsync("passphrase".AsMemory(), parameters, CancellationToken.None));
    }

    [Fact]
    public void Argon2Defaults_MatchRepositorySecurityProfile()
    {
        Assert.Equal(65_536, RepositoryCrypto.DefaultMemoryKiB);
        Assert.Equal(3, RepositoryCrypto.DefaultIterations);
        Assert.Equal(2, RepositoryCrypto.DefaultParallelism);
    }

    [Fact]
    public void DeriveObjectKey_SeparatesLogicalObjects()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var firstMetadata = Metadata;
        var secondMetadata = Metadata with { ObjectId = new LogicalObjectId("object-8ace") };
        var firstKey = RepositoryCrypto.DeriveObjectKey(masterKey, firstMetadata);
        var secondKey = RepositoryCrypto.DeriveObjectKey(masterKey, secondMetadata);

        Assert.NotEqual(firstKey, secondKey);
        CryptographicOperations.ZeroMemory(masterKey);
        CryptographicOperations.ZeroMemory(firstKey);
        CryptographicOperations.ZeroMemory(secondKey);
    }

    [Fact]
    public async Task EncryptAndDecryptAsync_RoundTripsPlaintext()
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("history payload");

        await using var encrypted = new MemoryStream();
        await crypto.EncryptAsync(new MemoryStream(plaintext), encrypted, key, Metadata, CancellationToken.None);
        encrypted.Position = 0;
        await using var decrypted = new MemoryStream();

        await crypto.DecryptAsync(encrypted, decrypted, key, Metadata, CancellationToken.None);

        Assert.Equal(plaintext, decrypted.ToArray());
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task EncryptAsync_UsesFreshNonceForIdenticalPlaintext()
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("same history payload");

        var first = await EncryptAsync(crypto, plaintext, key, Metadata);
        var second = await EncryptAsync(crypto, plaintext, key, Metadata);

        Assert.NotEqual(first, second);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task DecryptAsync_RejectsWrongPassphraseWithoutWritingPlaintext()
    {
        var crypto = new RepositoryCrypto();
        var parameters = TestParameters();
        var encryptionKey = await crypto.DeriveMasterKeyAsync("correct passphrase".AsMemory(), parameters, CancellationToken.None);
        var wrongKey = await crypto.DeriveMasterKeyAsync("wrong passphrase".AsMemory(), parameters, CancellationToken.None);
        var envelope = await EncryptAsync(crypto, Encoding.UTF8.GetBytes("sensitive payload"), encryptionKey, Metadata);
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
            crypto.DecryptAsync(new MemoryStream(envelope), destination, wrongKey, Metadata, CancellationToken.None));

        Assert.Empty(destination.ToArray());
        CryptographicOperations.ZeroMemory(encryptionKey);
        CryptographicOperations.ZeroMemory(wrongKey);
    }

    [Fact]
    public async Task DecryptAsync_RejectsCiphertextMutationWithoutWritingPlaintext()
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = await EncryptAsync(crypto, Encoding.UTF8.GetBytes("sensitive payload"), key, Metadata);
        envelope[^17] ^= 0x40;
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
            crypto.DecryptAsync(new MemoryStream(envelope), destination, key, Metadata, CancellationToken.None));

        Assert.Empty(destination.ToArray());
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task DecryptAsync_RejectsAssociatedDataMutationWithoutWritingPlaintext()
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = await EncryptAsync(crypto, Encoding.UTF8.GetBytes("sensitive payload"), key, Metadata);
        envelope[9] ^= 0x01;
        await using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            crypto.DecryptAsync(new MemoryStream(envelope), destination, key, Metadata, CancellationToken.None));

        Assert.Empty(destination.ToArray());
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task DecryptAsync_RejectsExpectedMetadataMismatchWithoutWritingPlaintext()
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = await EncryptAsync(crypto, Encoding.UTF8.GetBytes("sensitive payload"), key, Metadata);
        var differentMetadata = Metadata with { ObjectId = new LogicalObjectId("different-object") };
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<CryptographicException>(() =>
            crypto.DecryptAsync(new MemoryStream(envelope), destination, key, differentMetadata, CancellationToken.None));

        Assert.Empty(destination.ToArray());
        CryptographicOperations.ZeroMemory(key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public async Task DecryptAsync_RejectsTruncatedEnvelopeAsCryptographicFailure(int retainedBytes)
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = await EncryptAsync(crypto, Encoding.UTF8.GetBytes("payload"), key, Metadata);
        Array.Resize(ref envelope, retainedBytes);

        var exception = await Record.ExceptionAsync(() =>
            crypto.DecryptAsync(new MemoryStream(envelope), new MemoryStream(), key, Metadata, CancellationToken.None));

        Assert.IsAssignableFrom<CryptographicException>(exception);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task DecryptAsync_RejectsTruncationInsideObjectIdCiphertextAndTagWithoutWritingPlaintext()
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = await EncryptAsync(crypto, Encoding.UTF8.GetBytes("payload"), key, Metadata);
        var headerLength = 37 + Encoding.UTF8.GetByteCount(Metadata.ObjectId.Value);
        var retainedLengths = new[] { 38, headerLength + 6, envelope.Length - 1 };

        foreach (var retainedLength in retainedLengths)
        {
            await using var destination = new MemoryStream();
            var truncated = envelope[..retainedLength];

            var exception = await Record.ExceptionAsync(() =>
                crypto.DecryptAsync(new MemoryStream(truncated), destination, key, Metadata, CancellationToken.None));

            Assert.IsAssignableFrom<CryptographicException>(exception);
            Assert.Empty(destination.ToArray());
        }

        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task DecryptAsync_RejectsTrailingBytesWithoutWritingPlaintext()
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = await EncryptAsync(crypto, Encoding.UTF8.GetBytes("payload"), key, Metadata);
        Array.Resize(ref envelope, envelope.Length + 1);
        envelope[^1] = 0x5a;
        await using var destination = new MemoryStream();

        var exception = await Record.ExceptionAsync(() =>
            crypto.DecryptAsync(new MemoryStream(envelope), destination, key, Metadata, CancellationToken.None));

        Assert.IsAssignableFrom<CryptographicException>(exception);
        Assert.Empty(destination.ToArray());
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task DecryptAsync_RejectsUnknownFormatVersionAsCryptographicFailure()
    {
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = await EncryptAsync(crypto, Encoding.UTF8.GetBytes("payload"), key, Metadata);
        envelope[4] = 2;

        var exception = await Record.ExceptionAsync(() =>
            crypto.DecryptAsync(new MemoryStream(envelope), new MemoryStream(), key, Metadata, CancellationToken.None));

        Assert.IsAssignableFrom<CryptographicException>(exception);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task DecryptAsync_RejectsOversizedObjectIdLengthBeforeFurtherReads()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var malformed = new byte[37];
        "CHS1"u8.CopyTo(malformed);
        malformed[4] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(5, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(9, 4), (int)ObjectKind.ActiveSession);
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(13, 4), int.MaxValue);
        var crypto = new RepositoryCrypto();
        await using var source = new MemoryStream(malformed);

        var exception = await Record.ExceptionAsync(() =>
            crypto.DecryptAsync(source, new MemoryStream(), key, Metadata, CancellationToken.None));

        Assert.IsAssignableFrom<CryptographicException>(exception);
        Assert.Equal(37, source.Position);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task DecryptAsync_RejectsOversizedCiphertextLengthBeforeFurtherReads()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var malformed = new byte[38];
        "CHS1"u8.CopyTo(malformed);
        malformed[4] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(5, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(9, 4), (int)ObjectKind.ActiveSession);
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(13, 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(malformed.AsSpan(17, 8), long.MaxValue);
        malformed[37] = (byte)'x';
        var crypto = new RepositoryCrypto();
        await using var source = new MemoryStream(malformed);

        var exception = await Record.ExceptionAsync(() =>
            crypto.DecryptAsync(source, new MemoryStream(), key, Metadata, CancellationToken.None));

        Assert.IsAssignableFrom<CryptographicException>(exception);
        Assert.Equal(37, source.Position);
        CryptographicOperations.ZeroMemory(key);
    }

    private static Argon2Parameters TestParameters() => new(
        Salt: Convert.FromHexString("00112233445566778899AABBCCDDEEFF"),
        MemoryKiB: 1024,
        Iterations: 2,
        Parallelism: 1);

    private static async Task<byte[]> EncryptAsync(
        RepositoryCrypto crypto,
        byte[] plaintext,
        byte[] key,
        EnvelopeMetadata metadata)
    {
        await using var destination = new MemoryStream();
        await crypto.EncryptAsync(new MemoryStream(plaintext), destination, key, metadata, CancellationToken.None);
        return destination.ToArray();
    }
}
