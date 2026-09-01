using System.Reflection;

namespace CodexHistorySync.Cli;

/// <summary>
/// What this build is, read from the assembly the compiler stamped rather than from constants
/// that drift. The commit is the revision the build system appends to the informational version
/// as <c>0.9.0+&lt;sha&gt;</c>; it is shortened the way git does, so it can be pasted straight
/// into a `git show`.
/// </summary>
internal static class CliBuildInfo
{
    static CliBuildInfo()
    {
        var assembly = typeof(CliBuildInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var parts = (informationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "unknown")
            .Split('+', 2);
        var revision = parts.Length == 2 ? parts[1] : string.Empty;
        var commit = revision.Length > 7 ? revision[..7] : revision;
        var author = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;

        Version = parts[0];
        Commit = string.IsNullOrWhiteSpace(commit) ? "unknown" : commit;
        Author = string.IsNullOrWhiteSpace(author) ? "unknown" : author;
        // Assigned here rather than as an initializer: those run before this body, and would
        // have measured three nulls.
        PanelWidth = PanelWidthFor(Version, Commit, Author);
    }

    public static string Version { get; }
    public static string Commit { get; }
    public static string Author { get; }

    /// <summary>
    /// How wide the brand panel has to be to hold its detail line. It was written as 49, which
    /// held until a version number grew a character: at 0.10.10 the line wrapped and pushed the
    /// author onto a second row that the panel had no room for. Measured, it cannot drift again.
    /// The floor keeps the panel the size it has always been for shorter builds.
    /// </summary>
    public static int PanelWidth { get; }

    internal static int PanelWidthFor(string version, string commit, string author) =>
        // One space of padding each side, one border character each side.
        Math.Max(49, $"version {version}  commit {commit}  by {author}".Length + 4);
}
