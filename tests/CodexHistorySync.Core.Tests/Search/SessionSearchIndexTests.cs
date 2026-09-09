using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;

namespace CodexHistorySync.Core.Tests.Search;

public sealed class SessionSearchIndexTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "chs-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnsureCurrentAsync_IndexesASessionSoSearchFindsItsBody()
    {
        using var index = new SessionSearchIndex(root);
        var reader = new StubReader();
        var session = Session(ManagedAgent.Codex, "alpha", "Short title");
        reader.Bodies[session.SessionId] = "the unique zebra phrase lives only in the body";

        await index.EnsureCurrentAsync(Snapshot(session), reader, CancellationToken.None);

        var hits = await index.SearchAsync("unique zebra phrase", 10, CancellationToken.None);

        var hit = Assert.Single(hits);
        Assert.Equal(ManagedAgent.Codex, hit.Agent);
        Assert.Equal("alpha", hit.SessionId);
        Assert.Equal("Short title", hit.Title);
        Assert.Contains("zebra", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task EnsureCurrentAsync_SkipsASecondPassWhenTheDigestIsUnchanged()
    {
        using var index = new SessionSearchIndex(root);
        var reader = new StubReader();
        var session = Session(ManagedAgent.Grok, "beta", "Title");
        reader.Bodies[session.SessionId] = "stable conversation text";

        await index.EnsureCurrentAsync(Snapshot(session), reader, CancellationToken.None);
        await index.EnsureCurrentAsync(Snapshot(session), reader, CancellationToken.None);

        Assert.Equal(1, reader.Reads);
        var hits = await index.SearchAsync("stable conversation", 10, CancellationToken.None);
        Assert.Single(hits);
    }

    [Fact]
    public async Task EnsureCurrentAsync_ReindexesWhenTheBodyChanges()
    {
        using var index = new SessionSearchIndex(root);
        var reader = new StubReader();
        var session = Session(ManagedAgent.Claude, "gamma", "Title");
        reader.Bodies[session.SessionId] = "first wording about apples";

        await index.EnsureCurrentAsync(Snapshot(session), reader, CancellationToken.None);
        reader.Bodies[session.SessionId] = "second wording about oranges";
        var updated = session with { LastModifiedAt = session.LastModifiedAt.AddMinutes(1) };
        await index.EnsureCurrentAsync(Snapshot(updated), reader, CancellationToken.None);

        Assert.Empty(await index.SearchAsync("apples", 10, CancellationToken.None));
        var hits = await index.SearchAsync("oranges", 10, CancellationToken.None);
        Assert.Equal("gamma", Assert.Single(hits).SessionId);
    }

    [Fact]
    public async Task EnsureCurrentAsync_RefreshesTitleAndTimestampEvenWhenTheBodyIsUnchanged()
    {
        using var index = new SessionSearchIndex(root);
        var reader = new StubReader();
        var session = Session(ManagedAgent.Codex, "rename", "OldHeading");
        reader.Bodies[session.SessionId] = "unchanged conversation";
        await index.EnsureCurrentAsync(Snapshot(session), reader, CancellationToken.None);

        var renamed = session with { Title = "NewHeading", LastModifiedAt = session.LastModifiedAt.AddMinutes(1) };
        await index.EnsureCurrentAsync(Snapshot(renamed), reader, CancellationToken.None);
        await index.EnsureCurrentAsync(Snapshot(renamed), reader, CancellationToken.None);

        Assert.Empty(await index.SearchAsync("OldHeading", 10, CancellationToken.None));
        Assert.Equal("NewHeading", Assert.Single(await index.SearchAsync("NewHeading", 10, CancellationToken.None)).Title);
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task EnsureCurrentAsync_DeletesSessionsThatLeftTheCatalog()
    {
        using var index = new SessionSearchIndex(root);
        var reader = new StubReader();
        var kept = Session(ManagedAgent.Codex, "kept", "Keep");
        var gone = Session(ManagedAgent.Codex, "gone", "Drop");
        reader.Bodies[kept.SessionId] = "kept body pineapple";
        reader.Bodies[gone.SessionId] = "gone body pineapple";

        await index.EnsureCurrentAsync(Snapshot(kept, gone), reader, CancellationToken.None);
        await index.EnsureCurrentAsync(Snapshot(kept), reader, CancellationToken.None);

        var hits = await index.SearchAsync("pineapple", 10, CancellationToken.None);
        Assert.Equal("kept", Assert.Single(hits).SessionId);
    }

    [Fact]
    public async Task EnsureCurrentAsync_DoesNotReadAnUnreadableSession()
    {
        using var index = new SessionSearchIndex(root);
        var reader = new StubReader();
        var readable = Session(ManagedAgent.Codex, "ok", "Ok");
        var broken = Session(ManagedAgent.Codex, "bad", "Bad") with { CanRead = false };
        reader.Bodies[readable.SessionId] = "readable mango text";
        reader.Bodies[broken.SessionId] = "should never be indexed mango";

        await index.EnsureCurrentAsync(Snapshot(readable, broken), reader, CancellationToken.None);

        Assert.Equal(["ok"], reader.ReadIds);
        var hits = await index.SearchAsync("mango", 10, CancellationToken.None);
        Assert.Equal("ok", Assert.Single(hits).SessionId);
    }

    [Fact]
    public async Task SearchAsync_FindsBodyTextThatIsNotInTheTitle()
    {
        using var index = new SessionSearchIndex(root);
        var reader = new StubReader();
        var session = Session(ManagedAgent.Continue, "delta", "Unrelated heading");
        reader.Bodies[session.SessionId] = "discuss the auth handshake in detail";

        await index.EnsureCurrentAsync(Snapshot(session), reader, CancellationToken.None);

        Assert.Empty(await index.SearchAsync("Unrelated heading that is not this", 10, CancellationToken.None));
        Assert.Equal("delta", Assert.Single(await index.SearchAsync("auth handshake", 10, CancellationToken.None)).SessionId);
    }

    [Fact]
    public async Task SearchAsync_EmptyOrWhitespaceQueryMatchesNothing()
    {
        using var index = new SessionSearchIndex(root);
        var reader = new StubReader();
        var session = Session(ManagedAgent.Codex, "echo", "Title");
        reader.Bodies[session.SessionId] = "anything at all";
        await index.EnsureCurrentAsync(Snapshot(session), reader, CancellationToken.None);

        Assert.Empty(await index.SearchAsync("", 10, CancellationToken.None));
        Assert.Empty(await index.SearchAsync("   ", 10, CancellationToken.None));
        Assert.Empty(await index.SearchAsync("\"\"", 10, CancellationToken.None));
    }

    [Fact]
    public void Constructor_RejectsAMissingLocalAppDataDirectory()
    {
        Assert.ThrowsAny<Exception>(() => new SessionSearchIndex(""));
        Assert.ThrowsAny<Exception>(() => new SessionSearchIndex("   "));
    }

    [Fact]
    public void DatabasePath_StaysInsideTheCodexHistorySyncDirectory()
    {
        using var index = new SessionSearchIndex(root);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "CodexHistorySync", "catalog.db")),
            index.DatabasePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static SessionCatalogSnapshot Snapshot(params ManagedSession[] sessions)
    {
        var grouped = sessions.ToLookup(session => session.Agent);
        return new SessionCatalogSnapshot(
            grouped[ManagedAgent.Codex].ToArray(),
            grouped[ManagedAgent.Grok].ToArray(),
            grouped[ManagedAgent.Claude].ToArray(),
            grouped[ManagedAgent.Continue].ToArray())
        {
            ConfiguredAgents = ManagedAgents.All
        };
    }

    private static ManagedSession Session(ManagedAgent agent, string id, string title) =>
        new(agent, id, Path.Combine("C:", "native", id), title, DateTimeOffset.UnixEpoch, false, true);

    private sealed class StubReader : ISessionContentReader
    {
        public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);
        public List<string> ReadIds { get; } = [];
        public int Reads => ReadIds.Count;

        public Task<PortableConversation> ReadAsync(ManagedSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadIds.Add(session.SessionId);
            var body = Bodies[session.SessionId];

            return Task.FromResult(new PortableConversation(
                ConversationAgent.Codex,
                session.SessionId,
                session.Title,
                @"C:\Repos\Demo",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                [new PortableTurn(ConversationRole.User, body)]));
        }
    }
}
