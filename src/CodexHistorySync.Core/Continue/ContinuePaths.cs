using CodexHistorySync.Core.IO;

namespace CodexHistorySync.Core.Continue;

public sealed record ContinuePaths(string Home, string Sessions)
{
    /// <summary>The shared index every session must appear in to be visible in Continue.</summary>
    public const string IndexFileName = "sessions.json";

    public static ContinuePaths? TryResolve(string? configuredHome = null)
    {
        try
        {
            var homeInput = configuredHome
                ?? Environment.GetEnvironmentVariable("CONTINUE_GLOBAL_DIR")
                ?? GetDefaultHome();
            if (string.IsNullOrWhiteSpace(homeInput)) return null;
            var home = Path.GetFullPath(homeInput);
            if (!Directory.Exists(home)) return null;
            var sessions = Path.GetFullPath(Path.Combine(home, "sessions"));
            if (!Directory.Exists(sessions)) return null;
            return new ContinuePaths(home, sessions);
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
        return Path.Combine(userProfile, ".continue");
    }

    /// <summary>
    /// Destination for one session. Sessions are flat under <c>sessions/</c>, so unlike Claude
    /// there is no project directory to carry — but the index shares that directory, and writing
    /// a session over it would destroy the list of every local session.
    /// </summary>
    public string SessionFilePath(string sessionId)
    {
        PathSafety.ValidateFileComponent(sessionId, nameof(sessionId));
        if (StringComparer.OrdinalIgnoreCase.Equals(sessionId, "sessions"))
            throw new ArgumentException("The session index is not a session.", nameof(sessionId));
        return Path.GetFullPath(Path.Combine(Sessions, sessionId + ".json"));
    }

    public string IndexFilePath => Path.GetFullPath(Path.Combine(Sessions, IndexFileName));

    /// <summary>True when the path is the shared index rather than a session.</summary>
    public static bool IsIndexFile(string path) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(path), IndexFileName);
}
