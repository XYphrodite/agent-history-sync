namespace CodexHistorySync.Core.Antigravity;

/// <summary>
/// Google Antigravity (AGY) home. Session summaries live in <c>conversation_summaries.db</c>,
/// per-session step databases in <c>conversations/&lt;id&gt;.db</c>, optional title annotations
/// in <c>annotations/&lt;id&gt;.pbtxt</c>, and brain artifacts/transcripts in <c>brain/&lt;id&gt;/</c>.
/// The anchor directory is local to the sync engine.
/// </summary>
public sealed record AntigravityPaths(string Home)
{
    public const string AnchorDirectoryName = ".agent-sync-antigravity";
    public const string SummariesDatabaseName = "conversation_summaries.db";

    public string AnchorRoot => Path.GetFullPath(Path.Combine(Home, AnchorDirectoryName));

    public string ConversationSummariesDbPath => Path.GetFullPath(Path.Combine(Home, SummariesDatabaseName));

    public string SummariesDatabasePath => ConversationSummariesDbPath;

    public string ConversationsDirectory => Path.GetFullPath(Path.Combine(Home, "conversations"));

    public string AnnotationsDirectory => Path.GetFullPath(Path.Combine(Home, "annotations"));

    public string BrainDirectory => Path.GetFullPath(Path.Combine(Home, "brain"));

    public static AntigravityPaths? TryResolve(string? configuredHome = null) =>
        TryResolve(configuredHome, userProfileOverride: null);

    internal static AntigravityPaths? TryResolve(string? configuredHome, string? userProfileOverride)
    {
        try
        {
            var homeInput = configuredHome
                ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME")
                ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_CLI_HOME")
                ?? Environment.GetEnvironmentVariable("ANTIGRAVITY_APP_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(homeInput))
            {
                if (!Path.IsPathRooted(homeInput)) return null;
                var configured = Path.GetFullPath(homeInput);
                return Directory.Exists(configured) ? new AntigravityPaths(configured) : null;
            }

            return ResolveDiscovered(userProfileOverride);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static AntigravityPaths? ResolveDiscovered(string? userProfileOverride = null)
    {
        var userProfile = userProfileOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) return null;

        var candidates = new[]
        {
            Path.Combine(userProfile, ".gemini", "antigravity-cli"),
            Path.Combine(userProfile, ".gemini", "antigravity")
        };

        string? emptyFallback = null;
        foreach (var candidate in candidates)
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { continue; }
            if (!Directory.Exists(full)) continue;
            if (File.Exists(Path.Combine(full, SummariesDatabaseName)))
                return new AntigravityPaths(full);
            emptyFallback ??= full;
        }

        return emptyFallback is null ? null : new AntigravityPaths(emptyFallback);
    }

    public static bool IsSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains('~') || value.Contains('/') || value.Contains('\\') || value.Contains('{') || value.Contains('(')) return false;
        return Guid.TryParse(value, out var guid) && guid != Guid.Empty;
    }

    public string ConversationDatabasePath(string sessionId)
    {
        if (!IsSessionId(sessionId)) throw new ArgumentException("Antigravity session id is invalid.", nameof(sessionId));
        return Path.GetFullPath(Path.Combine(ConversationsDirectory, sessionId + ".db"));
    }

    public string AnnotationPath(string sessionId)
    {
        if (!IsSessionId(sessionId)) throw new ArgumentException("Antigravity session id is invalid.", nameof(sessionId));
        return Path.GetFullPath(Path.Combine(AnnotationsDirectory, sessionId + ".pbtxt"));
    }

    public string BrainSessionDirectory(string sessionId)
    {
        if (!IsSessionId(sessionId)) throw new ArgumentException("Antigravity session id is invalid.", nameof(sessionId));
        return Path.GetFullPath(Path.Combine(BrainDirectory, sessionId));
    }

    public string TranscriptPath(string sessionId)
    {
        if (!IsSessionId(sessionId)) throw new ArgumentException("Antigravity session id is invalid.", nameof(sessionId));
        return Path.GetFullPath(Path.Combine(BrainSessionDirectory(sessionId), "transcript.jsonl"));
    }

    public string AnchorPath(string sessionId)
    {
        if (!IsSessionId(sessionId)) throw new ArgumentException("Antigravity session id is invalid.", nameof(sessionId));
        return Path.GetFullPath(Path.Combine(AnchorRoot, sessionId + ".json"));
    }

    public static bool TryParseAnchor(string candidate, out string home, out string sessionId)
    {
        home = string.Empty;
        sessionId = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(full), ".json")) return false;
            var id = Path.GetFileNameWithoutExtension(full);
            var anchorRoot = Path.GetDirectoryName(full);
            var resolvedHome = anchorRoot is null ? null : Path.GetDirectoryName(anchorRoot);
            if (anchorRoot is null || resolvedHome is null) return false;
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(anchorRoot), AnchorDirectoryName)) return false;
            if (!IsSessionId(id)) return false;
            home = Path.GetFullPath(resolvedHome);
            sessionId = id;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
