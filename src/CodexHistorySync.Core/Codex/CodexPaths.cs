namespace CodexHistorySync.Core.Codex;

public sealed record CodexPaths(string Home, string Sessions, string ArchivedSessions, string Attachments)
{
    public static CodexPaths Resolve(string? configuredHome)
    {
        var homeInput = configuredHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? GetDefaultHome();
        if (string.IsNullOrWhiteSpace(homeInput)) throw new ArgumentException("A Codex home path is required.", nameof(configuredHome));

        var home = Path.GetFullPath(homeInput);
        if (!Directory.Exists(home)) throw new DirectoryNotFoundException("The configured Codex home does not exist.");
        if (IsInsideSyncRepository(home)) throw new ArgumentException("The Codex home cannot be inside the sync repository.", nameof(configuredHome));

        return new CodexPaths(
            home,
            Path.GetFullPath(Path.Combine(home, "sessions")),
            Path.GetFullPath(Path.Combine(home, "archived_sessions")),
            Path.GetFullPath(Path.Combine(home, "attachments")));
    }

    private static string GetDefaultHome()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) throw new InvalidOperationException("The user profile path is unavailable.");
        return Path.Combine(userProfile, ".codex");
    }

    private static bool IsInsideSyncRepository(string home)
    {
        foreach (var startingDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var repository = FindRepositoryRoot(startingDirectory);
            if (repository is not null && IsPathWithin(home, repository)) return true;
        }

        return false;
    }

    private static string? FindRepositoryRoot(string startingDirectory)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(startingDirectory)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
        }

        return null;
    }

    internal static bool IsPathWithin(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;

        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
