using System.Diagnostics;
using CodexHistorySync.Core.IO;

namespace CodexHistorySync.Core.Muse;

public sealed record MusePaths(string Home, string Sessions)
{
    public const string SessionFileName = "session.jsonl";
    internal IReadOnlyList<MuseWslDisk> OfflineDisks { get; init; } = [];
    public bool IsReadOnly => OfflineDisks.Count > 0;

    public static MusePaths? TryResolve(string? configuredHome = null)
    {
        try
        {
            var explicitHome = configuredHome
                ?? Environment.GetEnvironmentVariable("MUSE_HOME")
                ?? Environment.GetEnvironmentVariable("MUSE_CODE_HOME");
            if (explicitHome is not null && MuseWslDiscovery.TryUncHome(explicitHome, out _, out _))
                return MuseWslDiscovery.Resolve(explicitHome);
            var homeInput = explicitHome ?? GetDefaultHome();
            if (explicitHome is null && string.IsNullOrWhiteSpace(homeInput)) return MuseWslDiscovery.Resolve(null);
            if (string.IsNullOrWhiteSpace(homeInput)) return null;
            return ExistingHome(homeInput);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static MusePaths? ExistingHome(string input)
    {
        var home = Path.GetFullPath(input);
        var sessions = Path.Combine(home, "sessions");
        return Directory.Exists(sessions) ? new MusePaths(home, sessions) : null;
    }

    private static string GetDefaultHome()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".local", "share", "muse"),
            string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "muse"),
            string.IsNullOrWhiteSpace(profile) ? null : Path.Combine(profile, ".muse")
        };
        foreach (var candidate in candidates)
            if (candidate is not null && Directory.Exists(Path.Combine(candidate, "sessions"))) return candidate;
        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in RunningWslHomes(RunWsl))
                if (Directory.Exists(Path.Combine(candidate, "sessions"))) return candidate;
        }
        return string.Empty;
    }

    // Query the Linux account, preserving case. Never start a stopped distribution just to show a list.
    internal static IEnumerable<string> RunningWslHomes(Func<string[], string?> run)
    {
        var distributions = run(["--list", "--running", "--quiet"]);
        if (distributions is null) yield break;
        foreach (var distro in distributions.Replace("\0", string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = distro.Trim();
            if (name.Length == 0 || name.IndexOfAny(['/', '\\']) >= 0) continue;
            var home = run(["--distribution", name, "--exec", "printenv", "HOME"])?.Trim();
            if (home is null || !home.StartsWith('/') || home.IndexOfAny(['\r', '\n', '\\']) >= 0) continue;
            var segments = home.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment is "." or "..")) continue;
            yield return @"\\wsl$\" + name + home.Replace('/', '\\') + @"\.local\share\muse";
        }
    }

    internal static string? RunWsl(string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = arguments[0] == "--list" ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 && output.Wait(200) ? output.Result : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
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
