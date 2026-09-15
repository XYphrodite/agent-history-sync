using CodexHistorySync.Core.Kimi;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Kimi;

public sealed class KimiSessionScannerTests
{
    [Fact]
    public async Task ScanFindsSessionsInEveryWorkDirBucket()
    {
        using var fixture = new KimiHomeFixture();
        var first = fixture.WriteSession();
        var second = fixture.WriteSession(
            KimiHomeFixture.SecondSessionId, workDir: Path.Combine(fixture.Paths.Home, "other"));
        var scanner = new KimiSessionScanner(_ => Task.CompletedTask, isKimiRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.True(result.IsAbsenceConfirmed(ObjectKind.KimiSession));
        Assert.Equal(2, result.Objects.Count);
        Assert.Contains(result.Objects, item =>
            item.Id == new LogicalObjectId(KimiSessionPackage.ToLogicalId(KimiHomeFixture.MainSessionId)) &&
            item.Kind == ObjectKind.KimiSession &&
            item.SourcePath == Path.GetFullPath(KimiSessionPackage.StateFilePath(first)));
        Assert.Contains(result.Objects, item =>
            item.Id == new LogicalObjectId(KimiSessionPackage.ToLogicalId(KimiHomeFixture.SecondSessionId)) &&
            item.SourcePath == Path.GetFullPath(KimiSessionPackage.StateFilePath(second)));
    }

    [Fact]
    public async Task ScanUsesOneStabilityWindowAndRejectsAChangedSession()
    {
        using var fixture = new KimiHomeFixture();
        var stable = fixture.WriteSession();
        var changing = fixture.WriteSession(KimiHomeFixture.SecondSessionId);
        var waits = 0;
        var scanner = new KimiSessionScanner(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            waits++;
            await File.AppendAllTextAsync(
                Path.Combine(changing, "agents", "main", KimiSessionPackage.WireFileName),
                "{\"type\":\"turn.steer\",\"input\":[]}\n",
                cancellationToken);
        }, isKimiRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(1, waits);
        Assert.Single(result.Objects);
        Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(KimiSessionPackage.StateFilePath(stable)));
        Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(KimiSessionPackage.StateFilePath(changing)));
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.KimiSession));
    }

    [Fact]
    public async Task LiveKimiProcessDefersRecentlyWrittenSessionsOnly()
    {
        using var fixture = new KimiHomeFixture();
        var recent = fixture.WriteSession();
        var quiet = fixture.WriteSession(KimiHomeFixture.SecondSessionId);
        // Make the quiet session old: written well outside the activity window.
        var quietState = new FileInfo(KimiSessionPackage.StateFilePath(quiet));
        quietState.LastWriteTimeUtc = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        var quietWire = new FileInfo(Path.Combine(quiet, "agents", "main", KimiSessionPackage.WireFileName));
        quietWire.LastWriteTimeUtc = DateTime.UtcNow - TimeSpan.FromMinutes(10);

        var now = DateTimeOffset.UtcNow;
        var scanner = new KimiSessionScanner(
            _ => Task.CompletedTask,
            isKimiRunning: () => true,
            activityWindow: TimeSpan.FromSeconds(30),
            now: () => now);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Single(result.Objects);
        Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(KimiSessionPackage.StateFilePath(quiet)));
        Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(KimiSessionPackage.StateFilePath(recent)));
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.KimiSession));
    }

    [Fact]
    public async Task SessionsWithoutSynchronizableContentAreDeferredNotPublished()
    {
        using var fixture = new KimiHomeFixture();
        var live = fixture.WriteSession();
        var stateOnly = fixture.WriteSession(KimiHomeFixture.SecondSessionId);
        File.Delete(Path.Combine(stateOnly, "agents", "main", KimiSessionPackage.WireFileName));
        var scanner = new KimiSessionScanner(_ => Task.CompletedTask, isKimiRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Single(result.Objects);
        Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(KimiSessionPackage.StateFilePath(live)));
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.KimiSession));
    }

    [Fact]
    public async Task DuplicateSessionIdsAcrossBucketsAreRejected()
    {
        using var fixture = new KimiHomeFixture();
        fixture.WriteSession();
        fixture.WriteSession(KimiHomeFixture.MainSessionId, workDir: Path.Combine(fixture.Paths.Home, "other"));
        var scanner = new KimiSessionScanner(_ => Task.CompletedTask, isKimiRunning: () => false);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.True(result.HasFatalErrors);
        Assert.Empty(result.Objects);
    }

    [Fact]
    public async Task MissingSessionsRootMakesTheKindUncertain()
    {
        using var fixture = new KimiHomeFixture();
        var missing = new KimiPaths(
            Path.Combine(fixture.Paths.Home, "not-there"),
            Path.Combine(fixture.Paths.Home, "not-there", "sessions"));
        var scanner = new KimiSessionScanner(_ => Task.CompletedTask, isKimiRunning: () => false);

        var result = await scanner.ScanDetailedAsync(missing, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.KimiSession));
    }
}
