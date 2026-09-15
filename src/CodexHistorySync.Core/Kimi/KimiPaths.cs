using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.IO;

namespace CodexHistorySync.Core.Kimi;

public sealed record KimiPaths(string Home, string Sessions)
{
    /// <summary>The shared index every session appears in; maintained by Kimi Code CLI itself.</summary>
    public const string IndexFileName = "session_index.jsonl";

    public const string SessionIdPrefix = "session_";

    public static KimiPaths? TryResolve(string? configuredHome = null)
    {
        try
        {
            var homeInput = configuredHome
                ?? Environment.GetEnvironmentVariable("KIMI_CODE_HOME")
                ?? GetDefaultHome();
            if (string.IsNullOrWhiteSpace(homeInput)) return null;
            var home = Path.GetFullPath(homeInput);
            if (!Directory.Exists(home)) return null;
            var sessions = Path.GetFullPath(Path.Combine(home, "sessions"));
            if (!Directory.Exists(sessions)) return null;
            return new KimiPaths(home, sessions);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetDefaultHome()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) return string.Empty;
        return Path.Combine(userProfile, ".kimi-code");
    }

    /// <summary>
    /// Bucket directory for one working directory: <c>wd_&lt;slug&gt;_&lt;first 12 hex of sha256(workDir)&gt;</c>.
    /// Verified against the Kimi Code CLI layout; the hash is the authoritative part, the slug only
    /// a readability hint, so a slug guess that differs from Kimi's own still lands in a distinct,
    /// self-consistent bucket (the index entry written at import carries the absolute path).
    /// </summary>
    public static string ComputeWorkDirKey(string workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir)) throw new ArgumentException("A working directory is required.", nameof(workDir));
        var normalized = workDir.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) throw new ArgumentException("A working directory is required.", nameof(workDir));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();
        return $"wd_{SlugFromPath(normalized)}_{hash}";
    }

    private static string SlugFromPath(string normalizedPath)
    {
        var lastSegment = normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(lastSegment.Length);
        foreach (var character in lastSegment.ToLowerInvariant())
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "workspace" : slug;
    }

    public string SessionDirectory(string workDirKey, string sessionId)
    {
        PathSafety.ValidateFileComponent(workDirKey, nameof(workDirKey));
        PathSafety.ValidateFileComponent(sessionId, nameof(sessionId));
        return Path.GetFullPath(Path.Combine(Sessions, workDirKey, sessionId));
    }

    public string IndexFilePath => Path.GetFullPath(Path.Combine(Home, IndexFileName));

    /// <summary>True when the path is the shared index rather than session data.</summary>
    public static bool IsIndexFile(string path) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(path), IndexFileName);

    /// <summary>
    /// True when the candidate sits at exactly the depth Kimi uses inside one session directory
    /// and names a file a package may carry. This is the destination guard that keeps an import
    /// inside the sessions tree from pointing at the shared index or at runtime-only content
    /// (logs, notify, tasks, cron) that synchronization never owns.
    /// </summary>
    public bool IsSynchronizedSessionFile(string candidate)
    {
        var relative = Path.GetRelativePath(Sessions, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 3 or > 6) return false;
        if (!segments[0].StartsWith("wd_", StringComparison.Ordinal) || !IsSafeComponent(segments[0])) return false;
        if (!segments[1].StartsWith(SessionIdPrefix, StringComparison.Ordinal) ||
            !IsSafeComponent(segments[1][SessionIdPrefix.Length..])) return false;
        if (segments.Length == 3)
            return StringComparer.OrdinalIgnoreCase.Equals(segments[2], "state.json");
        if (segments.Length == 5)
            return StringComparer.OrdinalIgnoreCase.Equals(segments[2], "agents") &&
                   IsSafeComponent(segments[3]) &&
                   StringComparer.OrdinalIgnoreCase.Equals(segments[4], "wire.jsonl");
        if (segments.Length == 6)
            return StringComparer.OrdinalIgnoreCase.Equals(segments[2], "agents") &&
                   IsSafeComponent(segments[3]) &&
                   StringComparer.OrdinalIgnoreCase.Equals(segments[4], "plans") &&
                   segments[5].EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                   IsSafeComponent(segments[5][..^3]);
        return false;
    }

    private static bool IsSafeComponent(string segment) =>
        segment.Length != 0 && segment is not "." and not ".." &&
        segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
