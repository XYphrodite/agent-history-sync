using System.Text;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Claude;

public sealed class ClaudeMemoryScannerTests
{
    private const string Project = "c--Repos-Demo";
    private const string OtherProject = "c--Repos-Other";

    [Fact]
    public async Task ScanDetailedAsyncPublishesEachMarkdownFileAsItsOwnObject()
    {
        await using var fixture = new MemoryHomeFixture();
        var machines = fixture.WriteMemory(Project, "tailnet-machines", "# machines\n");
        fixture.WriteMemory(Project, "MEMORY", "- [machines](tailnet-machines.md)\n");

        var result = await new ClaudeMemoryScanner().ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(2, result.Objects.Count);
        Assert.All(result.Objects, item => Assert.Equal(ObjectKind.ClaudeMemory, item.Kind));
        Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(machines));
        Assert.Contains(result.Objects, item => item.Id.Value ==
            ClaudeMemoryPackage.ToLogicalId(Project, "MEMORY"));
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.ClaudeMemory));
    }

    [Fact]
    public async Task ScanDetailedAsyncHashesThePackageNotTheRawFile()
    {
        await using var fixture = new MemoryHomeFixture();
        var file = fixture.WriteMemory(Project, "tailnet-machines", "one\r\ntwo\n");

        var scanned = Assert.Single((await new ClaudeMemoryScanner().ScanDetailedAsync(fixture.Paths, CancellationToken.None)).Objects);

        var package = ClaudeMemoryPackage.BuildFromFile(file);
        Assert.Equal(ClaudeMemoryPackage.HashPackage(package).Hex, scanned.Hash.Hex);
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(),
            scanned.Hash.Hex);
    }

    [Fact]
    public async Task ScanDetailedAsyncIgnoresTranscriptsAndNestedMarkdown()
    {
        await using var fixture = new MemoryHomeFixture();
        fixture.WriteMemory(Project, "tailnet-machines", "# machines\n");
        var nested = Path.Combine(fixture.Paths.Projects, Project, "memory", "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "hidden.md"), "# no\n");
        File.WriteAllText(Path.Combine(fixture.Paths.Projects, Project, "10000000-0000-0000-0000-000000000001.jsonl"),
            "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}\n");

        var result = await new ClaudeMemoryScanner().ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        var scanned = Assert.Single(result.Objects);
        Assert.True(ClaudeMemoryPackage.TryParseLogicalId(scanned.Id.Value, out _, out var name));
        Assert.Equal("tailnet-machines", name);
    }

    [Fact]
    public async Task ScanDetailedAsyncGivesTheSameNameInTwoProjectsDifferentIds()
    {
        await using var fixture = new MemoryHomeFixture();
        fixture.WriteMemory(Project, "shared", "# one\n");
        fixture.WriteMemory(OtherProject, "shared", "# two\n");

        var result = await new ClaudeMemoryScanner().ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(2, result.Objects.Count);
        Assert.Empty(result.DuplicateIds);
        Assert.NotEqual(result.Objects[0].Id, result.Objects[1].Id);
    }

    [Fact]
    public async Task ScanDetailedAsyncConfirmsAbsenceWhenNoProjectHasAMemoryDirectory()
    {
        await using var fixture = new MemoryHomeFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Paths.Projects, Project));

        var result = await new ClaudeMemoryScanner().ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.ClaudeMemory));
    }

    [Fact]
    public async Task ScanDetailedAsyncReportsUncertainWhenProjectsRootIsMissing()
    {
        await using var fixture = new MemoryHomeFixture();
        var paths = new ClaudePaths(fixture.Paths.Home, Path.Combine(fixture.Paths.Home, "absent"));

        var result = await new ClaudeMemoryScanner().ScanDetailedAsync(paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeMemory));
    }

    [Fact]
    public async Task ScanDetailedAsyncDefersAnUnreadableFileRatherThanPublishingADeletion()
    {
        await using var fixture = new MemoryHomeFixture();
        var stable = fixture.WriteMemory(Project, "stable", "# ok\n");
        var locked = fixture.WriteMemory(Project, "locked", "# busy\n");
        var scanner = new ClaudeMemoryScanner();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

            Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(stable));
            Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(locked));
            Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeMemory));
        }
    }

    [Fact]
    public async Task ScanDetailedAsyncSkipsAnEmptyFileWithoutConfirmingItsAbsence()
    {
        await using var fixture = new MemoryHomeFixture();
        fixture.WriteMemory(Project, "empty", "\n");

        var result = await new ClaudeMemoryScanner().ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeMemory));
    }

    private sealed class MemoryHomeFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), $"codex-history-sync-claude-memory-{Guid.NewGuid():N}");

        public MemoryHomeFixture()
        {
            var home = Path.Combine(root, ".claude");
            var projects = Path.Combine(home, "projects");
            Directory.CreateDirectory(projects);
            Paths = new ClaudePaths(home, projects);
        }

        public ClaudePaths Paths { get; }

        public string WriteMemory(string project, string name, string body)
        {
            var directory = Path.Combine(Paths.Projects, project, "memory");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, name + ".md");
            File.WriteAllText(file, body, new UTF8Encoding(false));
            return file;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }
}
