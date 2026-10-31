using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHistorySync.Core.Antigravity;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Tests.Antigravity;

/// <summary>
/// Ephemeral test fixture setting up an isolated Antigravity home layout with SQLite databases,
/// Protobuf step payloads, transcripts, and annotations.
/// </summary>
public sealed class AntigravityTestFixture : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Root { get; }
    public string Home { get; }
    public AntigravityPaths Paths { get; }

    public AntigravityTestFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "antigravity-core-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "antigravity-cli");
        Directory.CreateDirectory(Home);
        Paths = new AntigravityPaths(Home);
        Directory.CreateDirectory(Paths.ConversationsDirectory);
        Directory.CreateDirectory(Paths.AnnotationsDirectory);
        Directory.CreateDirectory(Paths.BrainDirectory);
        Directory.CreateDirectory(Paths.AnchorRoot);
    }

    public void CreateSummariesDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={Paths.SummariesDatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS conversation_summaries (
                conversation_id TEXT PRIMARY KEY,
                parent_conversation_id TEXT,
                nesting_depth INTEGER,
                agent_name TEXT,
                title TEXT,
                status TEXT,
                workspace_uris TEXT,
                created_at INTEGER,
                updated_at INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_parent_conv_id ON conversation_summaries(parent_conversation_id);
            CREATE INDEX IF NOT EXISTS idx_agent_name ON conversation_summaries(agent_name);
            """;
        command.ExecuteNonQuery();
    }

    public void InsertSummaryRow(
        string sessionId,
        string title,
        string? parentId = null,
        int nestingDepth = 0,
        string agentName = "teamwork",
        string status = "COMPLETED",
        string? workspaceUri = null,
        long? createdAt = null,
        long? updatedAt = null)
    {
        CreateSummariesDatabase();
        using var connection = new SqliteConnection($"Data Source={Paths.SummariesDatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO conversation_summaries (
                conversation_id, parent_conversation_id, nesting_depth, agent_name, title, status, workspace_uris, created_at, updated_at
            ) VALUES (
                @id, @parentId, @depth, @agent, @title, @status, @uris, @created, @updated
            );
            """;
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@parentId", (object?)parentId ?? string.Empty);
        command.Parameters.AddWithValue("@depth", nestingDepth);
        command.Parameters.AddWithValue("@agent", agentName);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@uris", workspaceUri is null ? "[]" : JsonSerializer.Serialize(new[] { workspaceUri }));
        command.Parameters.AddWithValue("@created", createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@updated", updatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }

    public void CreateConversationDatabase(string sessionId)
    {
        var dbPath = Paths.ConversationDatabasePath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS trajectory_meta (
                trajectory_id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                trajectory_type INTEGER,
                total_steps INTEGER
            );
            CREATE TABLE IF NOT EXISTS steps (
                idx INTEGER PRIMARY KEY,
                step_type INTEGER NOT NULL,
                status INTEGER NOT NULL,
                step_payload BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        using var metaCommand = connection.CreateCommand();
        metaCommand.CommandText =
            """
            INSERT OR REPLACE INTO trajectory_meta (trajectory_id, conversation_id, trajectory_type, total_steps)
            VALUES (@trajId, @convId, 4, 0);
            """;
        metaCommand.Parameters.AddWithValue("@trajId", "traj-" + sessionId);
        metaCommand.Parameters.AddWithValue("@convId", sessionId);
        metaCommand.ExecuteNonQuery();
    }

    public void InsertUserStep(
        string sessionId,
        int idx,
        string text,
        DateTimeOffset? timestamp = null,
        string? workspaceUri = null)
    {
        CreateConversationDatabase(sessionId);
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var (_, stepPayload) = AntigravityProtobuf.EncodeUserStep(
            idx,
            text,
            ts,
            sessionId,
            cascadeId: "cas-" + sessionId,
            turnId: "turn-" + idx,
            workspaceUri: workspaceUri,
            spanHex: "0123456789abcdef");

        InsertStepRaw(sessionId, idx, AntigravityProtobuf.StepTypeUserInput, AntigravityProtobuf.StepStatusDone, stepPayload);
    }

    public void InsertAssistantStep(
        string sessionId,
        int idx,
        string text,
        DateTimeOffset? timestamp = null)
    {
        CreateConversationDatabase(sessionId);
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var (_, stepPayload) = AntigravityProtobuf.EncodeAssistantStep(
            idx,
            text,
            ts,
            sessionId,
            cascadeId: "cas-" + sessionId,
            turnId: "turn-" + idx,
            spanHex: "0123456789abcdef");

        InsertStepRaw(sessionId, idx, AntigravityProtobuf.StepTypePlannerResponse, AntigravityProtobuf.StepStatusDone, stepPayload);
    }

    public void InsertStepRaw(string sessionId, int idx, int stepType, int status, byte[] payload)
    {
        CreateConversationDatabase(sessionId);
        var dbPath = Paths.ConversationDatabasePath(sessionId);
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO steps (idx, step_type, status, step_payload)
            VALUES (@idx, @type, @status, @payload);
            UPDATE trajectory_meta SET total_steps = (SELECT COUNT(*) FROM steps) WHERE conversation_id = @convId;
            """;
        command.Parameters.AddWithValue("@idx", idx);
        command.Parameters.AddWithValue("@type", stepType);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@convId", sessionId);
        command.ExecuteNonQuery();
    }

    public void WriteTranscript(string sessionId, IEnumerable<(string role, string text)> turns)
    {
        var brainDir = Paths.BrainSessionDirectory(sessionId);
        Directory.CreateDirectory(brainDir);
        var transcriptPath = Path.Combine(brainDir, "transcript.jsonl");

        using var writer = new StreamWriter(transcriptPath, append: false, Encoding.UTF8);
        var idx = 0;
        foreach (var (role, text) in turns)
        {
            var lineObj = new Dictionary<string, object>
            {
                ["step_index"] = idx++,
                ["source"] = role.ToUpperInvariant() == "USER" ? "USER" : "MODEL",
                ["type"] = role.ToUpperInvariant() == "USER" ? "USER_INPUT" : "PLANNER_RESPONSE",
                ["status"] = "DONE",
                ["content"] = text
            };
            writer.WriteLine(JsonSerializer.Serialize(lineObj, JsonOptions));
        }
    }

    public void WriteAnnotation(string sessionId, string annotationText)
    {
        var annotationPath = Paths.AnnotationPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(annotationPath)!);
        File.WriteAllText(annotationPath, annotationText, Encoding.UTF8);
    }

    public void WriteAnchor(string sessionId, string content)
    {
        var anchorPath = Paths.AnchorPath(sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(anchorPath)!);
        File.WriteAllText(anchorPath, content, Encoding.UTF8);
    }

    public void BuildStandardSession(
        string sessionId,
        string title,
        string userPrompt,
        string assistantResponse,
        string? workspaceUri = null,
        DateTimeOffset? timestamp = null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow;
        var unixSec = ts.ToUnixTimeSeconds();

        InsertSummaryRow(
            sessionId,
            title,
            parentId: null,
            nestingDepth: 0,
            agentName: "teamwork",
            status: "COMPLETED",
            workspaceUri: workspaceUri ?? "file:///C:/Repos/agent-sync",
            createdAt: unixSec,
            updatedAt: unixSec + 60);

        InsertUserStep(sessionId, idx: 0, text: userPrompt, timestamp: ts, workspaceUri: workspaceUri);
        InsertAssistantStep(sessionId, idx: 1, text: assistantResponse, timestamp: ts.AddSeconds(30));

        WriteTranscript(sessionId, new[]
        {
            ("USER", userPrompt),
            ("ASSISTANT", assistantResponse)
        });

        WriteAnnotation(sessionId, $"note: \"Standard session {sessionId}\"\n");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
