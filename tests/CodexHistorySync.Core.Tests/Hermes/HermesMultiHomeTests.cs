using CodexHistorySync.Core.Hermes;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Hermes;

public sealed class HermesMultiHomeTests : IDisposable
{
    private const string SessionId = "20250305_091523_a1b2c3d4";
    private const string OtherSessionId = "20250306_101624_b2c3d4e5";
    private readonly string root = Path.Combine(Path.GetTempPath(), "hermes-multihome-" + Guid.NewGuid().ToString("N"));
    private int nextMessageId;

    public HermesMultiHomeTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Discovery_KeepsAnExplicitHomeExclusive()
    {
        var configured = Home("configured").Home;
        var candidates = HermesHomeDiscovery.CandidateHomes(
            configured,
            wslHomeProbe: () => [Home("wsl").Home],
            machineDefaults: [Home("native").Home]);
        Assert.Equal(new[] { configured }, candidates);
    }

    [Fact]
    public void Discovery_MergesMachineDefaultsAndWslHomesInOrder()
    {
        var native = Home("native").Home;
        var legacy = Home("legacy").Home;
        var wsl = Home("wsl").Home;
        var candidates = HermesHomeDiscovery.CandidateHomes(
            configuredHome: null,
            wslHomeProbe: () => [wsl, legacy],
            machineDefaults: [native, legacy]);
        Assert.Equal(new[] { native, legacy, wsl }, candidates);
    }

