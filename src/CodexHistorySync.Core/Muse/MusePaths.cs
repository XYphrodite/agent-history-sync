using System.Text;
using CodexHistorySync.Core.IO;

namespace CodexHistorySync.Core.Muse;

public sealed record MusePaths(string Home, string Sessions)
{
    public const string SessionFileName = "session.jsonl";

    public static MusePaths? TryResolve(string? configuredHome = null)
    {
        try
        {
            var homeInput = configuredHome
                ?? Environment.GetEnvironmentVariable("MUSE_HOME")
                ?? Environment.GetEnvironmentVariable("MUSE_CODE_HOME")
                ?? GetDefaultHome();
            if (string.IsNullOrWhiteSpace(homeInput)) return null;
            var home = Path.GetFullPath(homeInput);
            if (!Directory.Exists(home)) return null;
            var sessions = Path.GetFullPath(Path.Combine(home, "sessions"));
            if (!Directory.Exists(sessions)) return null;
            return new MusePaths(home, sessions);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetDefaultHome()
    {
        // Check UserProfile/.local/share/muse (Linux/WSL)
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var linuxPath = Path.Combine(userProfile, ".local", "share", "muse");
            if (Directory.Exists(linuxPath)) return linuxPath;

            // Check WSL path when running on Windows: \\wsl$\Ubuntu\home\<user>\.local\share\muse
            // Try to resolve WSL home via wsl$ if on Windows
            if (OperatingSystem.IsWindows())
            {
                var wslPath = TryResolveWslHome(userProfile);
                if (wslPath is not null && Directory.Exists(wslPath)) return wslPath;
            }
        }

        // Fallback: check LOCALAPPDATA/muse
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var localPath = Path.Combine(localAppData, "muse");
            if (Directory.Exists(localPath)) return localPath;
        }

        // Final fallback: UserProfile/.muse
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var dotMuse = Path.Combine(userProfile, ".muse");
            if (Directory.Exists(dotMuse))
            {
                var dotMuseSessions = Path.Combine(dotMuse, "sessions");
                if (Directory.Exists(dotMuseSessions)) return dotMuse;
            }
        }

        // If nothing exists, return the Linux default for new installations
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, ".local", "share", "muse");
        }

        return string.Empty;
    }

    private static string? TryResolveWslHome(string windowsUserProfile)
    {
        try
        {
            var userName = Path.GetFileName(windowsUserProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(userName)) return null;
            var wslHome = $@"\\wsl$\Ubuntu\home\{userName}\.local\share\muse";
            // Also try with different distro names
            var candidates = new[]
            {
                wslHome,
                $@"\\wsl$\Ubuntu-22.04\home\{userName}\.local\share\muse",
                $@"\\wsl$\Ubuntu-24.04\home\{userName}\.local\share\muse",
            };
            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate)) return candidate;
            }
            // Also try via /mnt/c path if running in WSL itself, check /home
            var wslHomeLinux = $"/home/{userName}/.local/share/muse";
            if (Directory.Exists(wslHomeLinux)) return wslHomeLinux;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public string SessionFilePath(string sessionId)
    {
        // Muse sessions are stored as sessions/YYYY/MM/DD/<uuid>/session.jsonl
        // We need to find the session by scanning, but for direct path we search
        var pattern = $"{sessionId}{Path.DirectorySeparatorChar}{SessionFileName}";
        // This is a helper, actual discovery is via scanner enumeration
        return Path.GetFullPath(Path.Combine(Sessions, pattern));
    }

    public bool IsSynchronizedSessionFile(string candidate)
    {
        var relative = Path.GetRelativePath(Sessions, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        // Expected: YYYY/MM/DD/<uuid>/session.jsonl  (5 segments) or with subagent/tool-outputs (more)
        // For now, only sync the main session.jsonl
        if (segments.Length == 5 && StringComparer.OrdinalIgnoreCase.Equals(segments[4], SessionFileName))
        {
            // Validate YYYY, MM, DD, uuid
            if (segments[0].Length == 4 && int.TryParse(segments[0], out _) &&
                segments[1].Length == 2 && int.TryParse(segments[1], out _) &&
                segments[2].Length == 2 && int.TryParse(segments[2], out _) &&
                IsValidUuid(segments[3]))
                return true;
        }
        return false;
    }

    private static bool IsValidUuid(string value) =>
        Guid.TryParse(value, out _);

    public static bool IsSessionFile(string path) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(path), SessionFileName);
}
