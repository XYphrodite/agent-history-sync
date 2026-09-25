using Microsoft.Win32;

namespace CodexHistorySync.Core.Muse;

internal static class MuseWslDiscovery
{
    internal static string SevenZipPath()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var candidate = Path.Combine(root, "7-Zip", "7z.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
    }

    internal static bool TryUncHome(string path, out string distribution, out string home)
    {
        distribution = ""; home = "";
        var normalized = path.Replace('/', '\\');
        var prefix = new[] { @"\\wsl$\", @"\\wsl.localhost\" }
            .FirstOrDefault(value => normalized.StartsWith(value, StringComparison.OrdinalIgnoreCase));
        if (prefix is null) return false;
        var parts = normalized[prefix.Length..].TrimEnd('\\').Split('\\');
        if (parts.Length < 2 || parts.Any(part => part.Length == 0 || part is "." or ".." || part.IndexOfAny([':', '*', '?']) >= 0))
            throw new ArgumentException("Invalid WSL Muse home.");
        distribution = parts[0];
        home = string.Join('/', parts.Skip(1));
        return true;
    }

    internal static bool IsStopped(string distribution)
    {
        var running = MusePaths.RunWsl(["--list", "--running", "--quiet"]);
        return running is not null && !Names(running).Contains(distribution, StringComparer.OrdinalIgnoreCase);
    }

    internal static string[] Names(string output) => output.Replace("\0", "").Split(['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static MusePaths? Resolve(string? configuredHome)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? requested = null;
        string? home = null;
        if (configuredHome is not null)
        {
            if (!TryUncHome(configuredHome, out var name, out var inside)) return null;
            requested = name; home = inside;
        }
        var output = MusePaths.RunWsl(["--list", "--running", "--quiet"]);
        if (output is null) return null;
        var running = Names(output);
        if (requested is not null && running.Contains(requested, StringComparer.OrdinalIgnoreCase))
            return MusePaths.ExistingHome(configuredHome!);
        using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (registry is null) return null;
        var disks = new List<MuseWslDisk>();
        var tool = SevenZipPath();
        foreach (var key in registry.GetSubKeyNames())
        {
            using var distro = registry.OpenSubKey(key);
            if (distro?.GetValue("Version") is not int version || version != 2 ||
                distro.GetValue("DistributionName") is not string name ||
                distro.GetValue("BasePath") is not string root ||
                name.IndexOfAny(['/', '\\']) >= 0 || running.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                requested is not null && !string.Equals(requested, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (root.StartsWith(@"\??\", StringComparison.Ordinal)) root = root[4..];
            var fileName = distro.GetValue("VhdFileName") as string ?? "ext4.vhdx";
            if (Path.GetFileName(fileName) != fileName) continue;
            var image = Path.Combine(root, fileName);
            if (!File.Exists(image)) continue;
            disks.Add(new MuseWslDisk(name, image, home, new SevenZipMuseArchive(tool, image), () => IsStopped(name)));
        }
        if (disks.Count == 0) return null;
        // These are display identities only. No filesystem API may probe the stopped distribution's UNC path.
        var identity = configuredHome ?? @"\\wsl$";
        return new MusePaths(identity, identity + @"\sessions") { OfflineDisks = disks };
    }
}
