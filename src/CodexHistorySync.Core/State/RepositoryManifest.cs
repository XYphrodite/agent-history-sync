using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.State;

public sealed record RepositoryManifest(int SchemaVersion, Argon2Parameters Argon2Parameters);

public sealed record DeviceState(
    int SchemaVersion,
    string RepositoryId,
    IReadOnlyList<ObjectVersion> Objects);
