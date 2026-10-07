using System.Diagnostics;
using System.Text;

namespace CodexHistorySync.Core.Hermes;

/// <summary>
/// Finds every Hermes home on one machine: the native Windows install
/// (<c>%LOCALAPPDATA%\hermes</c>), the legacy <c>%USERPROFILE%\.hermes</c> tree, and the homes of
/// running WSL distributions reached through <c>\\wsl.localhost</c> (falling back to <c>\\wsl$</c>).
/// An explicitly configured home (a parameter or <c>HERMES_HOME</c>) stays exclusive — profiles and
/// tests rely on one configured home meaning exactly one home.
/// </summary>
internal static class HermesHomeDiscovery
{
    /// <summary>Set to <c>0</c>, <c>false</c>, or <c>off</c> to skip WSL distributions entirely.</summary>
    public const string WslSwitch = "AGENT_SYNC_HERMES_WSL";

    /// <summary>
    /// Ordered, de-duplicated candidate homes. The primary home comes first: the configured home,
    /// otherwise <c>%LOCALAPPDATA%\hermes</c>, <c>%USERPROFILE%\.hermes</c>, then WSL homes.
    /// </summary>
    public static IReadOnlyList<string> CandidateHomes(
        string? configuredHome,
        Func<IReadOnlyList<string>>? wslHomeProbe = null,
        IReadOnlyList<string>? machineDefaults = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredHome)) return [configuredHome];

        var candidates = new List<string>();
        if (machineDefaults is not null) candidates.AddRange(machineDefaults);
        else
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(local)) candidates.Add(Path.Combine(local, "hermes"));
            if (!string.IsNullOrWhiteSpace(user)) candidates.Add(Path.Combine(user, ".hermes"));
        }
        candidates.AddRange((wslHomeProbe ?? WslHomes)());
        return Deduplicate(candidates);
    }

    /// <summary>Homes of running WSL distributions that actually hold a Hermes state database.</summary>
    public static IReadOnlyList<string> WslHomes()
    {
        if (!WslScanEnabled()) return [];
        var homes = new List<string>();
        foreach (var distro in ListWslDistros())
        {
            // One reachability root per distribution is enough; the first that resolves wins.
            foreach (var root in new[] { @"\\wsl.localhost", @"\\wsl$" })
            {
                var found = DistroHomes(Path.Combine(root, distro));
                if (found.Count == 0) continue;
                homes.AddRange(found);
                break;
            }
        }
        return Deduplicate(homes);
    }

    /// <summary>One <c>.hermes</c> home per Linux account: every <c>home/*</c> directory plus <c>root</c>.</summary>
    private static IReadOnlyList<string> DistroHomes(string distroRoot)
    {
        var homes = new List<string>();
        var accounts = new List<string>();
        try
        {
            var userRoot = Path.Combine(distroRoot, "home");
            if (Directory.Exists(Path.Combine(distroRoot, "root"))) accounts.Add(Path.Combine(distroRoot, "root"));
            if (Directory.Exists(userRoot))
                accounts.AddRange(Directory.EnumerateDirectories(userRoot).OrderBy(path => path, StringComparer.Ordinal));
        }
        catch (Exception exception) when (IsDiscoveryFailure(exception))
        {
            return [];
        }

        foreach (var account in accounts)
        {
            var candidate = Path.Combine(account, ".hermes");
            if (HermesPaths.HasAnyDatabase(candidate)) homes.Add(candidate);
        }
        return homes;
    }

    private static IReadOnlyList<string> ListWslDistros()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.Unicode
            };
            start.ArgumentList.Add("--list");
            start.ArgumentList.Add("--quiet");
            using var process = Process.Start(start);
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception exception) when (IsDiscoveryFailure(exception) || exception is InvalidOperationException) { }
                return [];
            }
            return output
                .Split('\n')
                .Select(line => line.Trim('\0', '\r', ' ', '\t', '*'))
                .Where(name => name.Length != 0 && !name.Contains('\\') && !name.Contains('/') && !name.Contains("..", StringComparison.Ordinal))
                .Select(StripDefaultMarker)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception) when (IsDiscoveryFailure(exception) || exception is InvalidOperationException)
        {
            return [];
        }
    }

    private static string StripDefaultMarker(string name)
    {
        const string marker = " (default)";
        return name.EndsWith(marker, StringComparison.OrdinalIgnoreCase) ? name[..^marker.Length] : name;
    }

    private static bool WslScanEnabled()
    {
        var value = Environment.GetEnvironmentVariable(WslSwitch);
        if (string.IsNullOrWhiteSpace(value)) return true;
        return !value.Equals("0", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("off", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> Deduplicate(IEnumerable<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (seen.Add(candidate)) result.Add(candidate);
        }
        return result;
    }

    private static bool IsDiscoveryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
