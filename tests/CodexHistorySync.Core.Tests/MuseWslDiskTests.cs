using System.Text;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Muse;
using CodexHistorySync.Core.Search;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Core.Tests;

public sealed class MuseWslDiskTests
{
    private const string ParentId = "11111111-1111-4111-8111-111111111111";
    private const string ChildId = "22222222-2222-4222-8222-222222222222";
    private const string Home = "home/linux-user/.local/share/muse";
    private const string Parent = Home + "/sessions/2026/09/25/" + ParentId + "/session.jsonl";
    private const string Child = Home + "/sessions/2026/09/25/" + ParentId + "/subagent/" + ChildId + "/session.jsonl";

    [Fact]
    public async Task CatalogAndFamilyDoNotExtractJournalsAndOpeningExtractsOnlyTheSelectedSession()
    {
        using var fixture = new Fixture();
        fixture.Archive.Files[Parent] = "{\"role\":\"user\",\"text\":\"hello offline\"}\n{\"role\":\"assistant\",\"text\":\"answer\"}";
        fixture.Archive.Files[Child] = "{\"role\":\"user\",\"text\":\"child\"}";
        fixture.Archive.Files[Home + "/sessions/2026/09/25/" + ParentId + "/tool-outputs/" + ChildId + "/session.jsonl"] = "ignored";
        var snapshot = await fixture.Catalog.ScanAsync(CancellationToken.None);
        var session = Assert.Single(snapshot.Muse);
        Assert.True(session.IsReadOnly);
        Assert.False(session.IsActive);
        Assert.Contains("WSL remains stopped", snapshot.LoadingMessage);
        Assert.Empty(fixture.Archive.Extracted);
        var family = await new LocalSessionFamilyReader(null, fixture.Paths).ReadAsync(session, CancellationToken.None);
        var child = Assert.Single(family.Children).Session;
        Assert.True(child.IsReadOnly);
        Assert.Equal(ChildId, child.SessionId);
        Assert.Empty(fixture.Archive.Extracted);
        var conversation = await new SessionContentReader().ReadAsync(session, CancellationToken.None);
        Assert.Equal([ConversationRole.User, ConversationRole.Assistant], conversation.Turns.Select(turn => turn.Role));
        Assert.Equal("hello offline", conversation.Turns[0].Text);
        Assert.Equal([Parent], fixture.Archive.Extracted);
        await fixture.Catalog.ScanAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Archive.Lists);
    }

    [Fact]
    public async Task BackgroundSearchAndMutationsNeverOpenOfflineJournalsOrUncPaths()
    {
        using var fixture = new Fixture();
        fixture.Archive.Files[Parent] = "{\"text\":\"offline\"}";
        var snapshot = await fixture.Catalog.ScanAsync(CancellationToken.None);
        var session = Assert.Single(snapshot.Muse);
        using var search = new SessionSearchIndex(fixture.Root.FullName);
        await search.EnsureCurrentAsync(snapshot, new SessionContentReader(), CancellationToken.None);
        var operations = new LocalSessionOperations(null, null, new NoActivity(), new NoDelete(), null, null);
        Assert.Empty(operations.AvailableCopyTargets(session));
        await Assert.ThrowsAsync<ManagedSessionOperationException>(() => operations.DeleteAsync(session, CancellationToken.None));
        await Assert.ThrowsAsync<ManagedSessionOperationException>(() => operations.CopyAsync(session, ManagedAgent.Codex, CancellationToken.None));
        Assert.Empty(fixture.Archive.Extracted);
    }

    [Fact]
    public async Task AlteredSessionIdentityIsRejectedBeforeCreatingTemporaryFiles()
    {
        using var fixture = new Fixture();
        fixture.Archive.Files[Parent] = "{\"text\":\"offline\"}";
        var session = Assert.Single((await fixture.Catalog.ScanAsync(CancellationToken.None)).Muse);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SessionContentReader().ReadAsync(
            session with { SessionId = "../other" }, CancellationToken.None));
        Assert.Empty(fixture.Archive.Extracted);
    }

    [Fact]
    public async Task OfflinePathsCannotBeUsedForWritingOrSyncScanning()
    {
        using var fixture = new Fixture();
        var conversation = new PortableConversation(ConversationAgent.Muse, ParentId, "title", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [new PortableTurn(ConversationRole.User, "hello")]);
        await Assert.ThrowsAsync<IOException>(() => new MuseConversationWriter(fixture.Paths).WriteAsync(conversation, CancellationToken.None));
        var scan = await new MuseSessionScanner().ScanDetailedAsync(fixture.Paths, CancellationToken.None);
        Assert.Empty(scan.Objects);
        Assert.Contains(CodexHistorySync.Core.Model.ObjectKind.MuseSession, scan.UncertainKinds);
    }

    [Fact]
    public async Task StartedWslOrChangedImageRequiresRefreshInsteadOfReadingStaleOffsets()
    {
        using var fixture = new Fixture();
        fixture.Archive.Files[Parent] = "{\"text\":\"offline\"}";
        var session = Assert.Single((await fixture.Catalog.ScanAsync(CancellationToken.None)).Muse);
        fixture.Stopped = false;
        await Assert.ThrowsAsync<IOException>(() => new SessionContentReader().ReadAsync(session, CancellationToken.None));
        fixture.Stopped = true;
        File.AppendAllText(fixture.Image, "changed");
        await Assert.ThrowsAsync<IOException>(() => new SessionContentReader().ReadAsync(session, CancellationToken.None));
        Assert.Empty(fixture.Archive.Extracted);
    }

    [Fact]
    public async Task CancelledReadAndMissingToolAreReportedWithoutHidingOtherSources()
    {
        using var fixture = new Fixture();
        fixture.Archive.Files[Parent] = "{\"text\":\"offline\"}";
        var session = Assert.Single((await fixture.Catalog.ScanAsync(CancellationToken.None)).Muse);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SessionContentReader().ReadAsync(session, cancellation.Token));
        var disk = new MuseWslDisk("Ubuntu", fixture.Image, null,
            new SevenZipMuseArchive(Path.Combine(fixture.Root.FullName, "missing.exe"), fixture.Image), () => true);
        var paths = new MusePaths("unused", "unused") { OfflineDisks = [disk] };
        var snapshot = await new LocalSessionCatalog(null, null, new NoActivity(), musePaths: paths).ScanAsync(CancellationToken.None);
        Assert.Contains(ManagedAgent.Muse, snapshot.UnavailableAgents);
        Assert.Contains("Install 7-Zip", snapshot.LoadingMessage);
        Assert.Empty(fixture.Archive.Extracted);
    }

    [Theory]
    [InlineData("home/user/.local/share/muse/sessions/11111111-1111-4111-8111-111111111111/session.jsonl", true)]
    [InlineData(Parent, true)]
    [InlineData(Child, true)]
    [InlineData(Home + "/sessions/2026/09/25/" + ParentId + "/tool-outputs/" + ChildId + "/session.jsonl", false)]
    [InlineData(Home + "/sessions/invalid/session.jsonl", false)]
    public void OnlyMainSessionsAndSubagentChainsAreRecognized(string path, bool expected) =>
        Assert.Equal(expected, MuseDiskEntry.TrySession(path, null, out _, out _));

    [Fact]
    public void ListingRejectsTraversalAndSymlinksAndAcceptsNanosecondTimestamps()
    {
        var listing = Listing(Parent, 1) + Listing("../" + Parent, 1) +
            Listing("/" + Parent, 1) + Listing(Child, 1).Replace("Mode = -rw", "Mode = lrw");
        var entry = Assert.Single(MuseDiskEntry.ParseListing(listing));
        Assert.Equal(Parent, entry.Path);
        Assert.Equal(2026, entry.Modified.Year);
    }

    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\gamer\.local\share\muse")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\gamer\.local\share\muse")]
    public void ExplicitWslHomesAreRecognizedWithoutFilesystemAccess(string path)
    {
        Assert.True(MuseWslDiscovery.TryUncHome(path, out var distro, out var home));
        Assert.Equal("Ubuntu", distro);
        Assert.Equal("home/gamer/.local/share/muse", home);
    }

    [Fact]
    public void ExplicitHomeRestrictsDiscovery()
    {
        Assert.False(MuseDiskEntry.TrySession(Parent, "home/other/muse", out _, out _));
        Assert.True(MuseDiskEntry.TrySession("custom/muse/sessions/" + ParentId + "/session.jsonl", "custom/muse", out var home, out _));
        Assert.Equal("custom/muse", home);
        Assert.Throws<ArgumentException>(() => MuseWslDiscovery.TryUncHome(@"\\wsl$\Ubuntu\home\..\etc", out _, out _));
    }

    [Fact]
    public async Task RealVhdxSupportsIndexTitlesConversationAndSubagents()
    {
        var image = Environment.GetEnvironmentVariable("AGENT_SYNC_TEST_MUSE_VHDX");
        if (string.IsNullOrEmpty(image)) return; // The CI workflow supplies a real ext4/VHDX fixture.
        var disk = new MuseWslDisk("OfflineFixture", image, null,
            new SevenZipMuseArchive(MuseWslDiscovery.SevenZipPath(), image), () => true);
        var paths = new MusePaths("unused", "unused") { OfflineDisks = [disk] };
        var catalog = new LocalSessionCatalog(null, null, new NoActivity(), musePaths: paths);
        var session = Assert.Single((await catalog.ScanAsync(CancellationToken.None)).Muse);
        Assert.Equal("Offline fixture title", session.Title);
        Assert.Equal(ManagedTitleSource.Official, session.TitleSource);
        var conversation = await new SessionContentReader().ReadAsync(session, CancellationToken.None);
        Assert.Equal("hello offline", conversation.Turns[0].Text);
        Assert.Equal("offline answer", conversation.Turns[1].Text);
        var family = await new LocalSessionFamilyReader(null, paths).ReadAsync(session, CancellationToken.None);
        var child = Assert.Single(family.Children).Session;
        Assert.Equal("child offline", (await new SessionContentReader().ReadAsync(child, CancellationToken.None)).Turns[0].Text);
    }

    private static string Listing(string path, int size) =>
        $"Path = {path.Replace('/', '\\')}\nFolder = -\nSize = {size}\nMode = -rw-r--r--\nModified = 2026-09-25 12:00:00.123456789\nSymbolic Link = \n\n";

    private sealed class FakeArchive : IMuseArchive
    {
        public Dictionary<string, string> Files { get; } = [];
        public List<string> Extracted { get; } = [];
        public int Lists { get; private set; }
        public Task<string> ListAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Lists++;
            return Task.FromResult(string.Concat(Files.Select(file => Listing(file.Key, Encoding.UTF8.GetByteCount(file.Value)))));
        }
        public async Task ExtractAsync(string entry, Stream destination, long maximumBytes, CancellationToken ct)
        {
            Extracted.Add(entry);
            await destination.WriteAsync(Encoding.UTF8.GetBytes(Files[entry]), ct);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("muse-disk-test-");
        public string Image { get; }
        public FakeArchive Archive { get; } = new();
        public bool Stopped { get; set; } = true;
        public MusePaths Paths { get; }
        public LocalSessionCatalog Catalog { get; }
        public Fixture()
        {
            Image = Path.Combine(Root.FullName, "ext4.vhdx"); File.WriteAllText(Image, "fixture");
            Paths = new MusePaths("unused", "unused") { OfflineDisks = [new MuseWslDisk("Ubuntu", Image, null, Archive, () => Stopped)] };
            Catalog = new LocalSessionCatalog(null, null, new NoActivity(), musePaths: Paths);
        }
        public void Dispose() => Root.Delete(recursive: true);
    }

    private sealed class NoActivity : IManagedSessionActiveState
    {
        public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(ManagedAgent agent, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        public Task<bool> IsActiveAsync(ManagedAgent agent, string id, string path, CancellationToken ct) => throw new InvalidOperationException("Must not inspect offline activity.");
    }
    private sealed class NoDelete : IManagedSessionDirectoryDeleter
    {
        public Task DeleteAsync(string root, string directory, CancellationToken ct) => throw new InvalidOperationException("Must not touch offline paths.");
    }
}
