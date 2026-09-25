using System.Text;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Hermes;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Tests.Hermes;

public sealed class HermesSessionTests : IDisposable
{
    private const string SessionId = "20250305_091523_a1b2c3d4";
    private readonly string root = Path.Combine(Path.GetTempPath(), "hermes-sync-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Paths_PreferTheHomeThatHoldsState()
    {
        var modern = Path.Combine(root, "local", "hermes");
        var legacy = Path.Combine(root, "profile", ".hermes");
        Directory.CreateDirectory(modern);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "state.db"), "");
        Assert.Equal(Path.GetFullPath(legacy), HermesPaths.TryResolve(legacy)!.Home);

        var configured = Path.Combine(root, "configured");
        Directory.CreateDirectory(configured);
        Assert.Equal(Path.GetFullPath(configured), HermesPaths.TryResolve(configured)!.Home);
        Assert.Null(HermesPaths.TryResolve(Path.Combine(root, "missing")));
    }

    [Fact]
    public void Package_RoundTripsAndIgnoresLocalRowIdsAndNullColumns()
    {
        var source = Home("source");
        var target = Home("target");
        WriteSession(source.DatabasePath(HermesPaths.DefaultProfileName), messageIds: [1, 2], extraNullColumn: true);
        var package = HermesSessionPackage.TryBuild(source, HermesPaths.DefaultProfileName, SessionId);
        Assert.NotNull(package);
        Assert.Equal("he-default~" + SessionId, HermesSessionPackage.LogicalIdOf(package));
        Assert.DoesNotContain("config.yaml", Encoding.UTF8.GetString(package));
        Assert.DoesNotContain("secret-token", Encoding.UTF8.GetString(package));

        var other = Home("rowids");
        WriteSession(other.DatabasePath(HermesPaths.DefaultProfileName), messageIds: [40, 41], extraNullColumn: false);
        Assert.Equal(HermesSessionPackage.HashPackage(package), HermesSessionPackage.HashPackage(
            HermesSessionPackage.TryBuild(other, HermesPaths.DefaultProfileName, SessionId)!));

        HermesSessionPackage.Materialize(target, package);
        var rebuilt = HermesSessionPackage.TryBuild(target, HermesPaths.DefaultProfileName, SessionId);
        Assert.Equal(HermesSessionPackage.HashPackage(package), HermesSessionPackage.HashPackage(rebuilt!));

        using var connection = Open(target.DatabasePath(HermesPaths.DefaultProfileName));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM messages WHERE session_id = @id ORDER BY id";
        command.Parameters.AddWithValue("@id", SessionId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("hello hermes", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("reply", reader.GetString(0));
    }

    [Fact]
    public void Package_RejectsATraversalId()
    {
        Assert.False(HermesSessionPackage.IsHermesLogicalId("he-default~../secret"));
        Assert.False(HermesPaths.TryParseAnchor(Path.Combine(root, ".agent-sync-hermes", "default", "..json"), out _, out _, out _));
    }

    [Fact]
    public async Task Scanner_PublishesStableSessionsAndDefersALiveOne()
    {
        var home = Home("scan");
        WriteSession(home.DatabasePath(HermesPaths.DefaultProfileName), messageIds: [1, 2], extraNullColumn: false);
        var scanner = new HermesSessionScanner(_ => Task.CompletedTask, () => false, TimeSpan.Zero);
        var result = await scanner.ScanDetailedAsync(home, CancellationToken.None);
        var item = Assert.Single(result.Objects);
        Assert.Equal(ObjectKind.HermesSession, item.Kind);
        Assert.Equal("he-default~" + SessionId, item.Id.Value);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.HermesSession));

        var live = new HermesSessionScanner(_ => Task.CompletedTask, () => true, TimeSpan.FromDays(3650));
        var deferred = await live.ScanDetailedAsync(home, CancellationToken.None);
        Assert.Empty(deferred.Objects);
        Assert.False(deferred.IsAbsenceConfirmed(ObjectKind.HermesSession));
    }

    [Fact]
    public async Task Scanner_DoesNotConfirmAbsenceWhenTheDatabaseWasNeverCreated()
    {
        var home = Home("empty");
        var result = await new HermesSessionScanner(_ => Task.CompletedTask, () => false).ScanDetailedAsync(home, CancellationToken.None);
        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.HermesSession));
    }

    [Fact]
    public async Task ImportAndTombstone_RoundTripThroughTheHistoryWriter()
    {
        var source = Home("import-source");
        WriteSession(source.DatabasePath(HermesPaths.DefaultProfileName), messageIds: [1, 2], extraNullColumn: false);
        var package = HermesSessionPackage.TryBuild(source, HermesPaths.DefaultProfileName, SessionId)!;
        var target = Home("import-target");
        var codexHome = Path.Combine(root, "codex");
        Directory.CreateDirectory(codexHome);
        var backups = new BackupStore("repo", Path.Combine(root, "local"), CodexPaths.Resolve(codexHome), hermesPaths: target);
        var writer = new CodexHistoryWriter(CodexPaths.Resolve(codexHome), backups, new StoppedDetector(), hermesPaths: target);
        var destination = target.AnchorPath(HermesPaths.DefaultProfileName, SessionId);
        var incoming = new LocalObject(new LogicalObjectId(HermesSessionPackage.LogicalIdOf(package)), ObjectKind.HermesSession,
            destination, HermesSessionPackage.HashPackage(package), package.LongLength, DateTimeOffset.UtcNow);
        using var stream = new MemoryStream(package);
        Assert.Equal(ImportApplyResult.Applied, await writer.ImportAsync(incoming, stream, "import-1", ExpectedHistoryState.Absent, CancellationToken.None));
        Assert.Equal(incoming.Hash, HermesSessionPackage.HashAnchor(destination));

        Assert.Equal(TombstoneApplyResult.Applied, await writer.ApplyTombstoneAsync(incoming, incoming.Hash, "delete-1", CancellationToken.None));
        Assert.Null(HermesSessionPackage.TryBuild(target, HermesPaths.DefaultProfileName, SessionId));
    }

    [Fact]
    public async Task Conversation_ReadsUserTextAndWritesAResumableCliSession()
    {
        var home = Home("conversation");
        WriteSession(home.DatabasePath(HermesPaths.DefaultProfileName), messageIds: [1, 2], extraNullColumn: false);
        var reader = new HermesConversationReader();
        var conversation = await reader.ReadAsync(home.AnchorPath(HermesPaths.DefaultProfileName, SessionId), CancellationToken.None);
        Assert.Equal(ConversationAgent.Hermes, conversation.SourceAgent);
        Assert.Equal(["hello hermes", "reply"], conversation.Turns.Select(turn => turn.Text));
        Assert.Equal("C:/work/hermes", conversation.WorkingDirectory);

        var copyHome = Home("copy");
        var written = await new HermesConversationWriter(copyHome, () => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            () => DateTimeOffset.Parse("2026-03-05T09:15:23Z")).WriteAsync(conversation, CancellationToken.None);
        var copied = await reader.ReadAsync(written.NativePath, CancellationToken.None);
        Assert.Equal(conversation.Turns.Select(turn => turn.Text), copied.Turns.Select(turn => turn.Text));
        Assert.Equal("cli", ReadSource(copyHome, written.SessionId));
    }

    [Fact]
    public async Task Catalog_ListsTheSessionTitle()
    {
        var home = Home("catalog");
        WriteSession(home.DatabasePath(HermesPaths.DefaultProfileName), messageIds: [1], extraNullColumn: false);
        using var limiter = new SessionCatalogReadLimiter(2);
        var rows = await new HermesSessionCatalogSource(home).ScanAsync(limiter, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(SessionId, row.SessionId);
        Assert.Equal("Fix the build", row.Title);
        Assert.Equal(ManagedTitleSource.Official, row.TitleSource);
        Assert.True(row.CanRead);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private HermesPaths Home(string name)
    {
        var home = Path.Combine(root, name);
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.yaml"), "api_key: secret-token");
        return new HermesPaths(home);
    }

    private static void WriteSession(string database, int[] messageIds, bool extraNullColumn)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                title TEXT,
                cwd TEXT,
                started_at REAL NOT NULL,
                system_prompt TEXT,
                unused_note TEXT
            );
            CREATE TABLE messages (
                id INTEGER PRIMARY KEY,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT,
                timestamp REAL NOT NULL,
                reasoning TEXT
            );
            INSERT INTO sessions (id, source, title, cwd, started_at, system_prompt, unused_note)
            VALUES (@id, 'cli', 'Fix the build', 'C:/work/hermes', 1741166123.5, 'be careful', NULL);
            """;
        command.Parameters.AddWithValue("@id", SessionId);
        command.ExecuteNonQuery();
        if (extraNullColumn)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE sessions ADD COLUMN pinned INTEGER";
            alter.ExecuteNonQuery();
        }

        var contents = new[] { "hello hermes", "reply" };
        var roles = new[] { "user", "assistant" };
        for (var index = 0; index < messageIds.Length; index++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO messages (id, session_id, role, content, timestamp, reasoning)
                VALUES (@id, @session, @role, @content, @timestamp, 'hidden')
                """;
            insert.Parameters.AddWithValue("@id", messageIds[index]);
            insert.Parameters.AddWithValue("@session", SessionId);
            insert.Parameters.AddWithValue("@role", roles[Math.Min(index, roles.Length - 1)]);
            insert.Parameters.AddWithValue("@content", contents[Math.Min(index, contents.Length - 1)]);
            insert.Parameters.AddWithValue("@timestamp", 1741166123.5 + index);
            insert.ExecuteNonQuery();
        }
    }

    private static string? ReadSource(HermesPaths paths, string sessionId)
    {
        using var connection = Open(paths.DatabasePath(HermesPaths.DefaultProfileName));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source FROM sessions WHERE id = @id";
        command.Parameters.AddWithValue("@id", sessionId);
        return command.ExecuteScalar() as string;
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
