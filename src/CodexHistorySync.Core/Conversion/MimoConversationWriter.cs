using System.Globalization;
using System.Text.Json;
using CodexHistorySync.Core.Mimo;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Conversion;

public sealed class MimoConversationWriter : IConversationWriter
{
    private readonly MimoPaths paths;
    private readonly Func<string> idGenerator;
    private readonly Func<DateTimeOffset> utcNow;

    public MimoConversationWriter(MimoPaths paths, Func<string>? idGenerator = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.idGenerator = idGenerator ?? (() => "ses_" + Guid.NewGuid().ToString("N")[..24]);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<ConversationWriteResult> WriteAsync(PortableConversation conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Turns.Count == 0)
            throw new ArgumentException("MiMo conversation is invalid.", nameof(conversation));

        var created = utcNow();
        var title = string.IsNullOrWhiteSpace(conversation.Title) ? "Untitled" : conversation.Title.Trim();
        var directory = string.IsNullOrWhiteSpace(conversation.WorkingDirectory) ? Environment.CurrentDirectory : conversation.WorkingDirectory.Trim();

        // Need a project id – create temporary if not existing
        var projectId = EnsureProject(directory);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = GenerateSessionId();
            if (!MimoPaths.IsSessionId(sessionId)) continue;
            if (MimoSessionDatabase.ReadOne(paths, sessionId) is not null) continue;

            var nowMs = created.ToUnixTimeMilliseconds();
            var sessionCells = new List<MimoCell>
            {
                new("id", "text", sessionId, null, null),
                new("slug", "text", sessionId[4..Math.Min(sessionId.Length, 12)], null, null),
                new("project_id", "text", projectId, null, null),
                new("directory", "text", directory, null, null),
                new("title", "text", title, null, null),
                new("title_source", "text", "user", null, null),
                new("title_revision", "integer", null, 0, null),
                new("version", "text", "agent-sync", null, null),
                new("time_created", "integer", null, nowMs, null),
                new("time_updated", "integer", null, nowMs, null),
            };

            var messages = new List<IReadOnlyList<MimoCell>>();
            var partsPerMessage = new List<IReadOnlyList<MimoPart>>();
            string? previousMessageId = null;

            for (var i = 0; i < conversation.Turns.Count; i++)
            {
                var turn = conversation.Turns[i];
                var messageId = "msg_" + Guid.NewGuid().ToString("N")[..24];
                var role = turn.Role == ConversationRole.User ? "user" : "assistant";
                var time = nowMs + i * 1000;
                // MiMoCode resumes a session by reading message.info.time.created and the
                // text parts below; a bare { role, content } payload crashes the session loader.
                var dataJson = role == "user"
                    ? JsonSerializer.Serialize(new
                    {
                        role = "user",
                        agent = "main",
                        time = new { created = time },
                        model = new { providerID = "mimo", modelID = "mimo" }
                    })
                    : JsonSerializer.Serialize(new
                    {
                        role = "assistant",
                        agent = "main",
                        time = new { created = time, completed = time },
                        modelID = "mimo",
                        providerID = "mimo",
                        parentID = previousMessageId
                    });

                var msgCells = new List<MimoCell>
                {
                    new("id", "text", messageId, null, null),
                    new("session_id", "text", sessionId, null, null),
                    new("time_created", "integer", null, time, null),
                    new("time_updated", "integer", null, time, null),
                    new("data", "text", dataJson, null, null),
                };
                messages.Add(msgCells);

                var partId = "prt_" + Guid.NewGuid().ToString("N")[..24];
                var partJson = JsonSerializer.Serialize(new { type = "text", text = turn.Text });
                var partCells = new List<MimoCell>
                {
                    new("id", "text", partId, null, null),
                    new("session_id", "text", sessionId, null, null),
                    new("message_id", "text", messageId, null, null),
                    new("time_created", "integer", null, time, null),
                    new("time_updated", "integer", null, time, null),
                    new("data", "text", partJson, null, null),
                };
                partsPerMessage.Add([new MimoPart(partCells)]);
                previousMessageId = messageId;
            }

            var snapshot = new MimoSnapshot(sessionId, sessionCells, messages, partsPerMessage, created.ToUnixTimeSeconds());
            try
            {
                MimoSessionDatabase.Write(paths, snapshot);
            }
            catch (IOException ex) when (IsUnique(ex) && attempt + 1 < 8 && !string.IsNullOrWhiteSpace(title))
            {
                title += " (copy)";
                continue;
            }

            return Task.FromResult(new ConversationWriteResult(sessionId, paths.AnchorPath(sessionId)));
        }

        throw new IOException("MiMo could not allocate a new session id.");
    }

    private string GenerateSessionId()
    {
        var raw = idGenerator();
        if (raw.StartsWith("ses_", StringComparison.Ordinal)) return raw;
        return "ses_" + raw.Replace("-", "").Replace("_", "")[..20];
    }

    private string EnsureProject(string directory)
    {
        var dbPath = paths.PrimaryDatabasePath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False");
            conn.Open();
            using (var create = conn.CreateCommand())
            {
                // ReadOne validates the session table before Write materializes the package,
                // so a freshly created database needs the full schema up front.
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS "project" (
                        "id" TEXT PRIMARY KEY,
                        "worktree" TEXT NOT NULL,
                        "vcs" TEXT,
                        "name" TEXT,
                        "icon_url" TEXT,
                        "icon_color" TEXT,
                        "time_created" INTEGER NOT NULL,
                        "time_updated" INTEGER NOT NULL,
                        "time_initialized" INTEGER,
                        "sandboxes" TEXT,
                        "commands" TEXT
                    );
                    CREATE TABLE IF NOT EXISTS "session" (
                        "id" TEXT PRIMARY KEY,
                        "project_id" TEXT NOT NULL,
                        "time_created" INTEGER NOT NULL,
                        "time_updated" INTEGER NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS "message" (
                        "id" TEXT PRIMARY KEY,
                        "session_id" TEXT NOT NULL,
                        "time_created" INTEGER NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS "part" (
                        "id" TEXT PRIMARY KEY,
                        "message_id" TEXT NOT NULL,
                        "session_id" TEXT NOT NULL,
                        "time_created" INTEGER NOT NULL
                    );
                    """;
                create.ExecuteNonQuery();
            }

            using var lookup = conn.CreateCommand();
            lookup.CommandText = "SELECT id FROM project WHERE worktree = @wt LIMIT 1";
            lookup.Parameters.AddWithValue("@wt", directory);
            if (lookup.ExecuteScalar() is string existing && !string.IsNullOrWhiteSpace(existing))
                return existing;

            // mimo session list only surfaces sessions whose project is `global` or the
            // project of the current worktree. A synthetic prj_* row is invisible there,
            // so a directory MiMoCode has never opened lands in `global` like most copies.
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO project (id, worktree, vcs, time_created, time_updated, sandboxes)
                VALUES ('global', '/', NULL, @now, @now, '[]')
                """;
            insert.Parameters.AddWithValue("@now", utcNow().ToUnixTimeMilliseconds());
            insert.ExecuteNonQuery();
            return "global";
        }
        catch
        {
            return "global";
        }
    }

    private static bool IsUnique(IOException ex) =>
        ex.InnerException is SqliteException s && s.SqliteErrorCode == 19 && s.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
}
