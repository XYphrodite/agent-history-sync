using System.Reflection;
using CodexHistorySync.Core.Update;

namespace CodexHistorySync.Cli;

/// <summary>
/// The version of the binary that is running, taken from the assembly the build stamped rather
/// than from a constant that can drift away from <c>Directory.Build.props</c>.
/// </summary>
internal static class CliVersion
{
    public static ReleaseVersion Current { get; } = Resolve();

    private static ReleaseVersion Resolve()
    {
        var version = typeof(CliVersion).Assembly.GetName().Version;
        return version is not null
            ? new ReleaseVersion(version.Major, version.Minor, version.Build)
            : new ReleaseVersion(0, 0, 0);
    }
}
