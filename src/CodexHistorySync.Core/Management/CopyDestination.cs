namespace CodexHistorySync.Core.Management;

/// <summary>
/// One row in the copy picker. Hermes homes are separate rows so a Windows install and a WSL
/// install are not the same destination.
/// </summary>
public sealed record CopyDestination(ManagedAgent Agent, string Label, string? HermesHome = null)
{
    public static CopyDestination For(ManagedAgent agent) => new(agent, DisplayName(agent));

    public static string DisplayName(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => "Codex",
        ManagedAgent.Grok => "Grok",
        ManagedAgent.Claude => "Claude",
        ManagedAgent.Continue => "Continue",
        ManagedAgent.Kimi => "Kimi",
        ManagedAgent.Muse => "Muse",
        ManagedAgent.Hermes => "Hermes",
        ManagedAgent.Mimo => "MiMo",
        _ => agent.ToString()
    };

    /// <summary>
    /// One label per home, in the same order. A single Windows home and a single WSL home stay
    /// <c>Hermes (Windows)</c> and <c>Hermes (WSL)</c>. A second home of the same kind gets a
    /// distro or folder hint so the two rows are not identical.
    /// </summary>
    public static IReadOnlyList<string> LabelsForHermesHomes(IReadOnlyList<string> homes)
    {
        ArgumentNullException.ThrowIfNull(homes);
        var classified = homes.Select(Classify).ToArray();
        var wslCount = classified.Count(home => home.Wsl);
        var windowsCount = classified.Length - wslCount;
        return classified.Select(home => HomeLabel(home, wslCount, windowsCount)).ToArray();
    }

    private static string HomeLabel(ClassifiedHome home, int wslCount, int windowsCount)
    {
        if (home.Wsl)
            return wslCount > 1 && home.Distro is not null ? "Hermes (WSL " + home.Distro + ")" : "Hermes (WSL)";
        return windowsCount > 1 ? "Hermes (Windows, " + WindowsHint(home.Parts) + ")" : "Hermes (Windows)";
    }

    private static string WindowsHint(IReadOnlyList<string> parts)
    {
        if (parts.Any(part => part.Equals("AppData", StringComparison.OrdinalIgnoreCase))) return "AppData";
        return parts.Count == 0 ? "local" : parts[^1];
    }

    private static ClassifiedHome Classify(string home)
    {
        var parts = home.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            if (!parts[index].Equals("wsl.localhost", StringComparison.OrdinalIgnoreCase) &&
                !parts[index].Equals("wsl$", StringComparison.OrdinalIgnoreCase))
                continue;
            return new ClassifiedHome(true, index + 1 < parts.Length ? parts[index + 1] : null, parts);
        }

        return new ClassifiedHome(false, null, parts);
    }

    private sealed record ClassifiedHome(bool Wsl, string? Distro, IReadOnlyList<string> Parts);
}
