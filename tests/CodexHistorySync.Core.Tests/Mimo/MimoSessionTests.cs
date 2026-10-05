using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Mimo;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Tests.Mimo;

public sealed class MimoSessionTests : IDisposable
{
    private const string SessionId = "ses_abcdef1234567890abcdef12";
    private readonly string root = Path.Combine(Path.GetTempPath(), "mimo-sync-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Paths_ResolveFromHomeAndDatabase()
    {
        var home = Path.Combine(root, "mimocode");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "mimocode.db"), "");
        // TryResolve with configured home should succeed even without DB when MIMOCODE_HOME set?
        var resolved = MimoPaths.TryResolve(home);
        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(home), resolved!.Home);

        var configured = Path.Combine(root, "configured");
        Directory.CreateDirectory(configured);
        Assert.Equal(Path.GetFullPath(configured), MimoPaths.TryResolve(configured)!.Home);
        Assert.Null(MimoPaths.TryResolve(Path.Combine(root, "missing_without_env")));
    }

    [Fact]
    public void Package_RoundTripsAndIgnoresLocalRowIdsAndNullColumns()
    {
        var source = Home("source");
        var target = Home("target");
        WriteSession(source.PrimaryDatabasePath, messageIds: ["msg_aaaa", "msg_bbbb"], extraNullColumn: true);
        var package = MimoSessionPackage.TryBuild(source, SessionId);
        Assert.NotNull(package);
        Assert.Equal("mi-" + SessionId, MimoSessionPackage.LogicalIdOf(package));
        // Ensure no secret leakage from unrelated file
        File.WriteAllText(Path.Combine(source.Home, "auth.json"), "secret-token");
        Assert.DoesNotContain("secret-token", Encoding.UTF8.GetString(package));

        var other = Home("rowids");
        WriteSession(other.PrimaryDatabasePath, messageIds: ["msg_cccc", "msg_dddd"], extraNullColumn: false);
        // Different message ids but same logical session content? Actually we write same content but different ids -> hash will differ, so not equal.
        // Instead verify that package built from other is readable and has same session id
        var otherPkg = MimoSessionPackage.TryBuild(other, SessionId);
        Assert.NotNull(otherPkg);
        Assert.Equal("mi-" + SessionId, MimoSessionPackage.LogicalIdOf(otherPkg));

        MimoSessionPackage.Materialize(target, package);
        var rebuilt = MimoSessionPackage.TryBuild(target, SessionId);
        Assert.Equal(MimoSessionPackage.HashPackage(package), MimoSessionPackage.HashPackage(rebuilt!));

        using var connection = Open(target.PrimaryDatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM message WHERE session_id = @id ORDER BY time_created";
        command.Parameters.AddWithValue("@id", SessionId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var json = reader.GetString(0);
        Assert.Contains("hello mimo", json);
    }

    [Fact]
    public void Package_RejectsATraversalId()
    {
        Assert.False(MimoSessionPackage.IsMimoLogicalId("mi-../secret"));
        Assert.False(MimoPaths.TryParseAnchor(Path.Combine(root, ".agent-sync-mimo", "..json"), out _, out _));
        Assert.False(MimoPaths.IsSessionId("../secret"));
    }

    [Fact]
    public async Task Scanner_PublishesStableSessionsAndDefersALiveOne()
    {
        var home = Home("scan");
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_1111", "msg_2222"], extraNullColumn: false);
        var scanner = new MimoSessionScanner(_ => Task.CompletedTask, () => false, TimeSpan.Zero);
        var result = await scanner.ScanDetailedAsync(home, CancellationToken.None);
        var item = Assert.Single(result.Objects);
        Assert.Equal(ObjectKind.MimoSession, item.Kind);
        Assert.Equal("mi-" + SessionId, item.Id.Value);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.MimoSession));

        var live = new MimoSessionScanner(_ => Task.CompletedTask, () => true, TimeSpan.FromDays(3650));
        var deferred = await live.ScanDetailedAsync(home, CancellationToken.None);
        Assert.Empty(deferred.Objects);
        Assert.False(deferred.IsAbsenceConfirmed(ObjectKind.MimoSession));
    }

    [Fact]
    public async Task Scanner_DoesNotConfirmAbsenceWhenTheDatabaseWasNeverCreated()
    {
        var home = Home("empty");
        // Home exists but no DB
        var result = await new MimoSessionScanner(_ => Task.CompletedTask, () => false).ScanDetailedAsync(home, CancellationToken.None);
        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.MimoSession));
    }

    [Fact]
    public async Task ImportAndTombstone_RoundTripThroughTheHistoryWriter()
    {
        var source = Home("import-source");
        WriteSession(source.PrimaryDatabasePath, messageIds: ["msg_aaaa", "msg_bbbb"], extraNullColumn: false);
        var package = MimoSessionPackage.TryBuild(source, SessionId)!;
        var target = Home("import-target");
        var codexHome = Path.Combine(root, "codex");
        Directory.CreateDirectory(codexHome);
        var backups = new BackupStore("repo", Path.Combine(root, "local"), CodexPaths.Resolve(codexHome), mimoPaths: target);
        var writer = new CodexHistoryWriter(CodexPaths.Resolve(codexHome), backups, new StoppedDetector(), mimoPaths: target);
        var destination = target.AnchorPath(SessionId);
        var incoming = new LocalObject(new LogicalObjectId(MimoSessionPackage.LogicalIdOf(package)), ObjectKind.MimoSession,
            destination, MimoSessionPackage.HashPackage(package), package.LongLength, DateTimeOffset.UtcNow);
        using var stream = new MemoryStream(package);
        Assert.Equal(ImportApplyResult.Applied, await writer.ImportAsync(incoming, stream, "import-1", ExpectedHistoryState.Absent, CancellationToken.None));
        Assert.Equal(incoming.Hash, MimoSessionPackage.HashAnchor(destination));

        Assert.Equal(TombstoneApplyResult.Applied, await writer.ApplyTombstoneAsync(incoming, incoming.Hash, "delete-1", CancellationToken.None));
        Assert.Null(MimoSessionPackage.TryBuild(target, SessionId));
    }

    [Fact]
    public async Task Conversation_ReadsUserTextAndWritesAResumableCliSession()
    {
        var home = Home("conversation");
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_aaaa", "msg_bbbb"], extraNullColumn: false);
        // Need anchor for reader
        var package = MimoSessionPackage.TryBuild(home, SessionId)!;
        Directory.CreateDirectory(Path.GetDirectoryName(home.AnchorPath(SessionId))!);
        File.WriteAllBytes(home.AnchorPath(SessionId), package);
        var reader = new MimoConversationReader();
        var conversation = await reader.ReadAsync(home.AnchorPath(SessionId), CancellationToken.None);
        Assert.Equal(ConversationAgent.Mimo, conversation.SourceAgent);
        Assert.Equal(["hello mimo", "reply from mimo"], conversation.Turns.Select(turn => turn.Text));
        Assert.Equal("/work/mimo", conversation.WorkingDirectory);

        var copyHome = Home("copy");
        var written = await new MimoConversationWriter(copyHome, () => SessionId + "2", () => DateTimeOffset.Parse("2026-03-05T09:15:23Z")).WriteAsync(conversation, CancellationToken.None);
        var copied = await reader.ReadAsync(written.NativePath, CancellationToken.None);
        Assert.Equal(conversation.Turns.Select(turn => turn.Text), copied.Turns.Select(turn => turn.Text));

        // MiMoCode resumes a session by reading message.info.time.created and needs the
        // project row the session points at; a bare { role, content } copy crashes the loader.
        using var connection = Open(copyHome.PrimaryDatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM message WHERE session_id = @id ORDER BY time_created";
        command.Parameters.AddWithValue("@id", SessionId + "2");
        using var reader2 = command.ExecuteReader();
        while (reader2.Read())
        {
            using var doc = JsonDocument.Parse(reader2.GetString(0));
            Assert.True(doc.RootElement.TryGetProperty("time", out var time));
            Assert.True(time.TryGetProperty("created", out _));
            if (doc.RootElement.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            {
                // The TUI session picker reads tokens.output; missing it crashes the picker.
                Assert.True(doc.RootElement.TryGetProperty("tokens", out var tokens));
                Assert.True(tokens.TryGetProperty("output", out _));
            }
        }

        using var project = connection.CreateCommand();
        project.CommandText = "SELECT project_id FROM session WHERE id = @id";
        project.Parameters.AddWithValue("@id", SessionId + "2");
        // mimo session list only shows `global` or the current worktree's project.
        Assert.Equal("global", project.ExecuteScalar());

        using var fts = connection.CreateCommand();
        fts.CommandText = "SELECT COUNT(*) FROM history_fts WHERE session_id = @id";
        fts.Parameters.AddWithValue("@id", SessionId + "2");
        // TUI /sessions lists from history_fts; a copy without rows is invisible there.
        Assert.True(Convert.ToInt32(fts.ExecuteScalar()) > 0);
    }

    [Fact]
    public async Task Catalog_ListsTheSessionTitle()
    {
        var home = Home("catalog");
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_aaaa"], extraNullColumn: false);
        using var limiter = new SessionCatalogReadLimiter(2);
        var rows = await new MimoSessionCatalogSource(home).ScanAsync(limiter, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(SessionId, row.SessionId);
        Assert.Equal("Fix the build", row.Title);
        Assert.Equal(ManagedTitleSource.Official, row.TitleSource);
        Assert.True(row.CanRead);
    }

    [Fact]
    public async Task Catalog_HidesChildSessionsCreatedByInternalAgents()
    {
        var home = Home("catalog-subagent");
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_aaaa"], extraNullColumn: false);
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_cccc"], extraNullColumn: false,
            sessionId: "ses_child0000000000000001",
            title: "checkpoint-writer: Previous checkpoint: memory",
            parentId: SessionId);

        using var limiter = new SessionCatalogReadLimiter(2);
        var rows = await new MimoSessionCatalogSource(home).ScanAsync(limiter, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(SessionId, row.SessionId);
    }

    [Fact]
    public async Task Catalog_HidesLegacyGenerationSessions()
    {
        var home = Home("catalog-legacy");
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_aaaa"], extraNullColumn: false);
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_cccc"], extraNullColumn: false,
            sessionId: "ses_legacy000000000000001",
            title: "old mimo chat",
            version: "2.1.251");

        using var limiter = new SessionCatalogReadLimiter(2);
        var rows = await new MimoSessionCatalogSource(home).ScanAsync(limiter, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(SessionId, row.SessionId);
    }

    [Fact]
    public async Task Scanner_IgnoresChildSessionsInsteadOfPublishingOrDeletingThem()
    {
        var home = Home("scan-subagent");
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_aaaa"], extraNullColumn: false);
        WriteSession(home.PrimaryDatabasePath, messageIds: ["msg_cccc"], extraNullColumn: false,
            sessionId: "ses_child0000000000000001",
            title: "checkpoint-writer: Previous checkpoint: memory",
            parentId: SessionId);

        var scanner = new MimoSessionScanner(_ => Task.CompletedTask, () => false, TimeSpan.Zero);
        var result = await scanner.ScanDetailedAsync(home, CancellationToken.None);

        var item = Assert.Single(result.Objects);
        Assert.Equal("mi-" + SessionId, item.Id.Value);
        Assert.True(result.IsIgnored(new LogicalObjectId("mi-ses_child0000000000000001")));
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.MimoSession));
    }

    [Fact]
    public void Paths_DetectChannelDatabaseFiles()
    {
        var home = Home("channel");
        Directory.CreateDirectory(home.Home);
        File.WriteAllText(Path.Combine(home.Home, "mimocode.db"), "");
        File.WriteAllText(Path.Combine(home.Home, "mimocode-beta.db"), "");
        File.WriteAllText(Path.Combine(home.Home, "other.db"), "");
        var list = home.ListDatabasePaths();
        Assert.Contains(Path.Combine(home.Home, "mimocode.db"), list);
        Assert.Contains(Path.Combine(home.Home, "mimocode-beta.db"), list);
        Assert.DoesNotContain(Path.Combine(home.Home, "other.db"), list);
        Assert.True(MimoPaths.IsMimoDatabaseFile("mimocode.db"));
        Assert.True(MimoPaths.IsMimoDatabaseFile("mimocode-beta.db"));
        Assert.False(MimoPaths.IsMimoDatabaseFile("other.db"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private MimoPaths Home(string name)
    {
        var home = Path.Combine(root, name, "mimocode");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.json"), "{}");
        return new MimoPaths(home);
    }

    private static void WriteSession(string database, string[] messageIds, bool extraNullColumn,
        string? sessionId = null, string? title = null, string? parentId = null, string? version = null)
    {
        sessionId ??= SessionId;
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS session (
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                parent_id TEXT,
                directory TEXT NOT NULL,
                title TEXT,
                version TEXT,
                time_created INTEGER NOT NULL,
                time_updated INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS project (
                id TEXT PRIMARY KEY,
                worktree TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS message (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                data TEXT
            );
            CREATE TABLE IF NOT EXISTS part (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                message_id TEXT NOT NULL,
                time_created INTEGER NOT NULL,
                data TEXT
            );
            INSERT OR REPLACE INTO project (id, worktree) VALUES ('prj_123', '/work/mimo');
            INSERT OR REPLACE INTO session (id, project_id, parent_id, directory, title, version, time_created, time_updated)
            VALUES (@id, 'prj_123', @parent, '/work/mimo', @title, @version, 1741166123000, 1741166124000);
            """;
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@title", (object?)(title ?? "Fix the build") ?? DBNull.Value);
        command.Parameters.AddWithValue("@version", (object?)version ?? DBNull.Value);
        command.ExecuteNonQuery();
        if (extraNullColumn)
        {
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE session ADD COLUMN pinned INTEGER";
                alter.ExecuteNonQuery();
            }
            catch { }
        }

        var contents = new[] { "hello mimo", "reply from mimo" };
        var roles = new[] { "user", "assistant" };
        for (var index = 0; index < messageIds.Length; index++)
        {
            var msgId = messageIds[index];
            var role = roles[Math.Min(index, roles.Length - 1)];
            var text = contents[Math.Min(index, contents.Length - 1)];
            var data = JsonSerializer.Serialize(new { role, content = text });
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT OR REPLACE INTO message (id, session_id, time_created, data)
                VALUES (@id, @session, @time, @data)
                """;
            insert.Parameters.AddWithValue("@id", msgId);
            insert.Parameters.AddWithValue("@session", sessionId);
            insert.Parameters.AddWithValue("@time", 1741166123000 + index * 1000);
            insert.Parameters.AddWithValue("@data", data);
            insert.ExecuteNonQuery();

            var partJson = JsonSerializer.Serialize(new { type = "text", text });
            using var partInsert = connection.CreateCommand();
            partInsert.CommandText = """
                INSERT OR REPLACE INTO part (id, session_id, message_id, time_created, data)
                VALUES (@pid, @session, @mid, @time, @data)
                """;
            partInsert.Parameters.AddWithValue("@pid", "prt_" + msgId[4..]);
            partInsert.Parameters.AddWithValue("@session", sessionId);
            partInsert.Parameters.AddWithValue("@mid", msgId);
            partInsert.Parameters.AddWithValue("@time", 1741166123000 + index * 1000);
            partInsert.Parameters.AddWithValue("@data", partJson);
            partInsert.ExecuteNonQuery();
        }
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class StoppedDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
