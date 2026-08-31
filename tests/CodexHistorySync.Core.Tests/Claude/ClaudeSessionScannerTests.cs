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
    public async Task ScanDetailedAsyncPublishesOnlyTheLiveCopyOfARelocatedSession()
    {
        // A session whose working directory changes is copied into the new project folder and
        // continued there. Calling that a duplicate id makes the scan fatal, which stops every
        // agent from synchronizing over one ordinary Claude session.
        await using var fixture = new ClaudeHomeFixture();
        var frozen = fixture.WriteSession(Project, StableId);
        var live = fixture.WriteSession("c--Repos-Other", StableId);
        File.SetLastWriteTimeUtc(frozen, DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow - TimeSpan.FromHours(1));
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(live), Assert.Single(result.Objects).SourcePath);
        Assert.Empty(result.DuplicateIds);
        Assert.False(result.HasFatalErrors);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncDefersARelocatedSessionRatherThanPublishingItsFrozenCopy()
    {
        // Choosing the live copy before applying the activity window is what keeps a deferral
        // from silently promoting an older copy of the same session in its place.
        await using var fixture = new ClaudeHomeFixture();
        var frozen = fixture.WriteSession(Project, StableId);
        var live = fixture.WriteSession("c--Repos-Other", StableId);
        File.SetLastWriteTimeUtc(frozen, DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow);
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => true);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.Empty(result.DuplicateIds);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ClaudeSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncBreaksAWriteTimeTieOnTheLongerTranscript()
    {
        // Two machines scanning the same home must choose the same copy, so the tie-break is
        // content, not directory enumeration order.
        await using var fixture = new ClaudeHomeFixture();
        var shorter = fixture.WriteSession(Project, StableId);
        var longer = fixture.WriteSession("c--Repos-Other", StableId);
        await File.AppendAllTextAsync(longer,
            "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\",\"sessionId\":\"" + StableId +
            "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"more\"}]}}\n");
        var when = DateTime.UtcNow - TimeSpan.FromHours(1);
        File.SetLastWriteTimeUtc(shorter, when);
        File.SetLastWriteTimeUtc(longer, when);
        var scanner = new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(longer), Assert.Single(result.Objects).SourcePath);
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
