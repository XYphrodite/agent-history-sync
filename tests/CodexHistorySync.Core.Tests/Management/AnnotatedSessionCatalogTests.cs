using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class AnnotatedSessionCatalogTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ScanAsync_ReplacesAFallbackTitleWithTheAnnotation()
    {
        var snapshot = await Catalog(
            [Session("claude-one", "what the user asked", ManagedTitleSource.Fallback)],
            (ManagedAgent.Claude, "claude-one", Annotation("QR unlock on the club machines")))
            .ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Claude);
        Assert.Equal("QR unlock on the club machines", session.Title);
        Assert.Equal("What the session did.", session.Annotation?.Description);
    }

    [Fact]
    public async Task ScanAsync_ReplacesABareSessionIdTitleWithTheAnnotation()
    {
        var snapshot = await Catalog(
            [Session("claude-one", "claude-one", ManagedTitleSource.SessionId)],
            (ManagedAgent.Claude, "claude-one", Annotation("QR unlock on the club machines")))
            .ScanAsync(CancellationToken.None);

        Assert.Equal("QR unlock on the club machines", Assert.Single(snapshot.Claude).Title);
    }

    [Fact]
    public async Task ScanAsync_LeavesAnOfficialTitleStandingButStillCarriesTheAnnotation()
    {
        var snapshot = await Catalog(
            [Session("claude-one", "The name Claude gave it", ManagedTitleSource.Official)],
            (ManagedAgent.Claude, "claude-one", Annotation("A title of our own")))
            .ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Claude);
        Assert.Equal("The name Claude gave it", session.Title);
        Assert.Equal("A title of our own", session.Annotation?.Title);
    }

    [Fact]
    public async Task ScanAsync_MatchesAnnotationsByAgentAsWellAsId()
    {
        var catalog = new AnnotatedSessionCatalog(
            new StubCatalog(new SessionCatalogSnapshot(
                [Session("shared-id", "shared-id", ManagedTitleSource.SessionId, ManagedAgent.Codex)],
                [],
                [Session("shared-id", "shared-id", ManagedTitleSource.SessionId)],
                [])),
            new StubStore(new Dictionary<SessionAnnotationKey, SessionAnnotation>
            {
                [new SessionAnnotationKey(ManagedAgent.Claude, "shared-id")] = Annotation("The Claude one")
            }));

        var snapshot = await catalog.ScanAsync(CancellationToken.None);

        Assert.Equal("The Claude one", Assert.Single(snapshot.Claude).Title);
        Assert.Equal("shared-id", Assert.Single(snapshot.Codex).Title);
    }

    [Fact]
    public async Task ScanAsync_IgnoresAnAnnotationWhoseSessionIsGone()
    {
        var snapshot = await Catalog(
            [Session("claude-one", "claude-one", ManagedTitleSource.SessionId)],
            (ManagedAgent.Claude, "deleted-session", Annotation("Named a session that no longer exists")))
            .ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Claude);
        Assert.Equal("claude-one", session.Title);
        Assert.Null(session.Annotation);
    }

    [Fact]
    public async Task ScanAsync_ReturnsTheInnerSnapshotWhenNothingIsAnnotated()
    {
        var inner = new SessionCatalogSnapshot(
            [], [], [Session("claude-one", "claude-one", ManagedTitleSource.SessionId)], []);

        var snapshot = await new AnnotatedSessionCatalog(
            new StubCatalog(inner),
            new StubStore(new Dictionary<SessionAnnotationKey, SessionAnnotation>()))
            .ScanAsync(CancellationToken.None);

        Assert.Same(inner, snapshot);
    }

    [Fact]
    public async Task ScanAsync_KeepsTheSessionsReadableWhenTheStoreCannotBeRead()
    {
        // A damaged sidecar is a lost title, never a lost session list.
        var catalog = new AnnotatedSessionCatalog(
            new StubCatalog(new SessionCatalogSnapshot(
                [], [], [Session("claude-one", "claude-one", ManagedTitleSource.SessionId)], [])),
            new ThrowingStore());

        var snapshot = await catalog.ScanAsync(CancellationToken.None);

        Assert.Equal("claude-one", Assert.Single(snapshot.Claude).Title);
    }

    [Fact]
    public async Task ScanAsync_KeepsTheConfiguredAgentsOfTheInnerSnapshot()
    {
        var inner = new SessionCatalogSnapshot(
            [], [], [Session("claude-one", "claude-one", ManagedTitleSource.SessionId)], [])
        {
            ConfiguredAgents = [ManagedAgent.Claude]
        };

        var snapshot = await new AnnotatedSessionCatalog(
            new StubCatalog(inner),
            new StubStore(new Dictionary<SessionAnnotationKey, SessionAnnotation>
            {
                [new SessionAnnotationKey(ManagedAgent.Claude, "claude-one")] = Annotation("Named")
            }))
            .ScanAsync(CancellationToken.None);

        Assert.Equal([ManagedAgent.Claude], snapshot.ConfiguredAgents);
    }

    [Fact]
    public async Task ScanAsync_ReadsTheStoreOncePerScan()
    {
        var store = new StubStore(new Dictionary<SessionAnnotationKey, SessionAnnotation>
        {
            [new SessionAnnotationKey(ManagedAgent.Claude, "claude-one")] = Annotation("Named")
        });
        var catalog = new AnnotatedSessionCatalog(
            new StubCatalog(new SessionCatalogSnapshot(
                [], [], [Session("claude-one", "claude-one", ManagedTitleSource.SessionId)], [])),
            store);

        await catalog.ScanAsync(CancellationToken.None);

        Assert.Equal(1, store.Loads);
    }

    private static AnnotatedSessionCatalog Catalog(
        ManagedSession[] claude,
        params (ManagedAgent Agent, string SessionId, SessionAnnotation Annotation)[] annotations) =>
        new(
            new StubCatalog(new SessionCatalogSnapshot([], [], claude, [])),
            new StubStore(annotations.ToDictionary(
                entry => new SessionAnnotationKey(entry.Agent, entry.SessionId),
                entry => entry.Annotation)));

    private static ManagedSession Session(
        string sessionId,
        string title,
        ManagedTitleSource titleSource,
        ManagedAgent agent = ManagedAgent.Claude) =>
        new(agent, sessionId, $@"C:\home\{sessionId}.jsonl", title, Moment, false, true, titleSource);

    private static SessionAnnotation Annotation(string title) => new(
        title, "What the session did.", SessionAnnotationSource.Generated, "hash-1", "qwen3:8b", Moment);

    private sealed class StubCatalog(SessionCatalogSnapshot snapshot) : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }

    private sealed class StubStore(IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation> annotations)
        : ISessionAnnotationStore
    {
        public int Loads { get; private set; }

        public Task<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>> LoadAsync(
            CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(annotations);
        }

        public Task SaveAsync(
            SessionAnnotationKey key,
            SessionAnnotation annotation,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(SessionAnnotationKey key, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingStore : ISessionAnnotationStore
    {
        public Task<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>> LoadAsync(
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>>(
                new InvalidDataException("Session annotations are not readable JSON."));

        public Task SaveAsync(
            SessionAnnotationKey key,
            SessionAnnotation annotation,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(SessionAnnotationKey key, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
