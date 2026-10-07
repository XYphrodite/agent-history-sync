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

    /// <summary>
    /// Further Hermes homes discovered beside <see cref="Home"/> on the same machine (the legacy
    /// <c>~/.hermes</c> tree, WSL distribution homes). They are read and synchronized together
    /// with the primary home; imports and conversion writes still land in the primary home.
    /// </summary>
    public IReadOnlyList<HermesPaths> Companions { get; init; } = [];

    /// <summary>The primary home first, then every companion home.</summary>
    public IReadOnlyList<HermesPaths> AllHomes => [this, .. Companions];

    public string AnchorRoot => Path.GetFullPath(Path.Combine(Home, AnchorDirectoryName));

    public static HermesPaths? TryResolve(string? configuredHome = null) =>
        TryResolve(configuredHome ?? Environment.GetEnvironmentVariable("HERMES_HOME"), wslHomeProbe: null, machineDefaults: null);

    /// <summary>Test seam: a null <paramref name="configuredHome"/> means no configuration at all.</summary>
    internal static HermesPaths? TryResolve(
        string? configuredHome,
        Func<IReadOnlyList<string>>? wslHomeProbe,
        IReadOnlyList<string>? machineDefaults)
    {
        try
        {
            var homeInput = configuredHome;
            if (!string.IsNullOrWhiteSpace(homeInput))
            {
                // An explicitly configured home is exclusive: one home means one home.
                var configured = Path.GetFullPath(homeInput);
                return Directory.Exists(configured) ? new HermesPaths(configured) : null;
            }
            return ResolveDiscovered(HermesHomeDiscovery.CandidateHomes(configuredHome: null, wslHomeProbe, machineDefaults));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HermesPaths? ResolveDiscovered(IReadOnlyList<string> candidates)
    {
        var homes = new List<string>();
        var emptyFallback = new List<string>();
        foreach (var candidate in candidates)
        {
            string home;
            try { home = Path.GetFullPath(candidate); }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { continue; }
            if (!Directory.Exists(home)) continue;
            if (HasAnyDatabase(home)) homes.Add(home);
            else emptyFallback.Add(home);
        }

        // A home with no state.db has not been initialized. It still counts as the resolved home
        // when nothing else exists, but it never shadows a home that actually holds sessions.
        if (homes.Count == 0) return emptyFallback.Count == 0 ? null : new HermesPaths(emptyFallback[0]);
        return new HermesPaths(homes[0])
        {
            Companions = homes.Skip(1).Select(home => new HermesPaths(home)).ToArray()
        };
    }

    /// <summary>
    /// Windows installs use <c>%LOCALAPPDATA%\hermes</c>; a <c>~/.hermes</c> tree counts as well
    /// when it is the one that holds <c>state.db</c>. Both are candidate homes; see
    /// <see cref="HermesHomeDiscovery"/>.
    /// </summary>
    private static bool HasDatabase(string? home) =>
        home is not null && File.Exists(Path.Combine(home, DatabaseFileName));

    /// <summary>True when the home holds a root <c>state.db</c> or any <c>profiles/&lt;name&gt;/state.db</c>.</summary>
    public static bool HasAnyDatabase(string home)
    {
        try
        {
            if (HasDatabase(home)) return true;
            var profileRoot = Path.Combine(home, "profiles");
            if (!Directory.Exists(profileRoot)) return false;
            foreach (var directory in Directory.EnumerateDirectories(profileRoot))
                if (File.Exists(Path.Combine(directory, DatabaseFileName))) return true;
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
