using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Muse;

internal sealed record MuseIndexedTitle(string? Title, bool Official);

/// <summary>The native index is an optional title hint, never authority for filesystem paths.</summary>
internal static class MuseSessionIndex
{
    public static IReadOnlyDictionary<string, MuseIndexedTitle> Read(string home, CancellationToken ct)
    {
        var result = new Dictionary<string, MuseIndexedTitle>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(home, "session-index.db");
        try
        {
            if (!File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return result;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
            }.ToString());
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = "PRAGMA table_info(sessions)";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = schema.ExecuteReader())
                while (reader.Read()) { ct.ThrowIfCancellationRequested(); columns.Add(reader.GetString(1)); }
            if (!columns.Contains("session_id") || !columns.Contains("title")) return result;
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT session_id, title, " +
                (columns.Contains("session_name") ? "session_name" : "NULL") + " FROM sessions";
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = rows.GetString(0);
                if (!Guid.TryParse(id, out _)) continue;
                var name = rows.IsDBNull(2) ? null : rows.GetString(2);
                var official = !string.IsNullOrWhiteSpace(name);
                result[id] = new MuseIndexedTitle(official ? name : rows.IsDBNull(1) ? null : rows.GetString(1), official);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or
            InvalidOperationException or InvalidCastException or FormatException)
        {
            result.Clear();
        }
        return result;
    }
}
