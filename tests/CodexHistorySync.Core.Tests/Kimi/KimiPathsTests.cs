using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Tests.Kimi;

public sealed class KimiPathsTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-kimi-paths-{Guid.NewGuid():N}");

    [Fact]
    public void TryResolveReturnsNullWhenHomeIsMissing()
    {
        Assert.Null(KimiPaths.TryResolve(Path.Combine(root, "missing")));
    }

    [Fact]
    public void TryResolveReturnsNullWhenSessionsDirectoryIsMissing()
    {
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);

        Assert.Null(KimiPaths.TryResolve(home));
    }

    [Fact]
    public void TryResolveUsesConfiguredHomeAndRequiresSessions()
    {
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(Path.Combine(home, "sessions"));

        var paths = KimiPaths.TryResolve(home);

        Assert.NotNull(paths);
        Assert.Equal(Path.GetFullPath(home), paths.Home);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, "sessions")), paths.Sessions);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, KimiPaths.IndexFileName)), paths.IndexFilePath);
    }

    [Fact]
    public void ComputeWorkDirKeyMatchesTheVerifiedKimiLayout()
    {
        // Recorded vectors from a real ~/.kimi-code installation: sha256 of the workDir in the
        // C:/... form, first 12 lowercase hex characters, slug from the last path segment.
        Assert.Equal("wd_agent-sync_064d8e34e914", KimiPaths.ComputeWorkDirKey("C:/Repos/agent-sync"));
        Assert.Equal("wd_gamer_04fa2934738f", KimiPaths.ComputeWorkDirKey("C:\\Users\\Gamer"));
    }

    [Fact]
    public void SessionDirectoryRejectsTraversalAndIndexNames()
    {
        using var fixture = new KimiHomeFixture();
        Assert.Throws<ArgumentException>(() => fixture.Paths.SessionDirectory("..", "session_x"));
        Assert.Throws<ArgumentException>(() => fixture.Paths.SessionDirectory("wd_ok_0123456789ab", "session_x/y"));
    }

    [Fact]
    public void IsIndexFileRecognizesTheSharedIndex()
    {
        Assert.True(KimiPaths.IsIndexFile(Path.Combine("home", KimiPaths.IndexFileName)));
        Assert.False(KimiPaths.IsIndexFile(Path.Combine("home", "state.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
