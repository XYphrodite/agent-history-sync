using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class LocalSessionCatalogTests
{
    [Fact]
    public async Task ScanAsyncOrdersEachAgentByDescendingModifiedTimeAndExtractsTitles()
    {
        await using var fixture = new CatalogFixture();
        var olderCodex = await fixture.WriteCodexAsync(
            "older-codex", "Explicit Codex title", "older question", "2026-08-09T10:00:00Z");
        var newerCodex = await fixture.WriteCodexAsync(
            "newer-codex", null, "Fallback Codex question", "2026-08-09T12:00:00Z");
        var olderGrok = await fixture.WriteGrokAsync(
            "10000000-0000-0000-0000-000000000001", "Explicit Grok title", "older Grok question",
            "2026-08-09T09:00:00Z");
        var newerGrok = await fixture.WriteGrokAsync(
            "20000000-0000-0000-0000-000000000002", null, "Fallback Grok question",
            "2026-08-09T13:00:00Z");

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Collection(snapshot.Codex,
            session => Assert.Equal(
                ("newer-codex", "Fallback Codex question", Path.GetFullPath(newerCodex)),
                (session.SessionId, session.Title, session.NativePath)),
            session => Assert.Equal(
                ("older-codex", "Explicit Codex title", Path.GetFullPath(olderCodex)),
                (session.SessionId, session.Title, session.NativePath)));
        Assert.Collection(snapshot.Grok,
            session => Assert.Equal(
                ("20000000-0000-0000-0000-000000000002", "Fallback Grok question", Path.GetFullPath(newerGrok)),
                (session.SessionId, session.Title, session.NativePath)),
            session => Assert.Equal(
                ("10000000-0000-0000-0000-000000000001", "Explicit Grok title", Path.GetFullPath(olderGrok)),
                (session.SessionId, session.Title, session.NativePath)));
        Assert.True(snapshot.Codex[0].LastModifiedAt > snapshot.Codex[1].LastModifiedAt);
        Assert.True(snapshot.Grok[0].LastModifiedAt > snapshot.Grok[1].LastModifiedAt);
        Assert.All(snapshot.Codex.Concat(snapshot.Grok), session => Assert.True(session.CanRead));
    }

    [Fact]
    public async Task ScanAsyncChecksActivityOncePerAgent()
    {
        // Per-row process queries make startup cost grow with the number of displayed sessions.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("activity-codex-one", null, "one", "2026-08-09T10:00:00Z");
        await fixture.WriteCodexAsync("activity-codex-two", null, "two", "2026-08-09T11:00:00Z");
        await fixture.WriteGrokAsync(
            "51000000-0000-0000-0000-000000000001", null, "one", "2026-08-09T12:00:00Z");
        await fixture.WriteGrokAsync(
            "52000000-0000-0000-0000-000000000002", null, "two", "2026-08-09T13:00:00Z");

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Codex.Count);
        Assert.Equal(2, snapshot.Grok.Count);
        Assert.Equal(1, fixture.ActiveState.TotalQueries[ManagedAgent.Codex]);
        Assert.Equal(1, fixture.ActiveState.TotalQueries[ManagedAgent.Grok]);
    }

    [Fact]
    public async Task ScanAsyncKeepsActiveEntriesVisibleWhenNativeScannerDefersThem()
    {
        await using var fixture = new CatalogFixture();
        var sessionId = "30000000-0000-0000-0000-000000000003";
        await fixture.WriteGrokAsync(sessionId, "Active Grok", "question", "2026-08-09T14:00:00Z");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.GrokHome, "active_sessions.json"),
            JsonSerializer.Serialize(new[] { new { session_id = sessionId } }),
            new UTF8Encoding(false));
        fixture.ActiveState.ActiveIds.Add(sessionId);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Grok);
        Assert.Equal(sessionId, session.SessionId);
        Assert.True(session.IsActive);
        Assert.True(session.CanRead);
    }

    [Fact]
    public async Task ScanAsyncKeepsActiveSafelyIdentifiableGrokDirectoryVisibleWhenChatIsMissing()
    {
        await using var fixture = new CatalogFixture();
        var sessionId = "31000000-0000-0000-0000-000000000003";
        var sessionPath = await fixture.WriteGrokSummaryOnlyAsync(sessionId, "Missing chat");
        fixture.ActiveState.ActiveIds.Add(sessionId);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Grok);
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(Path.GetFullPath(sessionPath), session.NativePath);
        Assert.Equal("Missing chat", session.Title);
        Assert.True(session.IsActive);
        Assert.False(session.CanRead);
    }

    [Fact]
    public async Task ScanAsyncKeepsSafelyIdentifiableMalformedEntriesAsUnreadable()
    {
        await using var fixture = new CatalogFixture();
        var codexPath = await fixture.WriteMalformedCodexAsync("malformed-codex");
        var grokId = "40000000-0000-0000-0000-000000000004";
        var grokPath = await fixture.WriteMalformedGrokAsync(grokId);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        var codex = Assert.Single(snapshot.Codex);
        Assert.Equal(("malformed-codex", Path.GetFullPath(codexPath), "malformed-codex", false),
            (codex.SessionId, codex.NativePath, codex.Title, codex.CanRead));
        var grok = Assert.Single(snapshot.Grok);
        Assert.Equal((grokId, Path.GetFullPath(grokPath), grokId, false),
            (grok.SessionId, grok.NativePath, grok.Title, grok.CanRead));
    }

    [Fact]
    public async Task ScanAsyncReturnsEmptyColumnsWhenNativeRootsAreAbsent()
    {
        await using var fixture = new CatalogFixture(createNativeRoots: false);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Empty(snapshot.Codex);
        Assert.Empty(snapshot.Grok);
    }

    [Fact]
    public async Task ScanAsyncDoesNotSelectCodexCandidatesExcludedByNativeScannerRules()
    {
        await using var fixture = new CatalogFixture();
        var value = nameof(ScanAsyncDoesNotSelectCodexCandidatesExcludedByNativeScannerRules);
        var original = await fixture.WriteCodexAsync(value, value, value, DateTimeOffset.UtcNow.ToString());
        var disallowedDirectory = Path.Combine(
            fixture.CodexPaths.Sessions,
            new string(['l', 'o', 'g', 's']));
        Directory.CreateDirectory(disallowedDirectory);
        File.Move(original, Path.Combine(disallowedDirectory, Path.GetFileName(original)));

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Empty(snapshot.Codex);
    }

    [Fact]
    public async Task ScanAsyncUsesBoundedMetadataWithoutInvokingFullConversationReaderForLargeTail()
    {
        await using var fixture = new CatalogFixture();
        var path = await fixture.WriteCodexAsync(
            "bounded-codex", "Bounded title", "question", "2026-08-09T15:00:00Z");
        var largeTail = "{\"type\":\"event_msg\",\"payload\":{\"ignored\":\"" +
                        new string('x', 2 * 1024 * 1024);
        await File.AppendAllTextAsync(path, largeTail + "\n", new UTF8Encoding(false));

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Codex);
        Assert.Equal("Bounded title", session.Title);
        Assert.True(new FileInfo(path).Length > 2 * 1024 * 1024);
    }

    [Fact]
    public async Task ScanAsyncDoesNotFollowReparseCandidatesOutsideTheNativeRoot()
    {
        await using var fixture = new CatalogFixture();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside-sessions"));
        var outsideId = "50000000-0000-0000-0000-000000000005";
        var outsideSession = Path.Combine(outside.FullName, outsideId);
        Directory.CreateDirectory(outsideSession);
        await CatalogFixture.WriteGrokFilesAsync(
            outsideSession, outsideId, fixture.WorkingDirectory, "Outside", "outside question",
            "2026-08-09T15:00:00Z");
        var link = Path.Combine(fixture.GrokPaths.Sessions, "linked-outside");
        try
        {
            Directory.CreateSymbolicLink(link, outside.FullName);
            fixture.ReparsePaths.Add(link);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"Symbolic-link creation is unavailable: {exception.GetType().Name}");
        }

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.DoesNotContain(snapshot.Grok, session => session.SessionId == outsideId);
    }

    private sealed class CatalogFixture : IAsyncDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(false);
        private readonly string container;

        public CatalogFixture(bool createNativeRoots = true)
        {
            container = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codex-history-sync-task3-tests"));
            Directory.CreateDirectory(container);
            Root = Path.Combine(container, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            CodexHome = Path.Combine(Root, "codex-home");
            GrokHome = Path.Combine(Root, "grok-home");
            WorkingDirectory = Path.Combine(Root, "working-directory");
            Directory.CreateDirectory(CodexHome);
            Directory.CreateDirectory(GrokHome);
            Directory.CreateDirectory(WorkingDirectory);
            CodexPaths = CodexPaths.ResolveLayout(CodexHome);
            GrokPaths = new GrokPaths(GrokHome, Path.Combine(GrokHome, "sessions"));
            if (createNativeRoots)
            {
                Directory.CreateDirectory(CodexPaths.Sessions);
                Directory.CreateDirectory(CodexPaths.ArchivedSessions);
                Directory.CreateDirectory(GrokPaths.Sessions);
            }
        }

        public string Root { get; }
        public string CodexHome { get; }
        public string GrokHome { get; }
        public string WorkingDirectory { get; }
        public CodexPaths CodexPaths { get; }
        public GrokPaths GrokPaths { get; }
        public FakeActiveState ActiveState { get; } = new();
        public List<string> ReparsePaths { get; } = [];

        public LocalSessionCatalog CreateCatalog() => new(
            CodexPaths,
            GrokPaths,
            ActiveState,
            new SessionScanner(TimeSpan.Zero),
            new GrokSessionScanner(TimeSpan.Zero));

        public async Task<string> WriteCodexAsync(string id, string? title, string userText, string modifiedAt)
        {
            var directory = Path.Combine(CodexPaths.Sessions, "2026", "08", "09");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"rollout-{id}.jsonl");
            var metadata = new
            {
                type = "session_meta",
                payload = new { id, timestamp = "2026-08-09T08:00:00Z", cwd = WorkingDirectory, title }
            };
            var message = new
            {
                type = "response_item",
                payload = new
                {
                    type = "message", role = "user", timestamp = modifiedAt,
                    content = new[] { new { type = "input_text", text = userText } }
                }
            };
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(metadata) + "\n" + JsonSerializer.Serialize(message) + "\n", Utf8);
            return path;
        }

        public async Task<string> WriteGrokAsync(
            string id,
            string? title,
            string userText,
            string modifiedAt)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            await WriteGrokFilesAsync(session, id, WorkingDirectory, title, userText, modifiedAt);
            return session;
        }

        public async Task<string> WriteMalformedCodexAsync(string id)
        {
            var directory = Path.Combine(CodexPaths.Sessions, "2026", "08", "09");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"rollout-{id}.jsonl");
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } }) + "\n{bad-json}\n", Utf8);
            return path;
        }

        public async Task<string> WriteMalformedGrokAsync(string id)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"),
                "{\"role\":\"user\",\"content\":\"question\"}\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"), "{bad-json}", Utf8);
            return session;
        }

        public async Task<string> WriteGrokSummaryOnlyAsync(string id, string title)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"),
                JsonSerializer.Serialize(new
                {
                    info = new
                    {
                        id, cwd = WorkingDirectory, title,
                        created_at = "2026-08-09T08:00:00Z", updated_at = "2026-08-09T16:00:00Z"
                    }
                }), Utf8);
            return session;
        }

        public static async Task WriteGrokFilesAsync(
            string session,
            string id,
            string cwd,
            string? title,
            string userText,
            string modifiedAt)
        {
            await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"),
                JsonSerializer.Serialize(new
                {
                    role = "user",
                    content = new[] { new { type = "input_text", text = userText } }
                }) + "\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"),
                JsonSerializer.Serialize(new
                {
                    info = new
                    {
                        id, cwd, title, created_at = "2026-08-09T08:00:00Z", updated_at = modifiedAt
                    }
                }), Utf8);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var link in ReparsePaths)
            {
                if ((Directory.Exists(link) || File.Exists(link)) &&
                    File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(link);
            }

            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(container));
            if (!string.Equals(Path.GetDirectoryName(canonicalRoot), expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean up a test root outside its exact container.");
            if (Directory.Exists(canonicalRoot)) Directory.Delete(canonicalRoot, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeActiveState : IManagedSessionActiveState
    {
        public HashSet<string> ActiveIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<ManagedAgent, int> TotalQueries { get; } = new();

        public Task<bool> IsAgentActiveAsync(ManagedAgent agent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalQueries[agent] = TotalQueries.GetValueOrDefault(agent) + 1;
            return Task.FromResult(ActiveIds.Count != 0);
        }

        public Task<bool> IsActiveAsync(
            ManagedAgent agent,
            string sessionId,
            string nativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalQueries[agent] = TotalQueries.GetValueOrDefault(agent) + 1;
            return Task.FromResult(ActiveIds.Contains(sessionId));
        }
    }

}
