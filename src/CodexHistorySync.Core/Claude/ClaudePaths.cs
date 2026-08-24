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
