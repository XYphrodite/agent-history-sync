using System.Text.Json.Nodes;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Continue;

public sealed class ContinueConversationTests : IDisposable
{
    private const string SessionId = "9490954d-d7dd-4cbe-984c-6172d60bf3dc";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-continue-conversion-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadsTheShapeContinueActuallyWrites()
    {
        // Modelled on a real session: user content is an array of parts, assistant content is a
        // string, and an empty assistant entry precedes the model's thinking.
        var paths = CreateHome();
        var path = WriteSession(paths, SessionId, "hi", new JsonArray
        {
            Turn("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "hi" } }),
            Turn("assistant", ""),
            Turn("thinking", "The user just says \"hi\"."),
            Turn("assistant", "Hello! How can I help you today?")
        });

        var conversation = await new ContinueConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal(ConversationAgent.Continue, conversation.SourceAgent);
        Assert.Equal(SessionId, conversation.SourceSessionId);
        Assert.Equal("hi", conversation.Title);
        Assert.Equal(
            [new PortableTurn(ConversationRole.User, "hi"),
             new PortableTurn(ConversationRole.Assistant, "Hello! How can I help you today?")],
            conversation.Turns);
    }

    [Fact]
    public async Task TheCreationTimeComesFromTheIndexBecauseTheSessionFileHasNone()
    {
        var paths = CreateHome();
        var path = WriteSession(paths, SessionId, "hi", SimpleHistory());
        File.WriteAllText(paths.IndexFilePath, ContinueSessionIndex.Serialize(
            [ContinueSessionIndex.Synthesize(SessionId, "hi", "", DateTimeOffset.FromUnixTimeMilliseconds(1788134536812), 2)]));

        var conversation = await new ContinueConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1788134536812), conversation.CreatedAt);
    }

    [Fact]
    public async Task ASessionMissingFromTheIndexStillReads()
    {
        var paths = CreateHome();
        var path = WriteSession(paths, SessionId, "unlisted", SimpleHistory());

        var conversation = await new ContinueConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal("unlisted", conversation.Title);
        Assert.True(conversation.CreatedAt <= conversation.LastModifiedAt);
    }

    [Fact]
    public async Task TheWorkspaceUriBecomesAPathSoACopyLandsWhereItCameFrom()
    {
        var paths = CreateHome();
        var path = WriteSession(paths, SessionId, "hi", SimpleHistory(), "file:///c%3A/Repos/Reborn");

        var conversation = await new ContinueConversationReader().ReadAsync(path, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(@"c:\Repos\Reborn"), conversation.WorkingDirectory);
    }

    [Fact]
    public async Task TheIndexIsNeverReadAsAConversation()
    {
        var paths = CreateHome();
        File.WriteAllText(paths.IndexFilePath, ContinueSessionIndex.Serialize([]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ContinueConversationReader().ReadAsync(paths.IndexFilePath, CancellationToken.None));
    }

    [Theory]
    [InlineData("\"history\": {}")]
    [InlineData("\"history\": \"none\"")]
    [InlineData("\"history\": []")]
    public async Task ASessionWithNoUsableHistoryIsRefused(string history)
    {
        // A conversation with nothing in it is not a conversation: copying it would produce an
        // empty session on the destination agent and call that success.
        var paths = CreateHome();
        var path = Path.Combine(paths.Sessions, SessionId + ".json");
        File.WriteAllText(path,
            "{\"sessionId\":\"" + SessionId + "\",\"title\":\"empty\"," + history + "}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ContinueConversationReader().ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task ASessionFileNamingAnotherSessionIsRefused()
    {
        var paths = CreateHome();
        var path = Path.Combine(paths.Sessions, SessionId + ".json");
        File.WriteAllText(path, new JsonObject
        {
            ["sessionId"] = "00000000-0000-0000-0000-000000000000",
            ["title"] = "mismatched",
            ["history"] = SimpleHistory()
        }.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ContinueConversationReader().ReadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task AWrittenSessionIsListedAndReadsBackUnchanged()
    {
        var paths = CreateHome();
        File.WriteAllText(paths.IndexFilePath, ContinueSessionIndex.Serialize(
            [ContinueSessionIndex.Synthesize("11111111-1111-1111-1111-111111111111", "local", "", DateTimeOffset.UnixEpoch, 1)]));
        var conversation = new PortableConversation(
            ConversationAgent.Claude, "source-session", "Carried title", @"c:\Repos\Reborn",
            DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), DateTimeOffset.FromUnixTimeMilliseconds(1700000900000),
            [new PortableTurn(ConversationRole.User, "question"), new PortableTurn(ConversationRole.Assistant, "answer")]);

        var result = await new ContinueConversationWriter(paths).WriteAsync(conversation, CancellationToken.None);

        var written = await new ContinueConversationReader().ReadAsync(result.NativePath, CancellationToken.None);
        Assert.Equal("Carried title", written.Title);
        Assert.Equal(conversation.Turns, written.Turns);
        Assert.Equal(Path.GetFullPath(@"c:\Repos\Reborn"), written.WorkingDirectory);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), written.CreatedAt);

        // Listed after the entry that was already there, which is where Continue shows it first.
        var entries = ContinueSessionIndex.Parse(File.ReadAllText(paths.IndexFilePath));
        Assert.Equal(2, entries.Count);
        Assert.Equal("local", (string?)entries[0]["title"]);
        Assert.Equal(result.SessionId, (string?)entries[1]["sessionId"]);
    }

    [Fact]
    public async Task AWrittenSessionKeepsContinuesOwnRoleShapes()
    {
        // Writing an assistant turn as content parts would read back through Continue's own UI as
        // an empty message, because it expects a string there.
        var paths = CreateHome();
        var conversation = new PortableConversation(
            ConversationAgent.Grok, "source", "shapes", null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new PortableTurn(ConversationRole.User, "question"), new PortableTurn(ConversationRole.Assistant, "answer")]);

        var result = await new ContinueConversationWriter(paths).WriteAsync(conversation, CancellationToken.None);

        var session = (JsonObject)JsonNode.Parse(File.ReadAllText(result.NativePath))!;
        var history = (JsonArray)session["history"]!;
        Assert.IsType<JsonArray>(((JsonObject)history[0]!["message"]!)["content"]);
        Assert.Equal("answer", (string?)((JsonObject)history[1]!["message"]!)["content"]);
        Assert.Equal(string.Empty, (string?)session["workspaceDirectory"]);
    }

    [Fact]
    public async Task AWrittenSessionNeverReusesTheSourceId()
    {
        var paths = CreateHome();
        var reused = Guid.Parse(SessionId);
        var attempts = 0;
        var conversation = new PortableConversation(
            ConversationAgent.Claude, SessionId, "copy", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new PortableTurn(ConversationRole.User, "question")]);

        var result = await new ContinueConversationWriter(paths, () => attempts++ == 0 ? reused : Guid.NewGuid())
            .WriteAsync(conversation, CancellationToken.None);

        Assert.NotEqual(SessionId, result.SessionId);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void AMissingWorkingDirectoryStaysAString(string? input, string expected) =>
        Assert.Equal(expected, ContinueConversationWriter.ToWorkspaceUri(input));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private ContinuePaths CreateHome()
    {
        var home = Path.Combine(root, Guid.NewGuid().ToString("N"), ".continue");
        var sessions = Path.Combine(home, "sessions");
        Directory.CreateDirectory(sessions);
        return new ContinuePaths(home, sessions);
    }

    private static string WriteSession(ContinuePaths paths, string sessionId, string title, JsonArray history,
        string workspace = "")
    {
        var path = Path.Combine(paths.Sessions, sessionId + ".json");
        File.WriteAllText(path, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["title"] = title,
            ["workspaceDirectory"] = workspace,
            ["history"] = history,
            ["mode"] = "agent"
        }.ToJsonString());
        return path;
    }

    private static JsonArray SimpleHistory() => new(
        Turn("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "question" } }),
        Turn("assistant", "answer"));

    private static JsonObject Turn(string role, JsonNode? content) => new()
    {
        ["message"] = new JsonObject { ["role"] = role, ["content"] = content },
        ["contextItems"] = new JsonArray()
    };
}
