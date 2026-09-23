using SelfUpdateKit;

namespace CodexHistorySync.Cli;

/// <summary>
/// agent-sync wiring for the shared SelfUpdateKit: fixed repository, asset names for both
/// variants, and user agent. Update rules (mandatory checksum, staged probe with rollback)
/// come from the library, not from here. The repository is fixed in code on purpose: a
/// configurable download origin would be the shortest path from a stray environment variable
/// to an executable of somebody else's choosing.
/// </summary>
internal static class AgentSyncUpdate
{
    private const string Repository = "XYphrodite/agent-history-sync";
    private const string FullExecutableAsset = "agent-sync.exe";
    private const string LightExecutableAsset = "agent-sync-light.exe";
    private const string UserAgent = "agent-history-sync-update/1.0";

    /// <summary>
    /// Marker file the installer records next to the binary so an update keeps installing
    /// the same kind of build (<c>light</c> or <c>full</c>).
    /// </summary>
    internal const string VariantMarker = ".agent-sync-variant";

    internal enum Variant
    {
        Full,
        Light,
    }

    public static ReleaseSourceOptions Options() => Options(Variant.Full);

    public static ReleaseSourceOptions Options(Variant variant)
    {
        var asset = variant == Variant.Light ? LightExecutableAsset : FullExecutableAsset;
        return new()
        {
            Repository = Repository,
            ExecutableAssetNames = { asset },
            ChecksumAssetName = asset + ".sha256",
            ChecksumMode = ChecksumMode.SidecarAsset,
            UserAgent = UserAgent,
        };
    }

    /// <summary>
    /// Which build the installation came from. The installer records it next to the
    /// executable; an installation from before variants defaults to the full build, so
    /// an update never silently changes what kind of build is installed.
    /// </summary>
    public static Variant InstalledVariant(string? processPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(processPath) && Path.IsPathFullyQualified(processPath))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(processPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    var marker = Path.Combine(directory, VariantMarker);
                    if (File.Exists(marker) &&
                        string.Equals(File.ReadAllText(marker).Trim(), "light", StringComparison.OrdinalIgnoreCase))
                        return Variant.Light;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return Variant.Full;
    }
}
