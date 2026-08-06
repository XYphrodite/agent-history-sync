namespace CodexHistorySync.Core.Grok;

public sealed record GrokPaths(string Home, string Sessions)
{
    public static GrokPaths? TryResolve(string? configuredHome = null)
    {
        try
        {
            var homeInput = configuredHome
                ?? Environment.GetEnvironmentVariable("GROK_HOME")
                ?? GetDefaultHome();
            if (string.IsNullOrWhiteSpace(homeInput)) return null;
            var home = Path.GetFullPath(homeInput);
            if (!Directory.Exists(home)) return null;
            var sessions = Path.GetFullPath(Path.Combine(home, "sessions"));
            if (!Directory.Exists(sessions)) return null;
            return new GrokPaths(home, sessions);
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
        return Path.Combine(userProfile, ".grok");
    }

    public static string EncodeCwdSegment(string cwd)
    {
        // Matches Grok CLI layout: C%3A%5CRepos%5CReborn
        return Uri.EscapeDataString(Path.GetFullPath(cwd));
    }

    public string SessionDirectory(string cwd, string sessionId)
    {
        return Path.GetFullPath(Path.Combine(Sessions, EncodeCwdSegment(cwd), sessionId));
    }
}
