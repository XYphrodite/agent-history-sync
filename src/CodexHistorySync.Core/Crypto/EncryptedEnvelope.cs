using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Crypto;

public sealed record Argon2Parameters(
    byte[] Salt,
    int MemoryKiB,
    int Iterations,
    int Parallelism);

public sealed record EnvelopeMetadata(
    int SchemaVersion,
    LogicalObjectId ObjectId,
    ObjectKind Kind);

internal sealed record EncryptedEnvelope(
    EnvelopeMetadata Metadata,
    byte[] Header,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag);
