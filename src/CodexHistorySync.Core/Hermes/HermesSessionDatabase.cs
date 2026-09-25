using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Hermes;

/// <summary>
/// Reads and writes one Hermes session inside <c>state.db</c>. Full-text indexes, routing locks,
/// and every file beside the database stay on the machine that owns them.
/// </summary>
internal static class HermesSessionDatabase
{
    private static readonly Regex ColumnName = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static HermesReadResult ReadProfiles(HermesPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var snapshots = new List<HermesSnapshot>();
        var complete = true;
        foreach (var profile in paths.ListProfiles())
        {
            var read = ReadDatabase(profile, sessionId: null);
            if (!read.Complete) complete = false;
            snapshots.AddRange(read.Snapshots);
        }

        return new HermesReadResult(snapshots, complete);
    }

    public static HermesSnapshot? ReadOne(HermesPaths paths, string profile, string sessionId)
    {
        var database = paths.DatabasePath(profile);
        if (!File.Exists(database)) return null;
        var read = ReadDatabase(new HermesProfile(profile, database), sessionId);
        if (!read.Complete) throw new InvalidDataException("Hermes session could not be read.");
        return read.Snapshots.FirstOrDefault(snapshot => string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal));
    }

    public static void Write(HermesPaths paths, HermesSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(snapshot);
        var database = paths.DatabasePath(snapshot.Profile);
        var parent = Path.GetDirectoryName(database) ?? throw new InvalidDataException("Hermes database path has no directory.");
        Directory.CreateDirectory(parent);
        using var connection = Open(database, write: true);
        using var transaction = connection.BeginTransaction();
        try
        {
            EnsureTable(connection, "sessions",
                """
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY,
                    source TEXT NOT NULL,
                    started_at REAL NOT NULL
                );
                """);
            EnsureTable(connection, "messages",
                """
                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    timestamp REAL NOT NULL
                );
                """);
            EnsureColumns(connection, "sessions", snapshot.Session);
            foreach (var message in snapshot.Messages) EnsureColumns(connection, "messages", message);

            Execute(connection, "DELETE FROM messages WHERE session_id = @id", ("@id", snapshot.SessionId));
            Execute(connection, "DELETE FROM sessions WHERE id = @id", ("@id", snapshot.SessionId));
            Insert(connection, "sessions", snapshot.Session);
            foreach (var message in snapshot.Messages) Insert(connection, "messages", message);
            transaction.Commit();
        }
        catch (SqliteException exception)
        {
            throw new IOException("Hermes state database could not be updated.", exception);
        }
    }

    public static void Delete(HermesPaths paths, string profile, string sessionId)
    {
        var database = paths.DatabasePath(profile);
        if (!File.Exists(database)) return;
        using var connection = Open(database, write: true);
        using var transaction = connection.BeginTransaction();
        try
        {
            if (!TableExists(connection, "sessions")) return;
            if (TableExists(connection, "messages"))
                Execute(connection, "DELETE FROM messages WHERE session_id = @id", ("@id", sessionId));
            Execute(connection, "DELETE FROM sessions WHERE id = @id", ("@id", sessionId));
            transaction.Commit();
        }
        catch (SqliteException exception)
        {
            throw new IOException("Hermes state database could not be updated.", exception);
        }
    }

    private static HermesReadResult ReadDatabase(HermesProfile profile, string? sessionId)
    {
        using var connection = Open(profile.DatabasePath, write: false);
        if (!TableExists(connection, "sessions"))
            throw new InvalidDataException("Hermes state database has no sessions table.");

        var sessionColumns = Columns(connection, "sessions");
        if (!sessionColumns.Contains("id"))
            throw new InvalidDataException("Hermes sessions table has no id column.");
        var messageColumns = TableExists(connection, "messages")
            ? Columns(connection, "messages").Where(column => !string.Equals(column, "id", StringComparison.Ordinal)).ToArray()
            : [];

        var snapshots = new List<HermesSnapshot>();
        var complete = true;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + SelectList(sessionColumns) + " FROM sessions" +
            (sessionId is null ? string.Empty : " WHERE id = @id") + " ORDER BY id";
        if (sessionId is not null) command.Parameters.AddWithValue("@id", sessionId);
        using var reader = command.ExecuteReader();
        var sessions = new List<(string Id, IReadOnlyList<HermesCell> Cells)>();
        while (reader.Read())
        {
            var cells = ReadCells(reader, sessionColumns, dropId: false);
            var id = Text(cells, "id");
            if (id is null || !HermesPaths.IsSessionId(id))
            {
                complete = false;
                continue;
            }

            sessions.Add((id, cells));
        }
        reader.Dispose();

        foreach (var (id, cells) in sessions)
        {
            var messages = new List<IReadOnlyList<HermesCell>>();
            if (messageColumns.Length != 0)
            {
                using var messagesCommand = connection.CreateCommand();
                messagesCommand.CommandText = "SELECT " + SelectList(messageColumns) +
                    " FROM messages WHERE session_id = @id ORDER BY id";
                messagesCommand.Parameters.AddWithValue("@id", id);
                using var messageReader = messagesCommand.ExecuteReader();
                while (messageReader.Read()) messages.Add(ReadCells(messageReader, messageColumns, dropId: false));
            }

            snapshots.Add(new HermesSnapshot(profile.Name, id, cells, messages, LastActive(cells, messages)));
        }

        return new HermesReadResult(snapshots, complete);
    }

    private static string SelectList(IReadOnlyList<string> columns) =>
        string.Join(", ", columns.Select(column =>
            $"{Quote(column)}, typeof({Quote(column)}), CASE typeof({Quote(column)}) WHEN 'real' THEN CAST({Quote(column)} AS TEXT) ELSE NULL END"));

    private static List<HermesCell> ReadCells(SqliteDataReader reader, IReadOnlyList<string> columns, bool dropId)
    {
        var cells = new List<HermesCell>(columns.Count);
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
                "integer" => new HermesCell(name, "integer", null, reader.GetInt64(valueOrdinal), null),
                "real" => new HermesCell(name, "real", reader.GetString(index * 3 + 2), null, null),
                "text" => new HermesCell(name, "text", reader.IsDBNull(valueOrdinal) ? "" : reader.GetString(valueOrdinal), null, null),
                "blob" => new HermesCell(name, "blob", null, null, Convert.ToBase64String((byte[])reader.GetValue(valueOrdinal))),
                _ => throw new InvalidDataException("Hermes state database has an unsupported value type.")
            });
        }

        return cells.OrderBy(cell => cell.Name, StringComparer.Ordinal).ToList();
    }

    private static double LastActive(IReadOnlyList<HermesCell> session, IReadOnlyList<IReadOnlyList<HermesCell>> messages)
    {
        var latest = double.NegativeInfinity;
        foreach (var message in messages)
        {
            if (Text(message, "timestamp") is { } stamp &&
                double.TryParse(stamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                latest = Math.Max(latest, value);
        }

        if (!double.IsNegativeInfinity(latest)) return latest;
        if (Text(session, "started_at") is { } started &&
            double.TryParse(started, NumberStyles.Float, CultureInfo.InvariantCulture, out var startedValue))
            return startedValue;
        return 0;
    }

    private static string? Text(IReadOnlyList<HermesCell> cells, string name)
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

    private static void EnsureTable(SqliteConnection connection, string table, string createSql)
    {
        if (!TableExists(connection, table)) Execute(connection, createSql);
    }

    private static void EnsureColumns(SqliteConnection connection, string table, IReadOnlyList<HermesCell> cells)
    {
        var existing = Columns(connection, table);
        foreach (var cell in cells)
        {
            if (existing.Contains(cell.Name)) continue;
            Execute(connection, $"ALTER TABLE {Quote(table)} ADD COLUMN {Quote(cell.Name)} {SqlType(cell.Type)}");
            existing.Add(cell.Name);
        }
    }

    private static void Insert(SqliteConnection connection, string table, IReadOnlyList<HermesCell> cells)
    {
        if (cells.Count == 0) throw new InvalidDataException("Hermes session row is empty.");
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
                "integer" => cell.Integer ?? throw new InvalidDataException("Hermes integer value is missing."),
                "real" or "text" => (object?)cell.Text ?? "",
                "blob" => Convert.FromBase64String(cell.BlobBase64 ?? throw new InvalidDataException("Hermes blob value is missing.")),
                _ => throw new InvalidDataException("Hermes value type is unsupported.")
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
                throw new InvalidDataException("Hermes state database has an unsupported column name.");
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
                throw new IOException("Hermes state database is a reparse point.");
        }
        else if (!write)
        {
            throw new FileNotFoundException("Hermes state database does not exist.", databasePath);
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
            throw new InvalidDataException("Hermes column name is not safe to quote.");
        return "\"" + identifier + "\"";
    }

    private static string SqlType(string type) => type switch
    {
        "integer" => "INTEGER",
        "real" => "REAL",
        "text" => "TEXT",
        "blob" => "BLOB",
        _ => throw new InvalidDataException("Hermes value type is unsupported.")
    };
}

internal sealed record HermesCell(string Name, string Type, string? Text, long? Integer, string? BlobBase64);

internal sealed record HermesSnapshot(
    string Profile,
    string SessionId,
    IReadOnlyList<HermesCell> Session,
    IReadOnlyList<IReadOnlyList<HermesCell>> Messages,
    double LastActiveUnix);

internal sealed record HermesReadResult(IReadOnlyList<HermesSnapshot> Snapshots, bool Complete);
