using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Mimo;

internal static class MimoSessionDatabase
{
    private static readonly Regex ColumnName = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static MimoReadResult ReadAll(MimoPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshots = new List<MimoSnapshot>();
        var complete = true;
        var databases = paths.ListDatabasePaths();
        if (databases.Count == 0)
        {
            // Check if default expected DB file exists but scan found none due to filter?
            var primary = paths.PrimaryDatabasePath;
            if (File.Exists(primary))
                databases = [primary];
        }
        foreach (var db in databases)
        {
            var read = ReadDatabase(db);
            if (!read.Complete) complete = false;
            snapshots.AddRange(read.Snapshots);
        }

        // Also try reading primary even if not enumerated, to capture empty case
        if (snapshots.Count == 0 && databases.Count == 0)
        {
            // No data means empty but not necessarily incomplete – handled by scanner
        }

        return new MimoReadResult(snapshots, complete);
    }

    public static MimoSnapshot? ReadOne(MimoPaths paths, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!MimoPaths.IsSessionId(sessionId)) return null;
        foreach (var db in paths.ListDatabasePaths())
        {
            var read = ReadDatabase(db, sessionId);
            if (!read.Complete) throw new InvalidDataException("MiMo session could not be read.");
            var found = read.Snapshots.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
            if (found is not null) return found;
        }
        // Also try primary if not in list
        var primary = paths.PrimaryDatabasePath;
        if (File.Exists(primary) && !paths.ListDatabasePaths().Contains(primary, StringComparer.OrdinalIgnoreCase))
        {
            var read = ReadDatabase(primary, sessionId);
            if (!read.Complete) throw new InvalidDataException("MiMo session could not be read.");
            var found = read.Snapshots.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
            if (found is not null) return found;
        }
        return null;
    }

    public static void Write(MimoPaths paths, MimoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(snapshot);
        // Write to primary database (or the one where session currently lives if migrating?)
        // Choose primary; if session exists in another channel DB, we still write to primary
        // and the old will be considered duplicate until cleaned.
        var database = paths.PrimaryDatabasePath;
        var parent = Path.GetDirectoryName(database) ?? throw new InvalidDataException("MiMo database path has no directory.");
        Directory.CreateDirectory(parent);
        using var connection = Open(database, write: true);
        using var transaction = connection.BeginTransaction();
        try
        {
            EnsureTable(connection, "project",
                """
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
                """);
            EnsureTable(connection, "session",
                """
                CREATE TABLE IF NOT EXISTS "session" (
                    "id" TEXT PRIMARY KEY,
                    "project_id" TEXT NOT NULL,
                    "time_created" INTEGER NOT NULL,
                    "time_updated" INTEGER NOT NULL
                );
                """);
            EnsureTable(connection, "message",
                """
                CREATE TABLE IF NOT EXISTS "message" (
                    "id" TEXT PRIMARY KEY,
                    "session_id" TEXT NOT NULL,
                    "time_created" INTEGER NOT NULL
                );
                """);
            EnsureTable(connection, "part",
                """
                CREATE TABLE IF NOT EXISTS "part" (
                    "id" TEXT PRIMARY KEY,
                    "message_id" TEXT NOT NULL,
                    "session_id" TEXT NOT NULL,
                    "time_created" INTEGER NOT NULL
                );
                """);
            // TUI /sessions lists from the history_fts table; without rows here a copy
            // exists in the database but is invisible in the MiMoCode session picker.
            EnsureTable(connection, "history_fts",
                """
                CREATE TABLE IF NOT EXISTS "history_fts" (
                    "part_id" TEXT PRIMARY KEY NOT NULL,
                    "session_id" TEXT NOT NULL,
                    "message_id" TEXT NOT NULL,
                    "project_id" TEXT NOT NULL,
                    "tool_name" TEXT,
                    "body" TEXT NOT NULL,
                    "time_created" INTEGER NOT NULL
                );
                """);
            EnsureColumns(connection, "session", snapshot.Session);
            foreach (var message in snapshot.Messages) EnsureColumns(connection, "message", message);
            foreach (var partList in snapshot.Parts)
                foreach (var part in partList) EnsureColumns(connection, "part", part.Cells);

            Execute(connection, "DELETE FROM \"part\" WHERE \"session_id\" = @id", ("@id", snapshot.SessionId));
            Execute(connection, "DELETE FROM \"message\" WHERE \"session_id\" = @id", ("@id", snapshot.SessionId));
            Execute(connection, "DELETE FROM \"session\" WHERE \"id\" = @id", ("@id", snapshot.SessionId));
            Execute(connection, "DELETE FROM \"history_fts\" WHERE \"session_id\" = @sid", ("@sid", snapshot.SessionId));
            Insert(connection, "session", snapshot.Session);
            foreach (var message in snapshot.Messages) Insert(connection, "message", message);
            foreach (var partList in snapshot.Parts)
                foreach (var part in partList) Insert(connection, "part", part.Cells);
            IndexForSearch(connection, snapshot);

            transaction.Commit();
        }
        catch (SqliteException exception)
        {
            throw new IOException("MiMo state database could not be updated.", exception);
        }
    }

    public static void Delete(MimoPaths paths, string sessionId)
    {
        if (!MimoPaths.IsSessionId(sessionId)) return;
        foreach (var database in paths.ListDatabasePaths())
        {
            if (!File.Exists(database)) continue;
            using var connection = Open(database, write: true);
            using var transaction = connection.BeginTransaction();
            try
            {
                if (!TableExists(connection, "session")) continue;
                if (TableExists(connection, "part"))
                    Execute(connection, "DELETE FROM \"part\" WHERE \"session_id\" = @id", ("@id", sessionId));
                if (TableExists(connection, "message"))
                    Execute(connection, "DELETE FROM \"message\" WHERE \"session_id\" = @id", ("@id", sessionId));
                Execute(connection, "DELETE FROM \"session\" WHERE \"id\" = @id", ("@id", sessionId));
                transaction.Commit();
            }
            catch (SqliteException exception)
            {
                throw new IOException("MiMo state database could not be updated.", exception);
            }
        }
    }

    private static MimoReadResult ReadDatabase(string databasePath, string? sessionId = null)
    {
        using var connection = Open(databasePath, write: false);
        if (!TableExists(connection, "session"))
            throw new InvalidDataException("MiMo state database has no session table.");

        var sessionColumns = Columns(connection, "session");
        if (!sessionColumns.Contains("id"))
            throw new InvalidDataException("MiMo sessions table has no id column.");
        var messageColumns = TableExists(connection, "message")
            ? Columns(connection, "message")
            : new List<string>();
        // For messages, drop rowid-col "id" from filtering? Keep as is.
        var partColumns = TableExists(connection, "part")
            ? Columns(connection, "part")
            : new List<string>();
        // Parts may have id that we should keep but distinguish.

        var snapshots = new List<MimoSnapshot>();
        var complete = true;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + SelectList(sessionColumns) + " FROM \"session\"" +
            (sessionId is null ? string.Empty : " WHERE \"id\" = @id") + " ORDER BY \"id\"";
        if (sessionId is not null) command.Parameters.AddWithValue("@id", sessionId);
        using var reader = command.ExecuteReader();
        var sessions = new List<(string Id, IReadOnlyList<MimoCell> Cells)>();
        while (reader.Read())
        {
            var cells = ReadCells(reader, sessionColumns, dropId: false);
            var id = Text(cells, "id");
            if (id is null || !MimoPaths.IsSessionId(id))
            {
                complete = false;
                continue;
            }
            sessions.Add((id, cells));
        }
        reader.Dispose();

        foreach (var (id, cells) in sessions)
        {
            var messages = new List<IReadOnlyList<MimoCell>>();
            var partsPerMessage = new List<IReadOnlyList<MimoPart>>();

            if (messageColumns.Count != 0)
            {
                using var messagesCommand = connection.CreateCommand();
                messagesCommand.CommandText = "SELECT " + SelectList(messageColumns) +
                    " FROM \"message\" WHERE \"session_id\" = @id ORDER BY \"time_created\", \"id\"";
                messagesCommand.Parameters.AddWithValue("@id", id);
                using var messageReader = messagesCommand.ExecuteReader();
                var messageRows = new List<IReadOnlyList<MimoCell>>();
                while (messageReader.Read()) messageRows.Add(ReadCells(messageReader, messageColumns, dropId: false));
                messageReader.Dispose();

                foreach (var msgCells in messageRows)
                {
                    messages.Add(msgCells);
                    var msgId = Text(msgCells, "id");
                    var msgParts = new List<MimoPart>();
                    if (msgId is not null && partColumns.Count != 0)
                    {
                        using var partCommand = connection.CreateCommand();
                        partCommand.CommandText = "SELECT " + SelectList(partColumns) +
                            " FROM \"part\" WHERE \"message_id\" = @mid AND \"session_id\" = @sid ORDER BY \"time_created\", \"id\"";
                        partCommand.Parameters.AddWithValue("@mid", msgId);
                        partCommand.Parameters.AddWithValue("@sid", id);
                        using var partReader = partCommand.ExecuteReader();
                        while (partReader.Read())
                        {
                            var partCells = ReadCells(partReader, partColumns, dropId: false);
                            msgParts.Add(new MimoPart(partCells));
                        }
                    }
                    partsPerMessage.Add(msgParts);
                }
            }
            else
            {
                // No message table, still produce snapshot with just session
            }

            // Ensure partsPerMessage aligns with messages count (fill missing with empty)
            while (partsPerMessage.Count < messages.Count) partsPerMessage.Add([]);

            snapshots.Add(new MimoSnapshot(id, cells, messages, partsPerMessage, LastActive(cells, messages)));
        }

        return new MimoReadResult(snapshots, complete);
    }

    private static string SelectList(IReadOnlyList<string> columns) =>
        string.Join(", ", columns.Select(column =>
            $"{Quote(column)}, typeof({Quote(column)}), CASE typeof({Quote(column)}) WHEN 'real' THEN CAST({Quote(column)} AS TEXT) ELSE NULL END"));

    private static List<MimoCell> ReadCells(SqliteDataReader reader, IReadOnlyList<string> columns, bool dropId)
    {
        var cells = new List<MimoCell>(columns.Count);
        for (var index = 0; index < columns.Count; index++)
        {
            if (dropId && string.Equals(columns[index], "id", StringComparison.Ordinal)) continue;
            var typeOrdinal = index * 3 + 1;
            if (reader.IsDBNull(typeOrdinal)) continue;
            var storage = reader.GetString(typeOrdinal);
            if (storage is "null") continue;
            var valueOrdinal = index * 3;
            var name = columns[index];
            cells.Add(storage switch
            {
                "integer" => new MimoCell(name, "integer", null, reader.GetInt64(valueOrdinal), null),
                "real" => new MimoCell(name, "real", reader.GetString(index * 3 + 2), null, null),
                "text" => new MimoCell(name, "text", reader.IsDBNull(valueOrdinal) ? "" : reader.GetString(valueOrdinal), null, null),
                "blob" => new MimoCell(name, "blob", null, null, Convert.ToBase64String((byte[])reader.GetValue(valueOrdinal))),
                _ => throw new InvalidDataException("MiMo state database has an unsupported value type.")
            });
        }

        return cells.OrderBy(cell => cell.Name, StringComparer.Ordinal).ToList();
    }

    private static double LastActive(IReadOnlyList<MimoCell> session, IReadOnlyList<IReadOnlyList<MimoCell>> messages)
    {
        var latest = double.NegativeInfinity;
        foreach (var message in messages)
        {
            if (Text(message, "time_created") is { } stamp &&
                double.TryParse(stamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                latest = Math.Max(latest, value);
            else if (Text(message, "time_updated") is { } stamp2 &&
                     double.TryParse(stamp2, NumberStyles.Float, CultureInfo.InvariantCulture, out var v2))
                latest = Math.Max(latest, v2);
        }
        if (!double.IsNegativeInfinity(latest)) return NormalizeTime(latest);
        if (Text(session, "time_updated") is { } updated &&
            double.TryParse(updated, NumberStyles.Float, CultureInfo.InvariantCulture, out var uv))
            return NormalizeTime(uv);
        if (Text(session, "time_created") is { } created &&
            double.TryParse(created, NumberStyles.Float, CultureInfo.InvariantCulture, out var cv))
            return NormalizeTime(cv);
        return 0;
    }

    private static double NormalizeTime(double value)
    {
        // MiMo stores timestamps as milliseconds or seconds depending on version.
        // Normalize to seconds: if value > 1e12 treat as ms, else as ms*? Actually mimic tokscale logic
        // Keep as stored but ensure seconds for activity window comparison.
        // Activity window expects seconds Unix timestamp.
        // If value > 1e12 (> 2001-09-09 in ms), convert ms -> s.
        if (value > 1e12) return value / 1000.0;
        if (value > 1e10) return value / 1000.0; // also ms
        return value;
    }

    private static string? Text(IReadOnlyList<MimoCell> cells, string name)
    {
        var cell = cells.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (cell is null) return null;
        return cell.Type switch
        {
            "text" => cell.Text,
            "real" => cell.Text,
            "integer" => cell.Integer?.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static void IndexForSearch(SqliteConnection connection, MimoSnapshot snapshot)
    {
        var projectId = snapshot.Session
            .FirstOrDefault(c => string.Equals(c.Name, "project_id", StringComparison.Ordinal))
            ?.Text ?? "global";
        foreach (var partList in snapshot.Parts)
        {
            foreach (var part in partList)
            {
                var id = Text(part.Cells, "id");
                var messageId = Text(part.Cells, "message_id");
                if (id is null || messageId is null) continue;
                var (toolName, body) = SearchBody(part.Cells);
                if (body.Length == 0) continue;
                Execute(connection,
                    """
                    INSERT OR IGNORE INTO "history_fts"
                        ("part_id", "session_id", "message_id", "project_id", "tool_name", "body", "time_created")
                    VALUES (@pid, @sid, @mid, @project, @tool, @body, @created)
                    """,
                    ("@pid", id),
                    ("@sid", snapshot.SessionId),
                    ("@mid", messageId),
                    ("@project", projectId),
                    ("@tool", (object?)toolName ?? DBNull.Value),
                    ("@body", body),
                    ("@created", Text(part.Cells, "time_created") is { } created &&
                                 double.TryParse(created, NumberStyles.Float, CultureInfo.InvariantCulture, out var stamp)
                            ? stamp
                            : snapshot.LastActiveUnix));
            }
        }
    }

    private static (string? Tool, string Body) SearchBody(IReadOnlyList<MimoCell> cells)
    {
        var raw = Text(cells, "data");
        if (string.IsNullOrWhiteSpace(raw)) return (null, string.Empty);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? tool = root.TryGetProperty("tool", out var t) ? t.GetString() : null;
            var body = root.TryGetProperty("text", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String
                ? text.GetString() ?? string.Empty
                : root.TryGetProperty("output", out var output) && output.ValueKind == System.Text.Json.JsonValueKind.String
                    ? output.GetString() ?? string.Empty
                    : root.TryGetProperty("state", out var state) &&
                      state.ValueKind == System.Text.Json.JsonValueKind.Object &&
                      state.TryGetProperty("output", out var stateOut) &&
                      stateOut.ValueKind == System.Text.Json.JsonValueKind.String
                        ? stateOut.GetString() ?? string.Empty
                        : string.Empty;
            return (tool, body);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, raw);
        }
    }

    private static void EnsureTable(SqliteConnection connection, string table, string createSql)
    {
        if (!TableExists(connection, table)) Execute(connection, createSql);
    }

    private static void EnsureColumns(SqliteConnection connection, string table, IReadOnlyList<MimoCell> cells)
    {
        var existing = Columns(connection, table);
        foreach (var cell in cells)
        {
            if (existing.Contains(cell.Name)) continue;
            Execute(connection, $"ALTER TABLE {Quote(table)} ADD COLUMN {Quote(cell.Name)} {SqlType(cell.Type)}");
            existing.Add(cell.Name);
        }
    }

    private static void Insert(SqliteConnection connection, string table, IReadOnlyList<MimoCell> cells)
    {
        if (cells.Count == 0) throw new InvalidDataException("MiMo session row is empty.");
        var names = string.Join(", ", cells.Select(cell => Quote(cell.Name)));
        var values = string.Join(", ", cells.Select((cell, index) =>
            cell.Type == "real" ? $"CAST(@p{index} AS REAL)" : $"@p{index}"));
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {Quote(table)} ({names}) VALUES ({values})";
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            command.Parameters.AddWithValue("@p" + index.ToString(CultureInfo.InvariantCulture), cell.Type switch
            {
                "integer" => cell.Integer ?? throw new InvalidDataException("MiMo integer value is missing."),
                "real" or "text" => (object?)cell.Text ?? "",
                "blob" => Convert.FromBase64String(cell.BlobBase64 ?? throw new InvalidDataException("MiMo blob value is missing.")),
                _ => throw new InvalidDataException("MiMo value type is unsupported.")
            });
        }

        command.ExecuteNonQuery();
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (!ColumnName.IsMatch(name))
                throw new InvalidDataException("MiMo state database has an unsupported column name.");
            columns.Add(name);
        }

        columns.Sort(StringComparer.Ordinal);
        return columns;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        command.Parameters.AddWithValue("@name", table);
        return command.ExecuteScalar() is not null;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string databasePath, bool write)
    {
        if (File.Exists(databasePath))
        {
            if (File.GetAttributes(databasePath).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("MiMo state database is a reparse point.");
        }
        else if (!write)
        {
            throw new FileNotFoundException("MiMo state database does not exist.", databasePath);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = write
            ? "PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=2000;"
            : "PRAGMA query_only=ON; PRAGMA busy_timeout=2000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static string Quote(string identifier)
    {
        if (!ColumnName.IsMatch(identifier))
            throw new InvalidDataException("MiMo column name is not safe to quote.");
        return "\"" + identifier + "\"";
    }

    private static string SqlType(string type) => type switch
    {
        "integer" => "INTEGER",
        "real" => "REAL",
        "text" => "TEXT",
        "blob" => "BLOB",
        _ => throw new InvalidDataException("MiMo value type is unsupported.")
    };
}

internal sealed record MimoCell(string Name, string Type, string? Text, long? Integer, string? BlobBase64);

internal sealed record MimoPart(IReadOnlyList<MimoCell> Cells);

internal sealed record MimoSnapshot(
    string SessionId,
    IReadOnlyList<MimoCell> Session,
    IReadOnlyList<IReadOnlyList<MimoCell>> Messages,
    IReadOnlyList<IReadOnlyList<MimoPart>> Parts,
    double LastActiveUnix)
{
    /// <summary>
    /// A child session created for an internal agent run (checkpoint-writer and similar). The
    /// manager hides it and the scanner ignores it, matching how Codex treats subagent threads.
    /// </summary>
    public bool IsSubagent =>
        Session.Any(cell =>
            string.Equals(cell.Name, "parent_id", StringComparison.Ordinal) &&
            cell.Type == "text" &&
            !string.IsNullOrEmpty(cell.Text));

    /// <summary>
    /// A session left behind by the previous MiMo generation (schema <c>version</c> 2.1.x).
    /// MiMoCode does not list those as chats, so the manager hides them; the scanner still
    /// keeps them so an older machine's history is never treated as deleted.
    /// </summary>
    public bool IsLegacyGeneration =>
        Session.Any(cell =>
            string.Equals(cell.Name, "version", StringComparison.Ordinal) &&
            cell.Type == "text" &&
            cell.Text is { Length: > 0 } text &&
            text.StartsWith("2.1.", StringComparison.Ordinal));
}

internal sealed record MimoReadResult(IReadOnlyList<MimoSnapshot> Snapshots, bool Complete);
