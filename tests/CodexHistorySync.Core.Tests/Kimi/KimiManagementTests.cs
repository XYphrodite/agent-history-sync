using CodexHistorySync.Core.Kimi;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Kimi;

public sealed class KimiManagementTests
{
    [Fact]
    public async Task ScanListsSessionsWithOfficialTitlesFromStateJson()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession(title: "official kimi title");
        var catalog = new LocalSessionCatalog(null, null, new NeverActiveState(), kimiPaths: fixture.Paths);

        var snapshot = await catalog.ScanAsync(CancellationToken.None);

        Assert.Contains(ManagedAgent.Kimi, snapshot.ConfiguredAgents);
        var row = Assert.Single(snapshot.Kimi);
        Assert.Equal(KimiHomeFixture.MainSessionId, row.SessionId);
        Assert.Equal(Path.GetFullPath(sessionDirectory), row.NativePath);
        Assert.Equal("official kimi title", row.Title);
        Assert.Equal(ManagedTitleSource.Official, row.TitleSource);
        Assert.False(row.IsActive);
        Assert.True(row.CanRead);
    }

    [Fact]
    public async Task ScanFallsBackToTheFirstUserTurnWhenStateHasNoTitle()
    {
        using var fixture = new KimiHomeFixture();
        fixture.WriteSession(title: " ");
        var statePath = Path.Combine(
            fixture.Paths.SessionDirectory(
                KimiPaths.ComputeWorkDirKey(fixture.WorkDir),
                KimiPaths.SessionIdPrefix + KimiHomeFixture.MainSessionId),
            KimiSessionPackage.StateFileName);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!;
        state["title"] = "";
        File.WriteAllText(statePath, state.ToJsonString());

        var catalog = new LocalSessionCatalog(null, null, new NeverActiveState(), kimiPaths: fixture.Paths);
        var snapshot = await catalog.ScanAsync(CancellationToken.None);

        var row = Assert.Single(snapshot.Kimi);
        Assert.Equal("synthetic question", row.Title);
        Assert.Equal(ManagedTitleSource.Fallback, row.TitleSource);
    }

    [Fact]
    public async Task ScanMarksASessionUnreadableWhenStateDoesNotMatchTheDirectory()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession();
        File.WriteAllText(Path.Combine(sessionDirectory, KimiSessionPackage.StateFileName), """
            {"id":"session_99999999-0000-0000-0000-000000000009","version":2,"cwd":"C:/x"}
            """);

        var catalog = new LocalSessionCatalog(null, null, new NeverActiveState(), kimiPaths: fixture.Paths);
        var snapshot = await catalog.ScanAsync(CancellationToken.None);

        var row = Assert.Single(snapshot.Kimi);
        Assert.False(row.CanRead);
    }

    [Fact]
    public async Task ScanIgnoresRuntimeOnlyDirectoriesInsideSessions()
    {
        using var fixture = new KimiHomeFixture();
        var sessionDirectory = fixture.WriteSession();
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "tasks", "session_10000000-0000-0000-0000-000000000001"));
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "logs"));

        var catalog = new LocalSessionCatalog(null, null, new NeverActiveState(), kimiPaths: fixture.Paths);
        var snapshot = await catalog.ScanAsync(CancellationToken.None);

        Assert.Single(snapshot.Kimi);
    }

    private sealed class NeverActiveState : IManagedSessionActiveState
    {
        public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(
            ManagedAgent agent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<bool> IsActiveAsync(
            ManagedAgent agent, string sessionId, string nativePath, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
