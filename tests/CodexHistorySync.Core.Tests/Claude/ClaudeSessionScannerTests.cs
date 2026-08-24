using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Claude;

public sealed class ClaudeSessionScannerTests
{
    private const string StableId = "10000000-0000-0000-0000-000000000001";
    private const string ChangingId = "20000000-0000-0000-0000-000000000002";
    private const string Project = "c--Repos-Demo";

    [Fact]
    public async Task ScanDetailedAsyncUsesOneStabilityWindowAndRejectsAChangedCandidate()
    {
        // One shared wait keeps startup flat; a per-candidate wait would make it linear.
        await using var fixture = new ClaudeHomeFixture();
        var stable = fixture.WriteSession(Project, StableId);
        var changing = fixture.WriteSession(Project, ChangingId);
        var waits = 0;
        var scanner = new ClaudeSessionScanner(
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                waits++;
                await File.AppendAllTextAsync(changing, "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}\n", cancellationToken);
            },
            isClaudeRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(1, waits);
        Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(stable));
        Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(changing));
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncDefersRecentTranscriptsWhileClaudeIsRunning()
    {
        await using var fixture = new ClaudeHomeFixture();
        fixture.WriteSession(Project, StableId);
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => true);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncPublishesIdleTranscriptsWhileClaudeIsRunning()
    {
        await using var fixture = new ClaudeHomeFixture();
        var idle = fixture.WriteSession(Project, StableId);
        File.SetLastWriteTimeUtc(idle, DateTime.UtcNow - TimeSpan.FromHours(1));
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => true);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(idle));
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncMarksDuplicateIdsAcrossProjectDirectories()
    {
        await using var fixture = new ClaudeHomeFixture();
        fixture.WriteSession(Project, StableId);
        fixture.WriteSession("c--Repos-Other", StableId);
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.Contains(new LogicalObjectId(ClaudeSessionPackage.ToLogicalId(StableId)), result.DuplicateIds);
        Assert.True(result.HasFatalErrors);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncReportsUncertainWhenProjectsRootIsMissing()
    {
        await using var fixture = new ClaudeHomeFixture();
        var paths = new ClaudePaths(fixture.Paths.Home, Path.Combine(fixture.Paths.Home, "absent"));
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => false);

        var result = await scanner.ScanDetailedAsync(paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncDefersAnUnreadableTranscript()
    {
        await using var fixture = new ClaudeHomeFixture();
        var stable = fixture.WriteSession(Project, StableId);
        var locked = fixture.WriteSession(Project, ChangingId);
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => false);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

            Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(stable));
            Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(locked));
            Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
        }
    }

    [Fact]
    public async Task ScanDetailedAsyncIgnoresTranscriptsBelowTheProjectLevel()
    {
        await using var fixture = new ClaudeHomeFixture();
        var nested = Path.Combine(fixture.Paths.Projects, Project, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, StableId + ".jsonl"),
            "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\",\"sessionId\":\"" + StableId + "\"}\n");
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
    }

    private sealed class ClaudeHomeFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), $"codex-history-sync-claude-scanner-{Guid.NewGuid():N}");

        public ClaudeHomeFixture()
        {
            var home = Path.Combine(root, ".claude");
            var projects = Path.Combine(home, "projects");
            Directory.CreateDirectory(projects);
            Paths = new ClaudePaths(home, projects);
        }

        public ClaudePaths Paths { get; }

        public string WriteSession(string project, string sessionId)
        {
            var directory = Path.Combine(Paths.Projects, project);
            Directory.CreateDirectory(directory);
            var transcript = Path.Combine(directory, sessionId + ".jsonl");
            File.WriteAllText(transcript,
                "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\",\"sessionId\":\"" + sessionId +
                "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"question\"}]}}\n");
            return transcript;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }
}
