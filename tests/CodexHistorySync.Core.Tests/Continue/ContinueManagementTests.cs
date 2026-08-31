using System.Text.Json.Nodes;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Continue;

public sealed class ContinueManagementTests : IDisposable
{
    private const string SessionId = "9490954d-d7dd-4cbe-984c-6172d60bf3dc";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-continue-management-{Guid.NewGuid():N}");

    [Fact]
    public async Task TheCatalogTakesTitlesFromTheIndex()
    {
        var paths = CreateContinueHome();
        WriteSession(paths, SessionId, "title in the file");
        File.WriteAllText(paths.IndexFilePath, ContinueSessionIndex.Serialize(
            [ContinueSessionIndex.Synthesize(SessionId, "title in the index", "", DateTimeOffset.UnixEpoch, 2)]));

        var snapshot = await CreateCatalog(paths).ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Continue);
        Assert.Equal("title in the index", session.Title);
        Assert.Equal(ManagedAgent.Continue, session.Agent);
        Assert.True(session.CanRead);
        Assert.Contains(ManagedAgent.Continue, snapshot.ConfiguredAgents);
        Assert.Equal(snapshot.Continue, snapshot.For(ManagedAgent.Continue));
    }

    [Fact]
    public async Task ASessionMissingFromTheIndexIsStillListed()
    {
        var paths = CreateContinueHome();
        WriteSession(paths, SessionId, "title in the file");
        File.WriteAllText(paths.IndexFilePath, ContinueSessionIndex.Serialize([]));

        var snapshot = await CreateCatalog(paths).ScanAsync(CancellationToken.None);

        Assert.Equal("title in the file", Assert.Single(snapshot.Continue).Title);
    }

    [Fact]
    public async Task TheIndexNeverAppearsAsASession()
    {
        var paths = CreateContinueHome();
        WriteSession(paths, SessionId, "real");
        File.WriteAllText(paths.IndexFilePath, ContinueSessionIndex.Serialize([]));

        var snapshot = await CreateCatalog(paths).ScanAsync(CancellationToken.None);

        Assert.Single(snapshot.Continue);
        Assert.DoesNotContain(snapshot.Continue, session => ContinuePaths.IsIndexFile(session.NativePath));
    }

    [Fact]
    public async Task ABrokenIndexCostsTitlesRatherThanTheListing()
    {
        var paths = CreateContinueHome();
        WriteSession(paths, SessionId, "title in the file");
        File.WriteAllText(paths.IndexFilePath, "{ not an array");

        var snapshot = await CreateCatalog(paths).ScanAsync(CancellationToken.None);

        Assert.Equal("title in the file", Assert.Single(snapshot.Continue).Title);
    }

    [Fact]
    public async Task ASessionCopiesFromContinueToClaude()
    {
        var continuePaths = CreateContinueHome();
        var claudePaths = CreateClaudeHome();
        WriteSession(continuePaths, SessionId, "carried title", "file:///c%3A/Repos/Demo");
        var operations = CreateOperations(continuePaths, claudePaths);
        var source = await SingleAsync(CreateCatalog(continuePaths), ManagedAgent.Continue);

        var copiedId = await operations.CopyAsync(source, ManagedAgent.Claude, CancellationToken.None);

        var destination = Assert.Single(
            Directory.GetFiles(claudePaths.Projects, copiedId + ".jsonl", SearchOption.AllDirectories));
        var copied = await new ClaudeConversationReader().ReadAsync(destination, CancellationToken.None);
        Assert.Equal("carried title", copied.Title);
        Assert.Equal(
            [new PortableTurn(ConversationRole.User, "question"), new PortableTurn(ConversationRole.Assistant, "answer")],
            copied.Turns);
        // The workspace URI decoded into a real path, so the copy landed in that project directory.
        Assert.Equal(ClaudePaths.EncodeProjectSegment(@"c:\Repos\Demo"),
            Path.GetFileName(Path.GetDirectoryName(destination)));
    }

    [Fact]
    public async Task ASessionCopiedIntoContinueIsListedInTheIndex()
    {
        var continuePaths = CreateContinueHome();
        var claudePaths = CreateClaudeHome();
        File.WriteAllText(continuePaths.IndexFilePath, ContinueSessionIndex.Serialize([]));
        var claudeSession = WriteClaudeSession(claudePaths, "11111111-1111-1111-1111-111111111111", "from claude");
        var operations = CreateOperations(continuePaths, claudePaths);
        var source = new ManagedSession(ManagedAgent.Claude, "11111111-1111-1111-1111-111111111111",
            claudeSession, "from claude", DateTimeOffset.UtcNow, false, true);

        var copiedId = await operations.CopyAsync(source, ManagedAgent.Continue, CancellationToken.None);

        Assert.True(File.Exists(continuePaths.SessionFilePath(copiedId)));
        var entry = Assert.Single(ContinueSessionIndex.Parse(File.ReadAllText(continuePaths.IndexFilePath)));
        Assert.Equal("from claude", (string?)entry["title"]);
        Assert.Equal(copiedId, (string?)entry["sessionId"]);
    }

    [Fact]
    public async Task DeletingASessionAlsoRemovesItsIndexEntry()
    {
        // A row left in the index points at a file that is gone, and Continue throws when it is
        // opened. Its own delete removes both, and so must this one.
        var continuePaths = CreateContinueHome();
        WriteSession(continuePaths, SessionId, "doomed");
        File.WriteAllText(continuePaths.IndexFilePath, ContinueSessionIndex.Serialize(
        [
            ContinueSessionIndex.Synthesize(SessionId, "doomed", "", DateTimeOffset.UnixEpoch, 2),
            ContinueSessionIndex.Synthesize("22222222-2222-2222-2222-222222222222", "keep me", "", DateTimeOffset.UnixEpoch, 2)
        ]));
        var operations = CreateOperations(continuePaths, CreateClaudeHome());
        var source = await SingleAsync(CreateCatalog(continuePaths), ManagedAgent.Continue);

        await operations.DeleteAsync(source, CancellationToken.None);

        Assert.False(File.Exists(continuePaths.SessionFilePath(SessionId)));
        var entry = Assert.Single(ContinueSessionIndex.Parse(File.ReadAllText(continuePaths.IndexFilePath)));
        Assert.Equal("keep me", (string?)entry["title"]);
    }

    [Fact]
    public void ContinueIsACopyTargetForEveryOtherAgent()
    {
        Assert.Contains(ManagedAgent.Continue, ManagedAgents.Destinations(ManagedAgent.Claude));
        Assert.Contains(ManagedAgent.Continue, ManagedAgents.Destinations(ManagedAgent.Codex));
        Assert.DoesNotContain(ManagedAgent.Continue, ManagedAgents.Destinations(ManagedAgent.Continue));
        Assert.Equal(4, ManagedAgents.All.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static async Task<ManagedSession> SingleAsync(ILocalSessionCatalog catalog, ManagedAgent agent) =>
        Assert.Single((await catalog.ScanAsync(CancellationToken.None)).For(agent));

    private ILocalSessionCatalog CreateCatalog(ContinuePaths paths) =>
        new LocalSessionCatalog(null, null, new InactiveState(), continuePaths: paths);

    private static ILocalSessionOperations CreateOperations(ContinuePaths continuePaths, ClaudePaths claudePaths) =>
        new LocalSessionOperations(
            null, null, new InactiveState(), new UnusedDirectoryDeleter(), null, null,
            claudePaths, new ClaudeConversationWriter(claudePaths),
            continuePaths, new ContinueConversationWriter(continuePaths));

    private ContinuePaths CreateContinueHome()
    {
        var home = Path.Combine(root, Guid.NewGuid().ToString("N"), ".continue");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        return new ContinuePaths(home, sessions);
    }

    private ClaudePaths CreateClaudeHome()
    {
        var home = Path.Combine(root, Guid.NewGuid().ToString("N"), ".claude");
        var projects = Path.Combine(home, "projects");
        Directory.CreateDirectory(projects);
        return new ClaudePaths(home, projects);
    }

    private static void WriteSession(ContinuePaths paths, string sessionId, string title, string workspace = "")
    {
        File.WriteAllText(Path.Combine(paths.Sessions, sessionId + ".json"), new JsonObject
        {
            ["sessionId"] = sessionId,
            ["title"] = title,
            ["workspaceDirectory"] = workspace,
            ["history"] = new JsonArray
            {
                Turn("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "question" } }),
                Turn("assistant", "answer")
            },
            ["mode"] = "agent"
        }.ToJsonString());
    }

    private static string WriteClaudeSession(ClaudePaths paths, string sessionId, string title)
    {
        var project = ClaudePaths.EncodeProjectSegment(@"c:\Repos\Demo");
        var directory = Path.Combine(paths.Projects, project);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, sessionId + ".jsonl");
        var records = new[]
        {
            Record(sessionId, "user", "question"),
            Record(sessionId, "assistant", "answer"),
            new JsonObject { ["type"] = "ai-title", ["aiTitle"] = title, ["sessionId"] = sessionId }.ToJsonString()
        };
        File.WriteAllText(path, string.Join('\n', records) + "\n");
        return path;
    }

    private static string Record(string sessionId, string role, string text) => new JsonObject
    {
        ["type"] = role,
        ["sessionId"] = sessionId,
        ["cwd"] = @"c:\Repos\Demo",
        ["isSidechain"] = false,
        ["message"] = new JsonObject
        {
            ["role"] = role,
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }
        }
    }.ToJsonString();

    private static JsonObject Turn(string role, JsonNode? content) => new()
    {
        ["message"] = new JsonObject { ["role"] = role, ["content"] = content },
        ["contextItems"] = new JsonArray()
    };

    private sealed class InactiveState : IManagedSessionActiveState
    {
        public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(ManagedAgent agent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<bool> IsActiveAsync(ManagedAgent agent, string sessionId, string nativePath,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class UnusedDirectoryDeleter : IManagedSessionDirectoryDeleter
    {
        public Task DeleteAsync(string sessionsRoot, string sessionDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A file-backed agent must not use the directory deleter.");
    }
}
