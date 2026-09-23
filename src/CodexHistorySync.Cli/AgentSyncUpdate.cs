using SelfUpdateKit;

namespace CodexHistorySync.Cli;

/// <summary>
/// agent-sync wiring for the shared SelfUpdateKit: fixed repository, asset names, and user
/// agent. Update rules (mandatory checksum, staged probe with rollback) come from the
/// library, not from here. The repository is fixed in code on purpose: a configurable
/// download origin would be the shortest path from a stray environment variable to an
/// executable of somebody else's choosing.
/// </summary>
internal static class AgentSyncUpdate
{
    private const string Repository = "XYphrodite/agent-history-sync";
    private const string ExecutableAsset = "agent-sync.exe";
    private const string ChecksumAsset = "agent-sync.exe.sha256";
    private const string UserAgent = "agent-history-sync-update/1.0";

    public static ReleaseSourceOptions Options() => new()
    {
        Repository = Repository,
        ExecutableAssetNames = { ExecutableAsset },
        ChecksumAssetName = ChecksumAsset,
        ChecksumMode = ChecksumMode.SidecarAsset,
        UserAgent = UserAgent,
    };
}
