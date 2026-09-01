using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Annotations;

public sealed class SessionAnnotationSyncTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToLogicalId_NamesTheAgentAsWellAsTheSession() =>
        Assert.Equal(
            "annotation-claude-18d02148",
            SessionAnnotationPackage.ToLogicalId(new SessionAnnotationKey(ManagedAgent.Claude, "18d02148")));

    [Fact]
    public void ToLogicalId_TellsTwoAgentsHoldingOneSessionIdApart()
    {
        // The same id under two agents is ordinary, and each may be named separately.
        Assert.NotEqual(
            SessionAnnotationPackage.ToLogicalId(new SessionAnnotationKey(ManagedAgent.Claude, "shared")),
            SessionAnnotationPackage.ToLogicalId(new SessionAnnotationKey(ManagedAgent.Codex, "shared")));
    }

    [Theory]
    [InlineData(ManagedAgent.Claude, "18d02148-18bf-4ab7-9ea2-d5d75235b8b7")]
    [InlineData(ManagedAgent.Codex, "codex-session")]
    [InlineData(ManagedAgent.Grok, "grok.session")]
    [InlineData(ManagedAgent.Continue, "continue_session")]
    public void TryParseLogicalId_ReadsBackWhatToLogicalIdWrote(ManagedAgent agent, string sessionId)
    {
        var key = new SessionAnnotationKey(agent, sessionId);

        Assert.True(SessionAnnotationPackage.TryParseLogicalId(SessionAnnotationPackage.ToLogicalId(key), out var read));
        Assert.Equal(key, read);
    }

    [Theory]
    [InlineData("claude-one")]
    [InlineData("annotation-")]
    [InlineData("annotation-zed-one")]
    [InlineData("annotation-claude-")]
    [InlineData("annotation-claude-../escape")]
    [InlineData(null)]
    public void TryParseLogicalId_RefusesAnythingThatIsNotOne(string? value) =>
        Assert.False(SessionAnnotationPackage.TryParseLogicalId(value, out _));

    [Fact]
    public void TryReadPackage_RefusesBytesThatAreNotStrictUtf8() =>
        Assert.False(SessionAnnotationPackage.TryReadPackage([0xC3, 0x28], out _, out _));

    [Fact]
    public void TryReadPackage_ReadsWhatBuildWrote()
    {
        var key = new SessionAnnotationKey(ManagedAgent.Grok, "grok-one");

        Assert.True(SessionAnnotationPackage.TryReadPackage(
            SessionAnnotationPackage.Build(key, Annotation("Named here")), out var read, out var annotation));
        Assert.Equal(key, read);
        Assert.Equal("Named here", annotation.Title);
    }

    [Fact]
    public async Task ScanAsync_PublishesOneObjectPerAnnotatedSession()
    {
        await using var fixture = new ScanFixture();
        await fixture.StoreAsync(ManagedAgent.Claude, "claude-one", "A Claude title");
        await fixture.StoreAsync(ManagedAgent.Codex, "codex-one", "A Codex title");

        var scan = await fixture.ScanAsync();

        Assert.Equal(2, scan.Objects.Count);
        Assert.All(scan.Objects, item => Assert.Equal(ObjectKind.SessionAnnotations, item.Kind));
        Assert.Contains(scan.Objects, item => item.Id.Value == "annotation-claude-claude-one");
        Assert.Contains(scan.Objects, item => item.Id.Value == "annotation-codex-codex-one");
        Assert.False(scan.HasFatalErrors);
    }

    [Fact]
    public async Task ScanAsync_HashesWhatWouldBePublished()
    {
        await using var fixture = new ScanFixture();
        var key = new SessionAnnotationKey(ManagedAgent.Claude, "claude-one");
        await fixture.StoreAsync(key.Agent, key.SessionId, "A title");

        var scanned = Assert.Single((await fixture.ScanAsync()).Objects);

        var expected = SessionAnnotationPackage.HashPackage(
            await File.ReadAllBytesAsync(fixture.Store.PathFor(key)));
        Assert.Equal(expected.Hex, scanned.Hash.Hex);
        Assert.Equal(fixture.Store.PathFor(key), scanned.SourcePath);
    }

    [Fact]
    public async Task ScanAsync_ConfirmsAnAbsenceWhenNothingIsStored()
    {
        await using var fixture = new ScanFixture();

        var scan = await fixture.ScanAsync();

        // Confirmed absence, not uncertainty: an empty directory must not stop other agents from
        // publishing, and it is not a reason to tombstone anything either.
        Assert.Empty(scan.Objects);
        Assert.True(scan.IsAbsenceConfirmed(ObjectKind.SessionAnnotations));
    }

    [Fact]
    public async Task ScanAsync_SkipsAFileThatIsNotAnAnnotation()
    {
        await using var fixture = new ScanFixture();
        await fixture.StoreAsync(ManagedAgent.Claude, "claude-one", "Intact");
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "claude-broken.json"), "{ not json");

        Assert.Single((await fixture.ScanAsync()).Objects);
    }

    [Fact]
    public async Task ScanAsync_SkipsAFileNamedForASessionOtherThanTheOneInside()
    {
        // Publishing it would put one session's title under another session's logical id.
        await using var fixture = new ScanFixture();
        var key = new SessionAnnotationKey(ManagedAgent.Claude, "claude-one");
        await fixture.StoreAsync(key.Agent, key.SessionId, "A title");
        File.Move(fixture.Store.PathFor(key), Path.Combine(fixture.Directory, "claude-another.json"));

        Assert.Empty((await fixture.ScanAsync()).Objects);
    }

    private static SessionAnnotation Annotation(string title) => new(
        title, "What the session did.", SessionAnnotationSource.Generated, "hash-1", "qwen3:8b", Moment);

    private sealed class ScanFixture : IAsyncDisposable
    {
        public ScanFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"chs-annotation-scan-{Guid.NewGuid():N}");
            Store = new SessionAnnotationStore(Root);
        }

        public string Root { get; }

        public SessionAnnotationStore Store { get; }

        public string Directory => Store.Directory;

        public Task StoreAsync(ManagedAgent agent, string sessionId, string title) =>
            Store.SaveAsync(new SessionAnnotationKey(agent, sessionId), Annotation(title), CancellationToken.None);

        public Task<SessionScanResult> ScanAsync() =>
            new SessionAnnotationScanner().ScanDetailedAsync(Directory, CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            try
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
