using System.Text.RegularExpressions;

namespace CodexHistorySync.Core.Mimo;

/// <summary>
/// MiMo Code home. Sessions live in a single SQLite database under the XDG data directory
/// (usually <c>~/.local/share/mimocode/mimocode.db</c>, also <c>mimocode-&lt;channel&gt;.db</c>),
/// or an explicit <c>MIMOCODE_HOME/data</c> subtree, or an explicit <c>MIMOCODE_DB</c> file.
/// The anchor directory is local to the sync engine.
/// </summary>
public sealed record MimoPaths(string Home)
{
    public const string AnchorDirectoryName = ".agent-sync-mimo";
    public const string DatabaseBaseName = "mimocode.db";
    public const string DatabasePrefix = "mimocode";

    private static readonly Regex NamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_-]{0,159}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SessionNamePattern = new(
        @"^ses_[A-Za-z0-9_-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string AnchorRoot => Path.GetFullPath(Path.Combine(Home, AnchorDirectoryName));

    public static MimoPaths? TryResolve(string? configuredHome = null)
    {
        try
        {
            string? homeInput;
            if (!string.IsNullOrWhiteSpace(configuredHome))
            {
                homeInput = configuredHome;
            }
            else
            {
                var dbEnv = Environment.GetEnvironmentVariable("MIMOCODE_DB");
                if (!string.IsNullOrWhiteSpace(dbEnv) && !string.Equals(dbEnv, ":memory:", StringComparison.Ordinal))
                {
                    var resolved = TryResolveFromDatabasePath(dbEnv);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        homeInput = resolved;
                    else
                        homeInput = GetDefaultHome();
                }
                else
                {
                    var mimocodeHome = Environment.GetEnvironmentVariable("MIMOCODE_HOME");
                    if (!string.IsNullOrWhiteSpace(mimocodeHome) && Path.IsPathRooted(mimocodeHome))
                        homeInput = TryResolveFromMimocodeHome(mimocodeHome);
                    else
                        homeInput = GetDefaultHome();
                }
            }

            if (string.IsNullOrWhiteSpace(homeInput)) return null;
            var home = Path.GetFullPath(homeInput);
            if (!Directory.Exists(home))
            {
                if (!string.IsNullOrWhiteSpace(configuredHome)) return null;
                var envHome = Environment.GetEnvironmentVariable("MIMOCODE_HOME");
                if (!string.IsNullOrWhiteSpace(envHome)) return new MimoPaths(home);
                return null;
            }
            return new MimoPaths(home);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryResolveFromDatabasePath(string dbValue)
    {
        if (string.IsNullOrWhiteSpace(dbValue)) return null;
        if (string.Equals(dbValue, ":memory:", StringComparison.Ordinal)) return null;
        string? path = null;
        if (Path.IsPathRooted(dbValue))
            path = dbValue;
        else
        {
            // Relative to data directory; resolve via default home candidate.
            var baseDir = GetDefaultHome();
            if (string.IsNullOrWhiteSpace(baseDir)) return null;
            path = Path.Combine(baseDir, dbValue);
        }
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch { return null; }
    }

    private static string? TryResolveFromMimocodeHome(string home)
    {
        if (!Path.IsPathRooted(home)) return null;
        var data = Path.Combine(home, "data");
        return data;
    }

    private static string GetDefaultHome()
    {
        // Mimic resolveMimocodeHome: MIMOCODE_HOME/data else XDG_DATA/mimocode
        var mimocodeHome = Environment.GetEnvironmentVariable("MIMOCODE_HOME");
        if (!string.IsNullOrWhiteSpace(mimocodeHome) && Path.IsPathRooted(mimocodeHome))
            return Path.Combine(mimocodeHome, "data");

        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgData) && Path.IsPathRooted(xdgData))
        {
            var candidate = Path.Combine(xdgData, "mimocode");
            if (Directory.Exists(candidate) || HasAnyDatabase(candidate)) return candidate;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new List<string?>();

        // XDG default (~/.local/share/mimocode) – also used on Windows by MiMo desktop
        if (!string.IsNullOrWhiteSpace(userProfile))
            candidates.Add(Path.Combine(userProfile, ".local", "share", "mimocode"));

        // Windows XDG fallback (%LOCALAPPDATA%\mimocode) – documented in README
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Add(Path.Combine(localAppData, "mimocode"));

        // Another Windows fallback (%APPDATA%\mimocode)
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            candidates.Add(Path.Combine(appData, "mimocode"));

        foreach (var candidate in candidates)
        {
            if (candidate is null) continue;
            if (Directory.Exists(candidate)) return candidate;
            if (HasAnyDatabase(candidate)) return candidate;
        }

        // If none exists, return the most likely candidate so scanner can report uncertain
        // without authorizing tombstones. Prefer the XDG path.
        if (!string.IsNullOrWhiteSpace(userProfile))
            return Path.Combine(userProfile, ".local", "share", "mimocode");
        if (!string.IsNullOrWhiteSpace(localAppData))
            return Path.Combine(localAppData, "mimocode");
        return string.Empty;
    }

    private static bool HasAnyDatabase(string? home) =>
        home is not null && Directory.Exists(home) &&
        Directory.EnumerateFiles(home, "mimocode*.db").Any();

    public static bool IsSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (SessionNamePattern.IsMatch(value)) return true;
        // Allow legacy / generic ids as fallback but still require safe chars
        return NamePattern.IsMatch(value) && !value.Contains('~');
    }

    public static bool IsMimoDatabaseFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (string.Equals(fileName, DatabaseBaseName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = fileName[..^3];
        if (!stem.StartsWith(DatabasePrefix + "-", StringComparison.OrdinalIgnoreCase)) return false;
        var channel = stem[(DatabasePrefix.Length + 1)..];
        if (channel.Length == 0) return false;
        return channel.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    public string GetChannelDatabasePath()
    {
        // Respect MIMOCODE_DB explicit path if set
        var explicitDb = Environment.GetEnvironmentVariable("MIMOCODE_DB");
        if (!string.IsNullOrWhiteSpace(explicitDb) && !string.Equals(explicitDb, ":memory:", StringComparison.Ordinal))
        {
            if (Path.IsPathRooted(explicitDb)) return Path.GetFullPath(explicitDb);
            return Path.GetFullPath(Path.Combine(Home, explicitDb));
        }

        var channel = Environment.GetEnvironmentVariable("MIMOCODE_CHANNEL")
            ?? GetInstallationChannelFallback();
        if (string.IsNullOrWhiteSpace(channel) || new[] { "latest", "beta", "prod" }.Contains(channel, StringComparer.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.Combine(Home, DatabaseBaseName));
        var safe = Regex.Replace(channel, @"[^a-zA-Z0-9._-]", "-");
        return Path.GetFullPath(Path.Combine(Home, $"{DatabasePrefix}-{safe}.db"));
    }

    private static string? GetInstallationChannelFallback()
    {
        // No direct access to InstallationChannel; default to no channel
        return null;
    }

    public IReadOnlyList<string> ListDatabasePaths()
    {
        var explicitDb = Environment.GetEnvironmentVariable("MIMOCODE_DB");
        if (!string.IsNullOrWhiteSpace(explicitDb) && !string.Equals(explicitDb, ":memory:", StringComparison.Ordinal))
        {
            var single = GetChannelDatabasePath();
            return File.Exists(single) ? [single] : [];
        }

        if (!Directory.Exists(Home)) return [];
        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(Home, "mimocode*.db"))
            {
                try
                {
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (!IsMimoDatabaseFile(Path.GetFileName(file))) continue;
                    files.Add(Path.GetFullPath(file));
                }
                catch { continue; }
            }
        }
        catch { }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    public string PrimaryDatabasePath
    {
        get
        {
            var explicitDb = Environment.GetEnvironmentVariable("MIMOCODE_DB");
            if (!string.IsNullOrWhiteSpace(explicitDb) && !string.Equals(explicitDb, ":memory:", StringComparison.Ordinal))
                return GetChannelDatabasePath();
            var list = ListDatabasePaths();
            if (list.Count > 0) return list[0];
            return GetChannelDatabasePath();
        }
    }

    public string AnchorPath(string sessionId)
    {
        if (!IsSessionId(sessionId)) throw new ArgumentException("MiMo session id is invalid.", nameof(sessionId));
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