    [Fact]
    public void Resolve_AutoPicksThePrimaryHomeAndCarriesTheRestAsCompanions()
    {
        var native = Home("native");
        var legacy = Home("legacy");
        var wsl = Home("wsl");
        WriteSession(native, SessionId);
        WriteSession(legacy, SessionId);
        WriteSession(wsl, OtherSessionId);

        var resolved = HermesPaths.TryResolve(
            configuredHome: null,
            wslHomeProbe: () => [wsl.Home],
            machineDefaults: [native.Home, legacy.Home]);
        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(native.Home), resolved.Home);
        Assert.Equal(
            new[] { Path.GetFullPath(legacy.Home), Path.GetFullPath(wsl.Home) },
            resolved.Companions.Select(companion => companion.Home).ToArray());
        Assert.Equal(3, resolved.AllHomes.Count);
    }

    [Fact]
    public void Resolve_AnEmptyHomeIsTheAnswerOnlyWhenNothingElseExists()
    {
        var empty = Home("empty");
        var wsl = Home("wsl");
        WriteSession(wsl, SessionId);

        var withDatabase = HermesPaths.TryResolve(
            configuredHome: null,
            wslHomeProbe: () => [wsl.Home],
            machineDefaults: [empty.Home]);
        Assert.Equal(Path.GetFullPath(wsl.Home), withDatabase!.Home);

        var onlyEmpty = HermesPaths.TryResolve(
            configuredHome: null,
            wslHomeProbe: () => [],
            machineDefaults: [empty.Home]);
        Assert.Equal(Path.GetFullPath(empty.Home), onlyEmpty!.Home);
        Assert.Empty(onlyEmpty.Companions);

        var nothing = HermesPaths.TryResolve(
            configuredHome: null,
            wslHomeProbe: () => [],
            machineDefaults: []);
        Assert.Null(nothing);
    }

    [Fact]
    public async Task Scanner_ReadsEveryHomeAndPrefersThePrimaryCopy()
    {
        var primary = Home("primary");
        var companion = Home("companion");
        WriteSession(primary, SessionId);
        WriteSession(companion, OtherSessionId);
        var scanner = new HermesSessionScanner(_ => Task.CompletedTask, () => false, TimeSpan.Zero);

        var merged = await scanner.ScanDetailedAsync(WithCompanions(primary, companion), CancellationToken.None);
        Assert.Equal(2, merged.Objects.Count);
        Assert.True(merged.IsAbsenceConfirmed(ObjectKind.HermesSession));
        Assert.Empty(merged.DuplicateIds);

        // The same session in both homes is one session; the primary home's copy wins.
        WriteSession(companion, SessionId);
        var shared = await scanner.ScanDetailedAsync(WithCompanions(primary, companion), CancellationToken.None);
        var item = Assert.Single(shared.Objects, item => item.Id.Value == "he-default~" + SessionId);
        Assert.StartsWith(Path.GetFullPath(primary.Home), item.SourcePath, StringComparison.OrdinalIgnoreCase);
        var expected = HermesSessionPackage.HashPackage(
            HermesSessionPackage.TryBuild(primary, HermesPaths.DefaultProfileName, SessionId)!);
        Assert.Equal(expected, item.Hash);
    }

    [Fact]
    public async Task Catalog_MergesHomesAndPrefersThePrimaryRow()
    {
        var primary = Home("primary");
        var companion = Home("companion");
        WriteSession(primary, SessionId, title: "From the primary home");
        WriteSession(companion, OtherSessionId, title: "From the companion home");
        var source = new HermesSessionCatalogSource(WithCompanions(primary, companion));
        using var limiter = new SessionCatalogReadLimiter(1);

        var rows = await source.ScanAsync(limiter, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Title == "From the primary home");
        Assert.Contains(rows, row => row.Title == "From the companion home");

        WriteSession(companion, SessionId, title: "Duplicate copy");
        var merged = await source.ScanAsync(limiter, CancellationToken.None);
        var shared = Assert.Single(merged, row => row.SessionId == SessionId);
        Assert.Equal("From the primary home", shared.Title);
    }

    [Fact]
    public void ShadowCopy_RoundsAWriteBackToTheSource()
    {
        var home = Home("source");
        WriteSession(home, SessionId);
        var database = home.DatabasePath(HermesPaths.DefaultProfileName);

        using (var shadow = HermesDatabaseShadow.Open(database, write: true, forceShadow: true))
        {
            Assert.NotEqual(database, shadow.Database);
            HermesSessionDatabase.Delete(new HermesPaths(Path.GetDirectoryName(shadow.Database)!), HermesPaths.DefaultProfileName, SessionId);
            shadow.CommitBack();
        }

        Assert.Null(HermesSessionDatabase.ReadOne(home, HermesPaths.DefaultProfileName, SessionId));
    }

    [Fact]
    public void ShadowCopy_RefusesToOverwriteASourceThatChanged()
    {
        var home = Home("source");
        WriteSession(home, SessionId);
        var database = home.DatabasePath(HermesPaths.DefaultProfileName);
        var before = File.ReadAllBytes(database);

        using var shadow = HermesDatabaseShadow.Open(database, write: true, forceShadow: true);
        File.WriteAllText(database + "-wal", "frames written while the copy was open");
        Assert.Throws<IOException>(shadow.CommitBack);
        File.Delete(database + "-wal");
        Assert.Equal(before, File.ReadAllBytes(database));
        Assert.NotNull(HermesSessionDatabase.ReadOne(home, HermesPaths.DefaultProfileName, SessionId));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private HermesPaths Home(string name)
    {
        var home = Path.Combine(root, name);
        Directory.CreateDirectory(home);
        return new HermesPaths(home);
    }

    private static HermesPaths WithCompanions(HermesPaths primary, params HermesPaths[] companions) =>
        primary with { Companions = companions };

    private void WriteSession(HermesPaths home, string sessionId, string title = "Fix the build")
    {
        var messageId = ++nextMessageId;
        Directory.CreateDirectory(Path.GetDirectoryName(home.DatabasePath(HermesPaths.DefaultProfileName))!);
        HermesSessionDatabase.Write(home, new HermesSnapshot(
            HermesPaths.DefaultProfileName,
            sessionId,
            [
                new HermesCell("id", "text", sessionId, null, null),
                new HermesCell("source", "text", "cli", null, null),
                new HermesCell("started_at", "real", "1741166123.5", null, null),
                new HermesCell("title", "text", title, null, null)
            ],
            [
                [
                    new HermesCell("id", "integer", null, messageId, null),
                    new HermesCell("session_id", "text", sessionId, null, null),
                    new HermesCell("role", "text", "user", null, null),
                    new HermesCell("content", "text", "hello hermes", null, null),
                    new HermesCell("timestamp", "real", "1741166123.5", null, null)
                ]
            ],
            1741166123.5));
    }
}
