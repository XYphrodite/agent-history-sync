using CodexHistorySync.Core.IO;

namespace CodexHistorySync.Core.Claude;

public sealed record ClaudePaths(string Home, string Projects)
{
    public static ClaudePaths? TryResolve(string? configuredHome = null)
    {
        try
        {
            var homeInput = configuredHome
                ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                ?? GetDefaultHome();
            if (string.IsNullOrWhiteSpace(homeInput)) return null;
            var home = Path.GetFullPath(homeInput);
            if (!Directory.Exists(home)) return null;
            var projects = Path.GetFullPath(Path.Combine(home, "projects"));
            if (!Directory.Exists(projects)) return null;
            return new ClaudePaths(home, projects);
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
        return Path.Combine(userProfile, ".claude");
    }

    /// <summary>
    /// Forward-encodes a cwd into Claude's project directory name: <c>c:\Repos\Reborn</c> becomes
    /// <c>c--Repos-Reborn</c>. Only this direction is well defined — D1 forbids the reverse, because
    /// both ':' and '\' collapse to the same character. Used when creating a new session from a
    /// conversation copied out of another agent; a synchronized session carries its segment instead.
    /// </summary>
    public static string EncodeProjectSegment(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) throw new ArgumentException("A working directory is required.", nameof(cwd));
        var segment = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var builder = new System.Text.StringBuilder(segment.Length);
        foreach (var character in segment)
            builder.Append(character is ':' or '\\' or '/' ? '-' : character);
        return builder.ToString();
    }

    /// <summary>
    /// Destination for a session file. The project segment is carried through from the source
    /// machine verbatim: Claude derives it from the cwd by collapsing both ':' and '\' to '-',
    /// which is not reversible, so it must never be reconstructed (design D1).
    /// </summary>
    public string SessionFilePath(string projectSegment, string sessionId)
    {
        PathSafety.ValidateFileComponent(projectSegment, nameof(projectSegment));
        PathSafety.ValidateFileComponent(sessionId, nameof(sessionId));
        return Path.GetFullPath(Path.Combine(Projects, projectSegment, sessionId + ".jsonl"));
    }
}
