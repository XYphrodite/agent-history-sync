using CodexHistorySync.Cli.Mcp;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;
using ModelContextProtocol;

namespace CodexHistorySync.IntegrationTests;

public sealed class SessionMcpToolsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chs-mcp-tools-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SearchAndReadKeepIdenticalIdsFromDifferentAgentsSeparate()
    {
        var sessions = ManagedAgents.All.Select(agent => Session(agent, "shared-id")).ToArray();
        var catalog = new Catalog(sessions);
        var reader = new Reader();
        using var index = new SessionSearchIndex(root);
        using var tools = new SessionMcpTools(catalog, index, reader);

        var results = await tools.SearchAsync("conversation");
        Assert.Equal(4, results.Sessions.Count);
        foreach (var agent in ManagedAgents.All)
        {
            var page = await tools.GetAsync(agent.ToString().ToLowerInvariant(), "shared-id");
            Assert.Contains(agent + " conversation", page.Text);
            Assert.Equal(agent + " title", page.Title);
            Assert.DoesNotContain("native", page.Text);
        }
    }

    [Fact]
    public async Task CancellationReleasesTheGateForTheNextRequest()
    {
        var catalog = new Catalog([Session(ManagedAgent.Codex, "id")]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.BeforeScan = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var index = new SessionSearchIndex(root);
        using var tools = new SessionMcpTools(catalog, index, new Reader());
        using var cancellation = new CancellationTokenSource();
        var pending = tools.SearchAsync("conversation", cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        catalog.BeforeScan = null;
        var result = await tools.SearchAsync("conversation").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(result.Sessions);
    }

    [Fact]
    public async Task PagingDoesNotSplitASurrogatePairOrLoseTheRestOfALongTurn()
    {
        var catalog = new Catalog([Session(ManagedAgent.Codex, "id")]);
        using var index = new SessionSearchIndex(root);
        using var tools = new SessionMcpTools(catalog, index, new Reader { Text = "😀" + new string('x', 100000) });

        var first = await tools.GetAsync("codex", "id", max_characters: 7);
        Assert.Equal("USER:\n", first.Text);
        Assert.Equal(6, first.NextOffset);
        var emoji = await tools.GetAsync("codex", "id", offset: first.NextOffset!.Value, max_characters: 2);
        Assert.Equal("😀", emoji.Text);
        Assert.Equal(8, emoji.NextOffset);
        Assert.Equal(100008, emoji.TotalCharacters);
        var remainder = await tools.GetAsync("codex", "id", offset: 100000);
        Assert.Equal(new string('x', 8), remainder.Text);
        Assert.Null(remainder.NextOffset);
        await Assert.ThrowsAsync<McpException>(() => tools.GetAsync("codex", "id", offset: 7));
    }

    [Fact]
    public async Task UnreadableSessionsAreNotOpenedAndReadFailuresDoNotExposePaths()
    {
        var catalog = new Catalog([Session(ManagedAgent.Claude, "id") with { CanRead = false }]);
        var reader = new Reader { Failure = new InvalidDataException("private/path/transcript") };
        using var index = new SessionSearchIndex(root);
        using var tools = new SessionMcpTools(catalog, index, reader);
        await Assert.ThrowsAsync<McpException>(() => tools.GetAsync("claude", "id"));
        Assert.Equal(0, reader.Reads);
        catalog.Sessions = [Session(ManagedAgent.Claude, "id")];
        var failure = await Assert.ThrowsAsync<McpException>(() => tools.GetAsync("claude", "id"));
        Assert.DoesNotContain("private/path", failure.Message);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task InvalidSearchLimitsDoNotScanOrCreateAnIndex()
    {
        var catalog = new Catalog([]);
        using var index = new SessionSearchIndex(root);
        using var tools = new SessionMcpTools(catalog, index, new Reader());
        await Assert.ThrowsAsync<McpException>(() => tools.SearchAsync(new string('x', 1025)));
        await Assert.ThrowsAsync<McpException>(() => tools.SearchAsync("text", limit: 0));
        Assert.Equal(0, catalog.Scans);
        Assert.False(File.Exists(index.DatabasePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private ManagedSession Session(ManagedAgent agent, string id) =>
        new(agent, id, Path.Combine(root, "native", agent.ToString(), id), agent + " title", DateTimeOffset.UnixEpoch, false, true);

    private sealed class Catalog(ManagedSession[] sessions) : ILocalSessionCatalog
    {
        public ManagedSession[] Sessions { get; set; } = sessions;
        public Func<CancellationToken, Task>? BeforeScan { get; set; }
        public int Scans { get; private set; }

        public async Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
        {
            Scans++;
            if (BeforeScan is not null) await BeforeScan(cancellationToken);
            var grouped = Sessions.ToLookup(session => session.Agent);
            return new SessionCatalogSnapshot(grouped[ManagedAgent.Codex].ToArray(), grouped[ManagedAgent.Grok].ToArray(),
                grouped[ManagedAgent.Claude].ToArray(), grouped[ManagedAgent.Continue].ToArray()) { ConfiguredAgents = ManagedAgents.All };
        }
    }

    private sealed class Reader : ISessionContentReader
    {
        public string? Text { get; init; }
        public Exception? Failure { get; init; }
        public int Reads { get; private set; }

        public Task<PortableConversation> ReadAsync(ManagedSession session, CancellationToken cancellationToken)
        {
            Reads++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new PortableConversation(Enum.Parse<ConversationAgent>(session.Agent.ToString()),
                session.SessionId, session.Title, null, session.LastModifiedAt, session.LastModifiedAt,
                [new PortableTurn(ConversationRole.User, Text ?? session.Agent + " conversation")]));
        }
    }
}
