using System.Globalization;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Search;

public sealed record SessionSearchHit(
    ManagedAgent Agent,
    string SessionId,
    string Title,
    string Snippet,
    double Rank);

public interface ISessionSearchIndex
{
    Task EnsureCurrentAsync(
        SessionCatalogSnapshot snapshot,
        ISessionContentReader reader,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionSearchHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// Local FTS5 catalog of portable session text. It lives under
/// <c>%LOCALAPPDATA%\CodexHistorySync\catalog.db</c> and never opens a remote store.
/// </summary>
public sealed class SessionSearchIndex : ISessionSearchIndex, IDisposable
{
    public const int MaximumBodyCharacters = 256 * 1024;
    public const int DefaultSearchLimit = 50;
    public const int MaximumSearchLimit = 200;

    private const string DatabaseFileName = "catalog.db";
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SessionSearchIndex(string? localAppDataDirectory = null)
    {
        var root = localAppDataDirectory
            ?? Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Local application data directory is unavailable.");

        var appData = Path.GetFullPath(root);
        var directory = Path.GetFullPath(Path.Combine(appData, "CodexHistorySync"));
        _databasePath = Path.GetFullPath(Path.Combine(directory, DatabaseFileName));
        if (!CodexPaths.IsPathWithin(_databasePath, directory) ||
            !string.Equals(Path.GetFileName(_databasePath), DatabaseFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The catalog database path is invalid.", nameof(localAppDataDirectory));
        }

        RejectReparsePoints(appData);
        if (Directory.Exists(directory)) RejectReparsePoints(directory);
        if (File.Exists(_databasePath)) RejectReparsePoints(_databasePath);
    }

    public string DatabasePath => _databasePath;

    public async Task EnsureCurrentAsync(
        SessionCatalogSnapshot snapshot,
        ISessionContentReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(reader);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var existing = await LoadRowsAsync(connection, cancellationToken).ConfigureAwait(false);
            var present = new HashSet<(ManagedAgent Agent, string SessionId)>();
            foreach (var session in Enumerate(snapshot))
            {
                present.Add((session.Agent, session.SessionId));
                if (!session.CanRead) continue;

                if (existing.TryGetValue((session.Agent, session.SessionId), out var stored) &&
                    string.Equals(stored.Title, session.Title, StringComparison.Ordinal) &&
                    stored.LastModifiedAt == session.LastModifiedAt)
                {
                    continue;
                }

                PortableConversation conversation;
                try
                {
                    conversation = await reader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException
                                                      or UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }

                var digest = SessionDigest.Build(conversation, MaximumBodyCharacters);
                // Even an unchanged body needs its new title/timestamp persisted.
                await UpsertAsync(connection, session, digest, cancellationToken).ConfigureAwait(false);
            }

            foreach (var key in existing.Keys)
            {
                if (present.Contains(key)) continue;
                await DeleteAsync(connection, key.Agent, key.SessionId, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionSearchHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (limit <= 0) return [];
        limit = Math.Min(limit, MaximumSearchLimit);

        var match = ToMatchQuery(query);
        if (match is null) return [];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_databasePath)) return [];

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT s.agent, s.session_id, s.title,
                       snippet(sessions_fts, 1, '', '', '…', 10) AS snippet,
                       bm25(sessions_fts) AS rank
                FROM sessions_fts
                JOIN sessions AS s ON s.rowid = sessions_fts.rowid
                WHERE sessions_fts MATCH $query
                ORDER BY rank
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", match);
            command.Parameters.AddWithValue("$limit", limit);

            var hits = new List<SessionSearchHit>();
            await using var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Enum.TryParse(rows.GetString(0), ignoreCase: true, out ManagedAgent agent)) continue;
                var snippet = rows.IsDBNull(3) ? string.Empty : rows.GetString(3);
                var rank = rows.IsDBNull(4) ? 0d : rows.GetDouble(4);
                hits.Add(new SessionSearchHit(agent, rows.GetString(1), rows.GetString(2), snippet, rank));
            }

            return hits;
        }
        catch (SqliteException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    internal static string? ToMatchQuery(string query)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var quoted = new List<string>();
        foreach (var token in tokens)
        {
            var cleaned = token.Replace("\"", string.Empty, StringComparison.Ordinal);
            if (cleaned.Length == 0) continue;
            quoted.Add("\"" + cleaned + "\"");
        }

        return quoted.Count == 0 ? null : string.Join(" AND ", quoted);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath)
                        ?? throw new InvalidOperationException("The catalog database has no directory.");
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText =
                """
                CREATE TABLE IF NOT EXISTS sessions (
                    agent TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    last_modified TEXT NOT NULL,
                    native_path TEXT NOT NULL,
                    digest_hash TEXT NOT NULL,
                    body TEXT NOT NULL,
                    PRIMARY KEY (agent, session_id)
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS sessions_fts USING fts5(
                    title,
                    body,
                    session_id,
                    content='sessions',
                    content_rowid='rowid',
                    tokenize='unicode61'
                );
                CREATE TRIGGER IF NOT EXISTS sessions_ai AFTER INSERT ON sessions BEGIN
                    INSERT INTO sessions_fts(rowid, title, body, session_id)
                    VALUES (new.rowid, new.title, new.body, new.session_id);
                END;
                CREATE TRIGGER IF NOT EXISTS sessions_ad AFTER DELETE ON sessions BEGIN
                    INSERT INTO sessions_fts(sessions_fts, rowid, title, body, session_id)
                    VALUES ('delete', old.rowid, old.title, old.body, old.session_id);
                END;
                CREATE TRIGGER IF NOT EXISTS sessions_au AFTER UPDATE ON sessions BEGIN
                    INSERT INTO sessions_fts(sessions_fts, rowid, title, body, session_id)
                    VALUES ('delete', old.rowid, old.title, old.body, old.session_id);
                    INSERT INTO sessions_fts(rowid, title, body, session_id)
                    VALUES (new.rowid, new.title, new.body, new.session_id);
                END;
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private sealed record IndexedRow(string Title, DateTimeOffset LastModifiedAt, string DigestHash);

    private static async Task<Dictionary<(ManagedAgent Agent, string SessionId), IndexedRow>> LoadRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent, session_id, title, last_modified, digest_hash FROM sessions;";
        var rowsById = new Dictionary<(ManagedAgent Agent, string SessionId), IndexedRow>();
        await using var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Enum.TryParse(rows.GetString(0), ignoreCase: true, out ManagedAgent agent)) continue;
            if (!DateTimeOffset.TryParse(
                    rows.GetString(3),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var lastModified))
                continue;
            rowsById[(agent, rows.GetString(1))] = new IndexedRow(rows.GetString(2), lastModified, rows.GetString(4));
        }

        return rowsById;
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        ManagedSession session,
        SessionDigestResult digest,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions (agent, session_id, title, last_modified, native_path, digest_hash, body)
            VALUES ($agent, $session_id, $title, $last_modified, $native_path, $digest_hash, $body)
            ON CONFLICT(agent, session_id) DO UPDATE SET
                title = excluded.title,
                last_modified = excluded.last_modified,
                native_path = excluded.native_path,
                digest_hash = excluded.digest_hash,
                body = excluded.body;
            """;
        command.Parameters.AddWithValue("$agent", session.Agent.ToString());
        command.Parameters.AddWithValue("$session_id", session.SessionId);
        command.Parameters.AddWithValue("$title", session.Title);
        command.Parameters.AddWithValue(
            "$last_modified",
            session.LastModifiedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$native_path", session.NativePath);
        command.Parameters.AddWithValue("$digest_hash", digest.Hash);
        command.Parameters.AddWithValue("$body", digest.Text);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteAsync(
        SqliteConnection connection,
        ManagedAgent agent,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE agent = $agent AND session_id = $session_id;";
        command.Parameters.AddWithValue("$agent", agent.ToString());
        command.Parameters.AddWithValue("$session_id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<ManagedSession> Enumerate(SessionCatalogSnapshot snapshot) =>
        snapshot.ConfiguredAgents.SelectMany(snapshot.For);

    private static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Directory.GetParent(current)?.FullName)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The catalog database path contains a reparse point.");
            }
        }
    }
}
