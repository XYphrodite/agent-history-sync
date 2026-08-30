using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Update;

namespace CodexHistorySync.Core.Tests.Update;

public sealed class SelfUpdateServiceTests
{
    private static readonly ReleaseVersion InstalledVersion = new(0, 7, 0);

    [Fact]
    public async Task ThePublishedReleaseIsNotDownloadedWhenItIsNotNewer()
    {
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.7.0");

        var report = await fixture.Service(source).UpdateAsync(new SelfUpdateRequest(), CancellationToken.None);

        Assert.Equal(SelfUpdateStatus.AlreadyCurrent, report.Status);
        Assert.Equal(0, source.Downloads);
        Assert.Equal("installed", fixture.InstalledBody());
    }

    [Fact]
    public async Task AnOlderPublishedReleaseNeverReplacesANewerInstalledOne()
    {
        // A yanked or re-pointed "latest" must not silently walk this machine backwards.
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.6.1");

        var report = await fixture.Service(source).UpdateAsync(new SelfUpdateRequest(), CancellationToken.None);

        Assert.Equal(SelfUpdateStatus.AlreadyCurrent, report.Status);
        Assert.Equal(new ReleaseVersion(0, 6, 1), report.Release);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task CheckReportsTheNewerReleaseWithoutTouchingTheInstalledBinary()
    {
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0");

        var report = await fixture.Service(source)
            .UpdateAsync(new SelfUpdateRequest(CheckOnly: true), CancellationToken.None);

        Assert.Equal(SelfUpdateStatus.UpdateAvailable, report.Status);
        Assert.Equal(InstalledVersion, report.Installed);
        Assert.Equal(new ReleaseVersion(0, 8, 0), report.Release);
        Assert.Equal("v0.8.0", report.Tag);
        Assert.Equal(0, source.Downloads);
        Assert.Equal("installed", fixture.InstalledBody());
    }

    [Fact]
    public async Task ANewerReleaseReplacesTheInstalledBinaryAndKeepsThePreviousOne()
    {
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0", "released");

        var report = await fixture.Service(source).UpdateAsync(new SelfUpdateRequest(), CancellationToken.None);

        Assert.Equal(SelfUpdateStatus.Updated, report.Status);
        Assert.Equal("released", fixture.InstalledBody());
        Assert.Equal("installed", UpdateFixture.Body(Assert.Single(fixture.RetiredCopies())));
        Assert.Empty(fixture.StagingDirectories());
    }

    [Fact]
    public async Task AChecksumMismatchLeavesTheInstalledBinaryInPlace()
    {
        // Without this refusal the checksum asset would be decoration, and any tampered or
        // truncated download would become the executable this machine runs next.
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0", "released");
        source.Checksum = new string('a', 64) + "  agent-sync.exe";

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service(source).UpdateAsync(new SelfUpdateRequest(), CancellationToken.None));

        Assert.Contains("checksum", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("installed", fixture.InstalledBody());
        Assert.Empty(fixture.RetiredCopies());
        Assert.Empty(fixture.StagingDirectories());
    }

    [Fact]
    public async Task ADownloadThatIsNotAWindowsExecutableIsRefused()
    {
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0");
        source.Payload = Encoding.UTF8.GetBytes("<html>rate limited</html>");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service(source).UpdateAsync(new SelfUpdateRequest(), CancellationToken.None));

        Assert.Equal("installed", fixture.InstalledBody());
        Assert.Empty(fixture.RetiredCopies());
    }

    [Fact]
    public async Task AnEmptyDownloadIsRefused()
    {
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0");
        source.Payload = [];

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service(source).UpdateAsync(new SelfUpdateRequest(), CancellationToken.None));

