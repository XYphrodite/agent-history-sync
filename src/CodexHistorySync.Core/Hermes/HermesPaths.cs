using System.Text.RegularExpressions;

namespace CodexHistorySync.Core.Hermes;

/// <summary>
/// Hermes Agent home. Sessions live in <c>state.db</c> (and one database per named profile),
/// not in a transcript file. <see cref="AnchorRoot"/> is only a path key the sync engine can
/// address; Hermes itself never reads it.
/// </summary>
public sealed record HermesPaths(string Home)
{
    public const string AnchorDirectoryName = ".agent-sync-hermes";
    public const string DefaultProfileName = "default";
    public const string DatabaseFileName = "state.db";

    private static readonly Regex NamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_-]{0,159}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string AnchorRoot => Path.GetFullPath(Path.Combine(Home, AnchorDirectoryName));

    public static HermesPaths? TryResolve(string? configuredHome = null)
    {
        try
        {
            var homeInput = configuredHome
                ?? Environment.GetEnvironmentVariable("HERMES_HOME")
                ?? GetDefaultHome();
            if (string.IsNullOrWhiteSpace(homeInput)) return null;
            var home = Path.GetFullPath(homeInput);
            if (!Directory.Exists(home)) return null;
            return new HermesPaths(home);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows installs use <c>%LOCALAPPDATA%\hermes</c>. A <c>~/.hermes</c> tree is accepted when
    /// that is the one that actually holds <c>state.db</c>, or when it is the only home present.
    /// </summary>
    private static string GetDefaultHome()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var modern = string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "hermes");
        var legacy = string.IsNullOrWhiteSpace(user) ? null : Path.Combine(user, ".hermes");
        if (HasDatabase(modern)) return modern!;
        if (HasDatabase(legacy)) return legacy!;
        if (modern is not null && Directory.Exists(modern)) return modern;
        if (legacy is not null && Directory.Exists(legacy)) return legacy;
        return string.Empty;
    }

    private static bool HasDatabase(string? home) =>
        home is not null && File.Exists(Path.Combine(home, DatabaseFileName));

    public static bool IsProfileName(string value) =>
        string.Equals(value, DefaultProfileName, StringComparison.Ordinal) || NamePattern.IsMatch(value);

    public static bool IsSessionId(string value) => NamePattern.IsMatch(value) && !value.Contains('~');

    public string DatabasePath(string profile)
    {
        if (!IsProfileName(profile)) throw new ArgumentException("Hermes profile name is invalid.", nameof(profile));
        return string.Equals(profile, DefaultProfileName, StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(Home, DatabaseFileName))
            : Path.GetFullPath(Path.Combine(Home, "profiles", profile, DatabaseFileName));
    }

    public string AnchorPath(string profile, string sessionId)
    {
        if (!IsProfileName(profile)) throw new ArgumentException("Hermes profile name is invalid.", nameof(profile));
        if (!IsSessionId(sessionId)) throw new ArgumentException("Hermes session id is invalid.", nameof(sessionId));
        return Path.GetFullPath(Path.Combine(AnchorRoot, profile, sessionId + ".json"));
    }

    /// <summary>Profiles that currently have a <c>state.db</c>. An empty home is not an empty history.</summary>
    public IReadOnlyList<HermesProfile> ListProfiles()
    {
        var profiles = new List<HermesProfile>();
        var rootDatabase = DatabasePath(DefaultProfileName);
        if (File.Exists(rootDatabase)) profiles.Add(new HermesProfile(DefaultProfileName, rootDatabase));

        var profileRoot = Path.Combine(Home, "profiles");
        if (!Directory.Exists(profileRoot)) return profiles;
        foreach (var directory in Directory.EnumerateDirectories(profileRoot))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if (!NamePattern.IsMatch(name) || string.Equals(name, DefaultProfileName, StringComparison.Ordinal)) continue;
            var database = Path.Combine(directory, DatabaseFileName);
            if (File.Exists(database)) profiles.Add(new HermesProfile(name, Path.GetFullPath(database)));
        }

        return profiles;
    }

    public static bool TryParseAnchor(string candidate, out string home, out string profile, out string sessionId)
    {
        home = string.Empty;
        profile = string.Empty;
        sessionId = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(full), ".json")) return false;
            var id = Path.GetFileNameWithoutExtension(full);
            var profileDirectory = Path.GetDirectoryName(full);
            var anchorRoot = profileDirectory is null ? null : Path.GetDirectoryName(profileDirectory);
            var resolvedHome = anchorRoot is null ? null : Path.GetDirectoryName(anchorRoot);
            if (profileDirectory is null || anchorRoot is null || resolvedHome is null) return false;
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(anchorRoot), AnchorDirectoryName)) return false;
            var profileName = Path.GetFileName(profileDirectory);
            if (!IsProfileName(profileName) || !IsSessionId(id)) return false;
            home = Path.GetFullPath(resolvedHome);
            profile = profileName;
            sessionId = id;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

public sealed record HermesProfile(string Name, string DatabasePath);
