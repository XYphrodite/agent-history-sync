using System.Text;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Annotations;

public sealed class SessionAnnotationStoreTests
{
    private static readonly DateTimeOffset Moment = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LoadAsync_ReturnsNothingWhenNothingHasBeenStored()
    {
        await using var fixture = new AnnotationFixture();

        Assert.Empty(await fixture.CreateStore().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAnAnnotation()
    {
        await using var fixture = new AnnotationFixture();
        var key = new SessionAnnotationKey(ManagedAgent.Claude, "claude-one");

        await fixture.CreateStore().SaveAsync(key, Annotation("Session titles in agent-sync"), CancellationToken.None);

        // A second store instance proves the value survived the file, not just the object.
        var annotation = Assert.Contains(key, await fixture.CreateStore().LoadAsync(CancellationToken.None));
        Assert.Equal("Session titles in agent-sync", annotation.Title);
        Assert.Equal("What the session did.", annotation.Description);
        Assert.Equal(SessionAnnotationSource.Generated, annotation.Source);
        Assert.Equal("hash-1", annotation.DigestHash);
        Assert.Equal("qwen3:8b", annotation.Model);
        Assert.Equal(Moment, annotation.UpdatedAt);
    }

    [Fact]
    public async Task SaveAsync_GivesEverySessionAFileOfItsOwn()
    {
        await using var fixture = new AnnotationFixture();
        var store = fixture.CreateStore();

        await store.SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Claude, "shared-id"), Annotation("Claude"), CancellationToken.None);
        await store.SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Codex, "shared-id"), Annotation("Codex"), CancellationToken.None);

        var annotations = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(2, annotations.Count);
        Assert.Equal("Claude", annotations[new SessionAnnotationKey(ManagedAgent.Claude, "shared-id")].Title);
        Assert.Equal("Codex", annotations[new SessionAnnotationKey(ManagedAgent.Codex, "shared-id")].Title);
        Assert.Equal(2, Directory.GetFiles(fixture.AnnotationDirectory).Length);
    }

    [Fact]
    public async Task SaveAsync_ReplacesTheAnnotationOfTheSameSession()
    {
        await using var fixture = new AnnotationFixture();
        var store = fixture.CreateStore();
        var key = new SessionAnnotationKey(ManagedAgent.Claude, "claude-one");
        await store.SaveAsync(key, Annotation("First"), CancellationToken.None);

        await store.SaveAsync(
            key, Annotation("Second") with { Source = SessionAnnotationSource.Edited }, CancellationToken.None);

        var annotations = await store.LoadAsync(CancellationToken.None);
        var annotation = Assert.Contains(key, annotations);
        Assert.Single(annotations);
        Assert.Equal("Second", annotation.Title);
        Assert.Equal(SessionAnnotationSource.Edited, annotation.Source);
    }

    [Fact]
    public async Task LoadAsync_SkipsAnAgentThisBuildDoesNotKnowAndKeepsTheRest()
    {
        await using var fixture = new AnnotationFixture();
        // A newer build may store an agent this one has never heard of. One unreadable file must
        // not cost the user every other title beside it.
        await fixture.WriteRawAsync("zed-zed-one.json", Document(agent: "Zed", sessionId: "zed-one"));
        await fixture.WriteRawAsync("claude-claude-one.json", Document(sessionId: "claude-one", title: "Known"));

        var annotations = await fixture.CreateStore().LoadAsync(CancellationToken.None);

        var annotation = Assert.Single(annotations);
        Assert.Equal(new SessionAnnotationKey(ManagedAgent.Claude, "claude-one"), annotation.Key);
        Assert.Equal("Known", annotation.Value.Title);
    }

    [Fact]
    public async Task LoadAsync_SkipsASourceThisBuildDoesNotKnow()
    {
        await using var fixture = new AnnotationFixture();
        await fixture.WriteRawAsync(
            "claude-claude-one.json", Document(sessionId: "claude-one", source: "ImportedFromSomewhere"));

        Assert.Empty(await fixture.CreateStore().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_SkipsAFileItCannotParseAndKeepsTheRest()
    {
        await using var fixture = new AnnotationFixture();
        await fixture.WriteRawAsync("claude-broken.json", "{ not json");
        await fixture.WriteRawAsync("claude-claude-one.json", Document(sessionId: "claude-one", title: "Intact"));

        Assert.Equal("Intact", Assert.Single(await fixture.CreateStore().LoadAsync(CancellationToken.None)).Value.Title);
    }

    [Fact]
    public async Task LoadAsync_SkipsASchemaVersionItDoesNotKnow()
    {
        await using var fixture = new AnnotationFixture();
        await fixture.WriteRawAsync("claude-claude-one.json", Document(sessionId: "claude-one", schemaVersion: 99));

        Assert.Empty(await fixture.CreateStore().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_SkipsAFileNamedForASessionOtherThanTheOneInside()
    {
        // The name is the address. A file that disagrees with its own name is not trusted to say
        // which session it belongs to.
        await using var fixture = new AnnotationFixture();
        await fixture.WriteRawAsync("claude-claude-one.json", Document(sessionId: "a-different-session"));

        Assert.Empty(await fixture.CreateStore().LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_AcceptsATitleOfExactlyTheBound()
    {
        await using var fixture = new AnnotationFixture();
        var title = new string('t', SessionAnnotation.MaximumTitleLength);

        await fixture.CreateStore().SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Claude, "claude-one"), Annotation(title), CancellationToken.None);

        Assert.Equal(title, Assert.Single(await fixture.CreateStore().LoadAsync(CancellationToken.None)).Value.Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveAsync_RefusesAnEmptyTitle(string title)
    {
        await using var fixture = new AnnotationFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.CreateStore().SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Claude, "claude-one"), Annotation(title), CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_RefusesATitleOverTheBound()
    {
        await using var fixture = new AnnotationFixture();
        var title = new string('t', SessionAnnotation.MaximumTitleLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.CreateStore().SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Claude, "claude-one"), Annotation(title), CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_RefusesADescriptionOverTheBound()
    {
        await using var fixture = new AnnotationFixture();
        var annotation = Annotation("Title") with
        {
            Description = new string('d', SessionAnnotation.MaximumDescriptionLength + 1)
        };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.CreateStore().SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Claude, "claude-one"), annotation, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("nested/id")]
    [InlineData(@"nested\id")]
    [InlineData("colon:id")]
    public async Task SaveAsync_RefusesASessionIdThatIsNotASafeNameComponent(string sessionId)
    {
        await using var fixture = new AnnotationFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.CreateStore().SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Claude, sessionId), Annotation("Title"), CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTemporaryFileBehind()
    {
        await using var fixture = new AnnotationFixture();

        await fixture.CreateStore().SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Claude, "claude-one"), Annotation("Title"), CancellationToken.None);

        Assert.Empty(Directory.GetFiles(fixture.AnnotationDirectory, "*.tmp"));
        Assert.Single(Directory.GetFiles(fixture.AnnotationDirectory));
    }

    [Fact]
    public async Task SaveAsync_KeepsEveryFileReadableWhileWritersOverlap()
    {
        await using var fixture = new AnnotationFixture();
        var store = fixture.CreateStore();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => store.SaveAsync(
            new SessionAnnotationKey(ManagedAgent.Claude, $"claude-{index}"),
            Annotation($"Title {index}"),
            CancellationToken.None)));

        Assert.Equal(16, (await fixture.CreateStore().LoadAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public void Serialize_AndTryRead_AgreeOnOneAnnotation()
    {
        // These bytes are what travels between machines, so the pair has to be exact.
        var key = new SessionAnnotationKey(ManagedAgent.Grok, "grok-one");
        var bytes = SessionAnnotationStore.Serialize(key, Annotation("Named on another machine"));

        Assert.True(SessionAnnotationStore.TryRead(Encoding.UTF8.GetString(bytes), out var read, out var annotation));
        Assert.Equal(key, read);
        Assert.Equal("Named on another machine", annotation.Title);
        Assert.Equal(Moment, annotation.UpdatedAt);
    }

    [Fact]
    public void Serialize_WritesTheSameBytesOnEveryMachine()
    {
        var key = new SessionAnnotationKey(ManagedAgent.Claude, "claude-one");

        Assert.Equal(
            SessionAnnotationStore.Serialize(key, Annotation("Stable")),
            SessionAnnotationStore.Serialize(key, Annotation("Stable")));
    }

    [Fact]
    public void TryRead_RefusesTextThatIsNotAnAnnotation()
    {
        Assert.False(SessionAnnotationStore.TryRead("{ not json", out _, out _));
        Assert.False(SessionAnnotationStore.TryRead("null", out _, out _));
        Assert.False(SessionAnnotationStore.TryRead("""{ "schemaVersion": 1 }""", out _, out _));
    }

    [Fact]
    public void FileName_NamesOneFilePerAgentAndSession() =>
        Assert.Equal(
            "claude-claude-one.json",
            SessionAnnotationStore.FileName(new SessionAnnotationKey(ManagedAgent.Claude, "claude-one")));

    private static string Document(
        string agent = "Claude",
        string sessionId = "claude-one",
        string title = "A title",
        string source = "Generated",
        int schemaVersion = 1) =>
        $$"""
          {
            "schemaVersion": {{schemaVersion}},
            "agent": "{{agent}}",
            "sessionId": "{{sessionId}}",
            "title": "{{title}}",
            "description": "What the session did.",
            "source": "{{source}}",
            "digestHash": "hash-1",
            "model": "qwen3:8b",
            "updatedAt": "2026-09-01T12:00:00+00:00"
          }
          """;

    private static SessionAnnotation Annotation(string title) => new(
        title,
        "What the session did.",
        SessionAnnotationSource.Generated,
        "hash-1",
        "qwen3:8b",
        Moment);

    private sealed class AnnotationFixture : IAsyncDisposable
    {
        public AnnotationFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"chs-annotations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(AnnotationDirectory);
        }

        public string Root { get; }

        public string AnnotationDirectory => Path.Combine(Root, "CodexHistorySync", "annotations");

        public SessionAnnotationStore CreateStore() => new(Root);

        public Task WriteRawAsync(string fileName, string json) =>
            File.WriteAllTextAsync(Path.Combine(AnnotationDirectory, fileName), json);

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
