using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Hermes;
using CodexHistorySync.Core.Kimi;
using CodexHistorySync.Core.Mimo;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityConversationWriterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workingDir;
    private readonly AntigravityPaths _antigravityPaths;

    public AntigravityConversationWriterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "agy-conv-writer-" + Guid.NewGuid().ToString("N"));
        _workingDir = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(_workingDir);
        var agyHome = Path.Combine(_tempRoot, "antigravity");
        Directory.CreateDirectory(agyHome);
        _antigravityPaths = new AntigravityPaths(agyHome);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task WriteAsync_WithUserAndAssistantTurns_CreatesValidAntigravitySession()
    {
        var source = new PortableConversation(
            ConversationAgent.Antigravity,
            "orig-session",
            "Speed of Light Inquiry",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "What is the speed of light?"),
                new PortableTurn(ConversationRole.Assistant, "Approximately 299,792,458 m/s in vacuum."),
                new PortableTurn(ConversationRole.User, "Thank you!")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(AntigravityPaths.IsSessionId(result.SessionId));
        Assert.Equal(_antigravityPaths.AnchorPath(result.SessionId), result.NativePath);
        Assert.True(File.Exists(result.NativePath));
        Assert.True(File.Exists(_antigravityPaths.ConversationDatabasePath(result.SessionId)));
        Assert.True(File.Exists(_antigravityPaths.TranscriptPath(result.SessionId)));

        // Verify summary
        var summary = AntigravityDatabase.ReadSummary(_antigravityPaths.ConversationSummariesDbPath, result.SessionId);
        Assert.NotNull(summary);
        Assert.Equal("Speed of Light Inquiry", summary.Title);
        Assert.Equal("CASCADE_RUN_STATUS_IDLE", summary.Status);
        Assert.Null(summary.AgentName);

        // Read back with AntigravityConversationReader
        var reader = new AntigravityConversationReader();
        var reread = await reader.ReadAsync(result.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Antigravity, reread.SourceAgent);
        Assert.Equal(result.SessionId, reread.SourceSessionId);
        Assert.Equal("Speed of Light Inquiry", reread.Title);
        Assert.Equal(3, reread.Turns.Count);
        Assert.Equal(source.Turns[0].Role, reread.Turns[0].Role);
        Assert.Equal(source.Turns[0].Text, reread.Turns[0].Text);
        Assert.Equal(source.Turns[1].Role, reread.Turns[1].Role);
        Assert.Equal(source.Turns[1].Text, reread.Turns[1].Text);
        Assert.Equal(source.Turns[2].Role, reread.Turns[2].Role);
        Assert.Equal(source.Turns[2].Text, reread.Turns[2].Text);
    }

    [Fact]
    public async Task WriteAsync_WithEmptyConversation_ThrowsArgumentException()
    {
        var source = new PortableConversation(
            ConversationAgent.Antigravity,
            "orig-empty",
            "Empty",
            _workingDir,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task WriteAsync_GeneratesDeterministicGuidAndTruncatesLongTitle()
    {
        var fixedId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var longTitle = new string('A', 120);

        var source = new PortableConversation(
            ConversationAgent.Antigravity,
            "source-id",
            longTitle,
            _workingDir,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Short message")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: () => fixedId);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        Assert.Equal(fixedId, result.SessionId);

        var summary = AntigravityDatabase.ReadSummary(_antigravityPaths.ConversationSummariesDbPath, result.SessionId);
        Assert.NotNull(summary);
        Assert.Equal(80, summary.Title.Length);
        Assert.Equal(new string('A', 80), summary.Title);
    }

    [Theory]
    [InlineData(ConversationAgent.Claude)]
    [InlineData(ConversationAgent.Codex)]
    [InlineData(ConversationAgent.Grok)]
    [InlineData(ConversationAgent.Hermes)]
    [InlineData(ConversationAgent.Kimi)]
    [InlineData(ConversationAgent.Mimo)]
    public async Task ConvertToAntigravity_FromSupportedAgents_PreservesTurns(ConversationAgent agent)
    {
        var source = new PortableConversation(
            agent,
            "agent-session-123",
            $"Cross Agent {agent} Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, $"Message from {agent} user"),
                new PortableTurn(ConversationRole.Assistant, $"Message from {agent} assistant"),
                new PortableTurn(ConversationRole.User, "Follow-up question")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        var reader = new AntigravityConversationReader();
        var converted = await reader.ReadAsync(result.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Antigravity, converted.SourceAgent);
        Assert.Equal(3, converted.Turns.Count);
        Assert.Equal(source.Turns[0].Role, converted.Turns[0].Role);
        Assert.Equal(source.Turns[0].Text, converted.Turns[0].Text);
        Assert.Equal(source.Turns[1].Role, converted.Turns[1].Role);
        Assert.Equal(source.Turns[1].Text, converted.Turns[1].Text);
        Assert.Equal(source.Turns[2].Role, converted.Turns[2].Role);
        Assert.Equal(source.Turns[2].Text, converted.Turns[2].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToClaude_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Claude",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Claude from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Claude.")
            ]);

        var claudeHome = Path.Combine(_tempRoot, "claude");
        var claudeProjects = Path.Combine(claudeHome, "projects");
        Directory.CreateDirectory(claudeProjects);
        var claudePaths = new ClaudePaths(claudeHome, claudeProjects);
        var claudeWriter = new ClaudeConversationWriter(claudePaths);

        var writeResult = await claudeWriter.WriteAsync(agyConv, CancellationToken.None);
        var claudeReader = new ClaudeConversationReader();
        var readBack = await claudeReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Claude, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToGrok_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Grok",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Grok from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Grok.")
            ]);

        var grokHome = Path.Combine(_tempRoot, "grok");
        var grokSessions = Path.Combine(grokHome, "sessions");
        Directory.CreateDirectory(grokSessions);
        var grokPaths = new GrokPaths(grokHome, grokSessions);
        var grokWriter = new GrokConversationWriter(grokPaths);

        var writeResult = await grokWriter.WriteAsync(agyConv, CancellationToken.None);
        var grokReader = new GrokConversationReader();
        var readBack = await grokReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Grok, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToHermes_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Hermes",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Hermes from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Hermes.")
            ]);

        var hermesHome = Path.Combine(_tempRoot, "hermes");
        Directory.CreateDirectory(hermesHome);
        var hermesPaths = new HermesPaths(hermesHome);
        var hermesWriter = new HermesConversationWriter(hermesPaths);

        var writeResult = await hermesWriter.WriteAsync(agyConv, CancellationToken.None);
        var hermesReader = new HermesConversationReader();
        var readBack = await hermesReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Hermes, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToKimi_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Kimi",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Kimi from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Kimi.")
            ]);

        var kimiHome = Path.Combine(_tempRoot, "kimi");
        var kimiSessions = Path.Combine(kimiHome, "sessions");
        Directory.CreateDirectory(kimiSessions);
        var kimiPaths = new KimiPaths(kimiHome, kimiSessions);
        var kimiWriter = new KimiConversationWriter(kimiPaths);

        var writeResult = await kimiWriter.WriteAsync(agyConv, CancellationToken.None);
        var kimiReader = new KimiConversationReader();
        var readBack = await kimiReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Kimi, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task ConvertFromAntigravity_ToMimo_PreservesTurns()
    {
        var agyConv = new PortableConversation(
            ConversationAgent.Antigravity,
            "agy-session",
            "Antigravity to Mimo",
            _workingDir,
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello Mimo from Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Hello! I am Mimo.")
            ]);

        var mimoHome = Path.Combine(_tempRoot, "mimo");
        Directory.CreateDirectory(mimoHome);
        var mimoPaths = new MimoPaths(mimoHome);
        var mimoWriter = new MimoConversationWriter(mimoPaths);

        var writeResult = await mimoWriter.WriteAsync(agyConv, CancellationToken.None);
        var mimoReader = new MimoConversationReader();
        var readBack = await mimoReader.ReadAsync(writeResult.NativePath, CancellationToken.None);

        Assert.Equal(ConversationAgent.Mimo, readBack.SourceAgent);
        Assert.Equal(agyConv.Turns.Count, readBack.Turns.Count);
        Assert.Equal(agyConv.Turns[0].Text, readBack.Turns[0].Text);
        Assert.Equal(agyConv.Turns[1].Text, readBack.Turns[1].Text);
    }

    [Fact]
    public async Task WriteAsync_AndMaterialize_WithProductionNotNullSchema_PopulatesAllNativeTablesAndBlobs()
    {
        using (var conn = AntigravityDatabase.OpenConnection(_antigravityPaths.ConversationSummariesDbPath, write: true))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE `conversation_summaries` (
                    `conversation_id` text,
                    `title` text NOT NULL DEFAULT "",
                    `preview` text NOT NULL DEFAULT "",
                    `step_count` integer NOT NULL DEFAULT 0,
                    `last_modified_time` datetime NOT NULL,
                    `workspace_uris` text NOT NULL,
                    `status` text NOT NULL DEFAULT "",
                    `source` text NOT NULL DEFAULT "",
                    `project_id` text NOT NULL DEFAULT "",
                    `agent_name` text NOT NULL DEFAULT "",
                    `parent_conversation_id` text NOT NULL DEFAULT "",
                    `nesting_depth` integer NOT NULL DEFAULT 0,
                    `battle_id` text NOT NULL DEFAULT "",
                    `winning_conversation_id` text NOT NULL DEFAULT "",
                    `not_fully_idle` numeric NOT NULL DEFAULT false,
                    `killed` numeric NOT NULL DEFAULT false,
                    `last_user_input_time` datetime NOT NULL,
                    `last_user_input_step_index` integer NOT NULL DEFAULT -1,
                    `app_data_dir` text NOT NULL DEFAULT "",
                    `raw_summary` blob,
                    `group_id` text NOT NULL DEFAULT "",
                    PRIMARY KEY (`conversation_id`)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        var source = new PortableConversation(
            ConversationAgent.Codex,
            "codex-src-1",
            "Production Schema Conversion",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Convert this Codex session to Antigravity"),
                new PortableTurn(ConversationRole.Assistant, "Converted cleanly with full native schema.")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        using (var sumConn = AntigravityDatabase.OpenConnection(_antigravityPaths.ConversationSummariesDbPath, write: false))
        using (var sumCmd = sumConn.CreateCommand())
        {
            sumCmd.CommandText = "SELECT step_count, status, agent_name, parent_conversation_id, last_user_input_step_index, length(raw_summary) FROM conversation_summaries WHERE conversation_id = @id;";
            sumCmd.Parameters.AddWithValue("@id", result.SessionId);
            using var reader = sumCmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal("CASCADE_RUN_STATUS_IDLE", reader.GetString(1));
            Assert.Equal(string.Empty, reader.GetString(2));
            Assert.Equal(string.Empty, reader.GetString(3));
            Assert.Equal(0, reader.GetInt32(4));
            Assert.True(reader.GetInt32(5) > 0);
        }

        using (var convConn = AntigravityDatabase.OpenConnection(_antigravityPaths.ConversationDatabasePath(result.SessionId), write: false))
        {
            using (var metaCmd = convConn.CreateCommand())
            {
                metaCmd.CommandText = "SELECT cascade_id, trajectory_type, source FROM trajectory_meta LIMIT 1;";
                using var r = metaCmd.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal(result.SessionId, r.GetString(0));
                Assert.Equal(4, r.GetInt32(1));
                Assert.Equal(17, r.GetInt32(2));
            }

            using (var blobCmd = convConn.CreateCommand())
            {
                blobCmd.CommandText = "SELECT length(data) FROM trajectory_metadata_blob WHERE id = 'main';";
                Assert.True(Convert.ToInt32(blobCmd.ExecuteScalar()) > 0);
            }

            using (var stepCmd = convConn.CreateCommand())
            {
                stepCmd.CommandText = "SELECT status, length(metadata), length(step_payload) FROM steps ORDER BY idx ASC;";
                using var sr = stepCmd.ExecuteReader();
                var count = 0;
                while (sr.Read())
                {
                    count++;
                    Assert.Equal(AntigravityProtobuf.StepStatusDone, sr.GetInt32(0));
                    Assert.True(sr.GetInt32(1) > 0);
                    Assert.True(sr.GetInt32(2) > 0);
                }
                Assert.Equal(2, count);
            }
        }

        var nativeTranscript = Path.Combine(_antigravityPaths.BrainSessionDirectory(result.SessionId), ".system_generated", "logs", "transcript.jsonl");
        Assert.True(File.Exists(nativeTranscript));

        // Verify sync Materialize into a second production NOT NULL database also succeeds
        var secondHome = Path.Combine(_tempRoot, "antigravity-second");
        Directory.CreateDirectory(secondHome);
        var secondPaths = new AntigravityPaths(secondHome);
        using (var conn2 = AntigravityDatabase.OpenConnection(secondPaths.ConversationSummariesDbPath, write: true))
        {
            AntigravityDatabase.EnsureSummariesTable(conn2);
        }

        var package = AntigravitySessionPackage.BuildFromLocal(_antigravityPaths, result.SessionId);
        AntigravitySessionPackage.Materialize(secondPaths, package);

        var materializedConv = await new AntigravityConversationReader().ReadAsync(secondPaths.AnchorPath(result.SessionId), CancellationToken.None);
        Assert.Equal(2, materializedConv.Turns.Count);
        Assert.Equal(source.Turns[0].Text, materializedConv.Turns[0].Text);
        Assert.Equal(source.Turns[1].Text, materializedConv.Turns[1].Text);
    }
}
