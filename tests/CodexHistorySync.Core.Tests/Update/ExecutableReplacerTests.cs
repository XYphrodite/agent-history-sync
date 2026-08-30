using CodexHistorySync.Core.Update;

namespace CodexHistorySync.Core.Tests.Update;

public sealed class ExecutableReplacerTests
{
    [Fact]
    public void ReplaceInstallsTheStagedBinaryAndRetiresThePreviousOne()
    {
        using var fixture = new ReplacerFixture();

        var retired = new ExecutableReplacer().Replace(fixture.Installed, fixture.Stage("new bytes"));

        Assert.Equal("new bytes", File.ReadAllText(fixture.Installed));
        Assert.Equal("old bytes", File.ReadAllText(retired));
        Assert.StartsWith(fixture.Installed + ".old-", retired, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedInstallPutsThePreviousBinaryBack()
    {
        // Without the rollback the machine is left with no executable at the installed path,
        // which is the one outcome this command must never produce.
        using var fixture = new ReplacerFixture();
        var staged = fixture.Stage("new bytes");
        using var locked = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.ThrowsAny<IOException>(() => new ExecutableReplacer().Replace(fixture.Installed, staged));

        Assert.True(File.Exists(fixture.Installed));
        Assert.Equal("old bytes", File.ReadAllText(fixture.Installed));
        Assert.Empty(fixture.RetiredCopies());
    }

    [Fact]
    public void RestoreOverwritesAFailedInstallWithTheRetiredBinary()
    {
        using var fixture = new ReplacerFixture();
        var replacer = new ExecutableReplacer();
        var retired = replacer.Replace(fixture.Installed, fixture.Stage("new bytes"));

        replacer.Restore(retired, fixture.Installed);

        Assert.Equal("old bytes", File.ReadAllText(fixture.Installed));
        Assert.False(File.Exists(retired));
    }

    [Fact]
    public void RetiredCopiesAreCollectedAndAStillMappedOneIsLeftForALaterRun()
    {
        // The copy retired by an update stays mapped by that very process, so a delete failure
        // has to be tolerated rather than reported as an update failure.
        using var fixture = new ReplacerFixture();
        var collectable = fixture.Installed + ".old-20260101000000000";
        var mapped = fixture.Installed + ".old-20260102000000000";
        File.WriteAllText(collectable, "older");
        File.WriteAllText(mapped, "running");
        using var handle = new FileStream(mapped, FileMode.Open, FileAccess.Read, FileShare.Read);

        var removed = new ExecutableReplacer().RemoveRetiredCopies(fixture.Installed);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(collectable));
        Assert.True(File.Exists(mapped));
    }

    [Fact]
    public void RetiredCopyCollectionIgnoresUnrelatedNeighbours()
    {
        using var fixture = new ReplacerFixture();
        var neighbour = Path.Combine(fixture.Directory, "agent-sync.exe.sha256");
        var unrelated = Path.Combine(fixture.Directory, "other.exe.old-20260101000000000");
        File.WriteAllText(neighbour, "hash");
        File.WriteAllText(unrelated, "other");

        Assert.Equal(0, new ExecutableReplacer().RemoveRetiredCopies(fixture.Installed));

        Assert.True(File.Exists(neighbour));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void RelativePathsAreRefused()
    {
        var replacer = new ExecutableReplacer();

        Assert.Throws<ArgumentException>(() => replacer.Replace("agent-sync.exe", "staged.exe"));
        Assert.Throws<ArgumentException>(() => replacer.RemoveRetiredCopies("agent-sync.exe"));
    }

    private sealed class ReplacerFixture : IDisposable
    {
        public ReplacerFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), "agent-sync-replacer-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Installed = Path.Combine(Directory, "agent-sync.exe");
            File.WriteAllText(Installed, "old bytes");
        }

        public string Directory { get; }
        public string Installed { get; }

        public string Stage(string content)
        {
            var staging = Path.Combine(Directory, "staging");
            System.IO.Directory.CreateDirectory(staging);
            var path = Path.Combine(staging, "agent-sync.exe");
            File.WriteAllText(path, content);
            return path;
        }

        public string[] RetiredCopies() =>
            System.IO.Directory.GetFiles(Directory, "agent-sync.exe.old-*");

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
