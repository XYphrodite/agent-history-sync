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
    }

    public static string Version { get; }
    public static string Commit { get; }
    public static string Author { get; }
}