        Assert.Equal("installed", fixture.InstalledBody());
    }

    [Fact]
    public async Task AStagedBinaryThatCannotRunIsNeverInstalled()
    {
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0", "released");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service(source, probe: (_, _) => Task.FromResult(false))
                .UpdateAsync(new SelfUpdateRequest(), CancellationToken.None));

        Assert.Equal("installed", fixture.InstalledBody());
        Assert.Empty(fixture.RetiredCopies());
    }

    [Fact]
    public async Task AnInstalledBinaryThatCannotRunIsRolledBack()
    {
        // The post-install probe is the last line before the user is left without a CLI.
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0", "released");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service(source, probe: (path, _) => Task.FromResult(path != fixture.Installed))
                .UpdateAsync(new SelfUpdateRequest(), CancellationToken.None));

        Assert.Equal("installed", fixture.InstalledBody());
        Assert.Empty(fixture.RetiredCopies());
    }

    [Fact]
    public async Task AProbeThatThrowsAfterTheSwapAlsoRollsBack()
    {
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0", "released");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            fixture.Service(source, probe: (path, _) => path == fixture.Installed
                    ? throw new TimeoutException("probe")
                    : Task.FromResult(true))
                .UpdateAsync(new SelfUpdateRequest(), CancellationToken.None));

        Assert.Equal("installed", fixture.InstalledBody());
        Assert.Empty(fixture.RetiredCopies());
    }

    [Fact]
    public async Task TheReleaseIsRunFromStagingBeforeItIsRunFromTheInstalledPath()
    {
        // Reversing this order is what a single-file host cannot survive: once it has renamed
        // itself away it can no longer load the assemblies that starting a process needs.
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.8.0", "released");
        var probed = new List<(string Path, string Body)>();

        await fixture.Service(source, probe: (path, _) =>
        {
            probed.Add((path, UpdateFixture.Body(path)));
            return Task.FromResult(true);
        }).UpdateAsync(new SelfUpdateRequest(), CancellationToken.None);

        Assert.Equal(2, probed.Count);
        Assert.StartsWith(Path.Combine(fixture.Directory, ".agent-sync-update-"), probed[0].Path, StringComparison.Ordinal);
        Assert.Equal(fixture.Installed, probed[1].Path);
        Assert.All(probed, entry => Assert.Equal("released", entry.Body));
    }

    [Fact]
    public async Task APinnedTagInstallsTheReleaseEvenWhenItIsNotNewer()
    {
        // Pinning is how a bad release is undone, so it is an instruction, not a comparison.
        using var fixture = new UpdateFixture();
        var source = fixture.Source("v0.6.1", "pinned");

        var report = await fixture.Service(source)
            .UpdateAsync(new SelfUpdateRequest(Tag: "0.6.1"), CancellationToken.None);

        Assert.Equal(SelfUpdateStatus.Updated, report.Status);
        Assert.Equal("0.6.1", source.RequestedTag);
        Assert.Equal("pinned", fixture.InstalledBody());
    }

    [Fact]
    public async Task RetiredBinariesFromEarlierUpdatesAreCollectedEvenOnACheck()
    {
        // The copy an update retires is still mapped by that run, so only a later run can
        // remove it; leaving collection to the apply path alone would let them accumulate.
        using var fixture = new UpdateFixture();
        File.WriteAllText(fixture.Installed + ".old-20260101000000000", "previous");

        var report = await fixture.Service(fixture.Source("v0.7.0"))
            .UpdateAsync(new SelfUpdateRequest(CheckOnly: true), CancellationToken.None);

        Assert.Equal(1, report.RetiredCopiesRemoved);
        Assert.Empty(fixture.RetiredCopies());
    }

    private sealed class UpdateFixture : IDisposable
    {
        public UpdateFixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), "agent-sync-update-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Installed = Path.Combine(Directory, "agent-sync.exe");
            File.WriteAllBytes(Installed, Executable("installed"));
        }

        public string Directory { get; }

        public string Installed { get; }

        public FakeReleaseSource Source(string tag, string body = "released") => new(tag, Executable(body));

        public SelfUpdateService Service(FakeReleaseSource source,
            Func<string, CancellationToken, Task<bool>>? probe = null) =>
            new(Installed, InstalledVersion, source, probe: probe);

        public string InstalledBody() => Body(Installed);

        public string[] RetiredCopies() => System.IO.Directory.GetFiles(Directory, "agent-sync.exe.old-*");

        public string[] StagingDirectories() =>
            System.IO.Directory.GetDirectories(Directory, ".agent-sync-update-*");

        /// <summary>The payload behind the two-byte executable header the update insists on.</summary>
        public static string Body(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path))[2..];

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }

        private static byte[] Executable(string body) => Encoding.UTF8.GetBytes("MZ" + body);
    }

    private sealed class FakeReleaseSource : IReleaseSource
    {
        private readonly string tag;

        public FakeReleaseSource(string tag, byte[] payload)
        {
            this.tag = tag;
            Payload = payload;
        }

        public byte[] Payload { get; set; }

        /// <summary>Null means "the checksum the payload actually has".</summary>
        public string? Checksum { get; set; }

        public string? RequestedTag { get; private set; }

        public int Downloads { get; private set; }

        public Task<ReleaseDescriptor> ResolveAsync(string? requested, CancellationToken cancellationToken)
        {
            RequestedTag = requested;
            Assert.True(ReleaseVersion.TryParse(tag, out var version));
            return Task.FromResult(new ReleaseDescriptor(tag, version,
                new Uri("https://github.com/example/releases/download/agent-sync.exe"),
                new Uri("https://github.com/example/releases/download/agent-sync.exe.sha256")));
        }

        public async Task DownloadAsync(Uri address, string destinationPath, CancellationToken cancellationToken)
        {
            Downloads++;
            await File.WriteAllBytesAsync(destinationPath, Payload, cancellationToken);
        }

        public Task<string> ReadTextAsync(Uri address, CancellationToken cancellationToken) =>
            Task.FromResult(Checksum ??
                Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant() + "  agent-sync.exe");
    }
}
