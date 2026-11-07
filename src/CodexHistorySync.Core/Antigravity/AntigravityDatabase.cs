using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Antigravity;

public sealed record AntigravitySummaryRow(
    string ConversationId,
    string? ParentConversationId,
    int NestingDepth,
    string? AgentName,
    string Title,
    string? Status,
    IReadOnlyList<string> WorkspaceUris,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AntigravityStepRow(
    int Index,
    int StepType,
    int Status,
    byte[] Payload,
    byte[]? Metadata = null);

public sealed record AntigravityTrajectoryMeta(
    string TrajectoryId,
    string ConversationId,
    int TrajectoryType,
    int TotalSteps);

/// <summary>
/// Resilient SQLite access helper for Google Antigravity session storage:
/// handles both real production schemas (<c>last_modified_time</c>, <c>cascade_id</c>)
/// and test fixture schemas (<c>created_at</c>, <c>conversation_id</c>).
/// </summary>
public static class AntigravityDatabase
{
    public static SqliteConnection OpenConnection(string dbPath, bool write = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        if (write)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = write ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = write
            ? "PRAGMA foreign_keys=OFF; PRAGMA busy_timeout=5000;"
            : "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private const string DefaultInstallId = "00000000-0000-0000-0000-000000000000";

    public static void EnsureSummariesTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS `conversation_summaries` (
                `conversation_id` text PRIMARY KEY,
                `title` text NOT NULL DEFAULT "",
                `preview` text NOT NULL DEFAULT "",
                `step_count` integer NOT NULL DEFAULT 0,
                `last_modified_time` datetime NOT NULL DEFAULT "",
                `workspace_uris` text NOT NULL DEFAULT "[]",
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
                `last_user_input_time` datetime NOT NULL DEFAULT "",
                `last_user_input_step_index` integer NOT NULL DEFAULT -1,
                `app_data_dir` text NOT NULL DEFAULT "",
                `raw_summary` blob,
                `group_id` text NOT NULL DEFAULT ""
            );
            CREATE INDEX IF NOT EXISTS `idx_conversation_summaries_last_user_input_time` ON `conversation_summaries`(`last_user_input_time`);
            CREATE INDEX IF NOT EXISTS `idx_conversation_summaries_last_modified_time` ON `conversation_summaries`(`last_modified_time`);
            """;
        command.ExecuteNonQuery();
    }

    public static IReadOnlyList<AntigravitySummaryRow> ReadSummaries(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            return Array.Empty<AntigravitySummaryRow>();
        }

        try
        {
            var fileInfo = new FileInfo(dbPath);
            if (fileInfo.Length == 0)
            {
                return Array.Empty<AntigravitySummaryRow>();
            }

            using var connection = OpenConnection(dbPath, write: false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM conversation_summaries;";

            var results = new List<AntigravitySummaryRow>();
            using var reader = command.ExecuteReader();

            var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                colMap[reader.GetName(i)] = i;
            }

            while (reader.Read())
            {
                var conversationId = GetStringOrDefault(reader, colMap, "conversation_id", string.Empty);
                var parentConversationId = GetNullableString(reader, colMap, "parent_conversation_id");
                var nestingDepth = GetInt32OrDefault(reader, colMap, "nesting_depth", 0);
                var agentName = GetNullableString(reader, colMap, "agent_name");
                var title = GetStringOrDefault(reader, colMap, "title", string.Empty);
                if (string.IsNullOrWhiteSpace(title))
                {
                    var preview = GetStringOrDefault(reader, colMap, "preview", string.Empty);
                    if (!string.IsNullOrWhiteSpace(preview))
                    {
                        title = preview;
                    }
                }
                var status = GetNullableString(reader, colMap, "status");

                IReadOnlyList<string> workspaceUris = Array.Empty<string>();
                var rawUris = GetNullableString(reader, colMap, "workspace_uris");
                if (!string.IsNullOrWhiteSpace(rawUris))
                {
                    try
                    {
                        workspaceUris = JsonSerializer.Deserialize<List<string>>(rawUris) ?? (IReadOnlyList<string>)Array.Empty<string>();
                    }
                    catch (JsonException)
                    {
                        workspaceUris = new[] { rawUris };
                    }
                }

                var createdAt = ResolveDateTime(reader, colMap, "created_at", "last_user_input_time", "last_modified_time");
                var updatedAt = ResolveDateTime(reader, colMap, "updated_at", "last_modified_time", "last_user_input_time");

                results.Add(new AntigravitySummaryRow(
                    conversationId,
                    parentConversationId,
                    nestingDepth,
                    agentName,
                    title,
                    status,
                    workspaceUris,
                    createdAt,
                    updatedAt));
            }

            return results;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<AntigravitySummaryRow>();
        }
    }

    public static AntigravitySummaryRow? ReadSummary(string dbPath, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        return ReadSummaries(dbPath).FirstOrDefault(s => string.Equals(s.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase));
    }

    public static void WriteSummary(
        string dbPath,
        AntigravitySummaryRow summary,
        int stepCount = 0,
        int lastUserStepIndex = -1,
        string? trajectoryId = null,
        string? preview = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(summary);

        using var connection = OpenConnection(dbPath, write: true);
        EnsureSummariesTable(connection);

        var existingColumns = GetTableColumns(connection, "conversation_summaries");

        var columnNames = new List<string>();
        var paramNames = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        void TryAddColumn(string name, object? value)
        {
            if (existingColumns.Contains(name))
            {
                columnNames.Add(name);
                var paramName = "@" + name;
                paramNames.Add(paramName);
                parameters.Add((paramName, value ?? DBNull.Value));
            }
        }

        var normalizedTitle = summary.Title ?? string.Empty;
        var normalizedPreview = preview ?? normalizedTitle;
        var primaryWorkspaceUri = summary.WorkspaceUris is { Count: > 0 } && !string.IsNullOrWhiteSpace(summary.WorkspaceUris[0])
            ? summary.WorkspaceUris[0]
            : null;
        var effectiveTrajectoryId = string.IsNullOrWhiteSpace(trajectoryId) ? summary.ConversationId : trajectoryId;
        var rawSummaryBytes = AntigravityProtobuf.EncodeRawSummary(
            sessionId: effectiveTrajectoryId,
            cascadeId: summary.ConversationId,
            title: normalizedTitle,
            preview: normalizedPreview,
            stepCount: Math.Max(0, stepCount),
            createdAt: summary.CreatedAt,
            updatedAt: summary.UpdatedAt,
            lastUserInputAt: summary.CreatedAt,
            lastUserStepIndex: lastUserStepIndex,
            workspaceUri: primaryWorkspaceUri,
            installId: DefaultInstallId);

        TryAddColumn("conversation_id", summary.ConversationId);
        TryAddColumn("parent_conversation_id", summary.ParentConversationId ?? string.Empty);
        TryAddColumn("nesting_depth", summary.NestingDepth);
        TryAddColumn("agent_name", summary.AgentName ?? string.Empty);
        TryAddColumn("title", normalizedTitle);
        TryAddColumn("status", summary.Status ?? string.Empty);
        TryAddColumn("workspace_uris", summary.WorkspaceUris is not null && summary.WorkspaceUris.Count > 0
            ? JsonSerializer.Serialize(summary.WorkspaceUris)
            : "[]");
        TryAddColumn("created_at", summary.CreatedAt.ToUnixTimeSeconds());
        TryAddColumn("updated_at", summary.UpdatedAt.ToUnixTimeSeconds());
        TryAddColumn("last_modified_time", FormatSqlDateTime(summary.UpdatedAt));
        TryAddColumn("last_user_input_time", FormatSqlDateTime(summary.CreatedAt));
        TryAddColumn("last_user_input_step_index", lastUserStepIndex);
        TryAddColumn("preview", normalizedPreview);
        TryAddColumn("step_count", Math.Max(0, stepCount));
        TryAddColumn("source", string.Empty);
        TryAddColumn("battle_id", string.Empty);
        TryAddColumn("winning_conversation_id", string.Empty);
        TryAddColumn("not_fully_idle", 0);
        TryAddColumn("killed", 0);
        TryAddColumn("app_data_dir", "antigravity-cli");
        TryAddColumn("project_id", "default-cli-project");
        TryAddColumn("raw_summary", rawSummaryBytes);
        TryAddColumn("group_id", string.Empty);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT OR REPLACE INTO conversation_summaries ({string.Join(", ", columnNames)}) " +
            $"VALUES ({string.Join(", ", paramNames)});";

        foreach (var (paramName, val) in parameters)
        {
            command.Parameters.AddWithValue(paramName, val);
        }

        command.ExecuteNonQuery();
    }

    public static void DeleteSummary(string dbPath, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath) || string.IsNullOrWhiteSpace(conversationId))
            return;

        try
        {
            using var connection = OpenConnection(dbPath, write: true);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM conversation_summaries WHERE conversation_id = @id;";
                command.Parameters.AddWithValue("@id", conversationId);
                command.ExecuteNonQuery();
            }

            try
            {
                using var ftsCommand = connection.CreateCommand();
                ftsCommand.CommandText = "DELETE FROM conversations_fts WHERE conversation_id = @id;";
                ftsCommand.Parameters.AddWithValue("@id", conversationId);
                ftsCommand.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
            }
        }
        catch (SqliteException)
        {
        }
    }

    public static void EnsureConversationTables(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS `trajectory_meta` (
                `trajectory_id` text PRIMARY KEY,
                `cascade_id` text,
                `trajectory_type` integer,
                `source` integer
            );
            CREATE TABLE IF NOT EXISTS `steps` (
                `idx` integer PRIMARY KEY,
                `step_type` integer NOT NULL DEFAULT 0,
                `status` integer NOT NULL DEFAULT 0,
                `has_subtrajectory` numeric NOT NULL DEFAULT false,
                `metadata` blob,
                `error_details` blob,
                `permissions` blob,
                `task_details` blob,
                `render_info` blob,
                `step_payload` blob,
                `step_format` integer NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS `idx_steps_status` ON `steps`(`status`);
            CREATE INDEX IF NOT EXISTS `idx_steps_step_type` ON `steps`(`step_type`);
            CREATE TABLE IF NOT EXISTS `gen_metadata` (
                `idx` integer PRIMARY KEY,
                `data` blob,
                `size` integer NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS `executor_metadata` (
                `idx` integer PRIMARY KEY,
                `data` blob
            );
            CREATE TABLE IF NOT EXISTS `parent_references` (
                `idx` integer PRIMARY KEY,
                `data` blob
            );
            CREATE TABLE IF NOT EXISTS `trajectory_metadata_blob` (
                `id` text DEFAULT "main" PRIMARY KEY,
                `data` blob
            );
            CREATE TABLE IF NOT EXISTS `battle_mode_infos` (
                `idx` integer PRIMARY KEY,
                `data` blob
            );
            """;
        command.ExecuteNonQuery();
    }

    public static AntigravityTrajectoryMeta? ReadTrajectoryMeta(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(dbPath);
            if (fileInfo.Length == 0)
            {
                return null;
            }

            using var connection = OpenConnection(dbPath, write: false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM trajectory_meta LIMIT 1;";

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                colMap[reader.GetName(i)] = i;
            }

            var trajectoryId = GetStringOrDefault(reader, colMap, "trajectory_id", string.Empty);
            var conversationId = GetStringOrDefault(reader, colMap, "conversation_id", null!)
                ?? GetStringOrDefault(reader, colMap, "cascade_id", string.Empty);
            var trajectoryType = GetInt32OrDefault(reader, colMap, "trajectory_type", 0);

            int totalSteps;
            if (colMap.TryGetValue("total_steps", out var stepsOrdinal) && !reader.IsDBNull(stepsOrdinal))
            {
                totalSteps = reader.GetInt32(stepsOrdinal);
            }
            else
            {
                reader.Close();
                using var countCmd = connection.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(*) FROM steps;";
                var countObj = countCmd.ExecuteScalar();
                totalSteps = countObj is long l ? (int)l : Convert.ToInt32(countObj);
            }

            return new AntigravityTrajectoryMeta(trajectoryId, conversationId, trajectoryType, totalSteps);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public static IReadOnlyList<AntigravityStepRow> ReadSteps(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            return Array.Empty<AntigravityStepRow>();
        }

        try
        {
            var fileInfo = new FileInfo(dbPath);
            if (fileInfo.Length == 0)
            {
                return Array.Empty<AntigravityStepRow>();
            }

            using var connection = OpenConnection(dbPath, write: false);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT idx, step_type, status, step_payload
                FROM steps
                ORDER BY idx ASC;
                """;

            var results = new List<AntigravityStepRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var idx = reader.GetInt32(0);
                var stepType = reader.GetInt32(1);
                var status = reader.GetInt32(2);
                byte[] payload = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader[3];

                results.Add(new AntigravityStepRow(idx, stepType, status, payload));
            }

            return results;
        }
        catch (SqliteException)
        {
            return Array.Empty<AntigravityStepRow>();
        }
    }

    public static void StreamSteps(string dbPath, Action<int, int, int, byte[]?> onStep)
    {
        ArgumentNullException.ThrowIfNull(onStep);
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            return;
        }

        try
        {
            var fileInfo = new FileInfo(dbPath);
            if (fileInfo.Length == 0)
            {
                return;
            }

            using var connection = OpenConnection(dbPath, write: false);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT idx, step_type, status, step_payload
                FROM steps
                ORDER BY idx ASC;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var idx = reader.GetInt32(0);
                var stepType = reader.GetInt32(1);
                var status = reader.GetInt32(2);
                var payload = reader.IsDBNull(3) ? null : (byte[])reader[3];
                onStep(idx, stepType, status, payload);
            }
        }
        catch (SqliteException)
        {
        }
    }

    public static void WriteSteps(
        string dbPath,
        string trajectoryId,
        string conversationId,
        IReadOnlyList<AntigravityStepRow> steps,
        string? workspaceUri = null,
        DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trajectoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(steps);

        using var connection = OpenConnection(dbPath, write: true);
        EnsureConversationTables(connection);

        var existingColumns = GetTableColumns(connection, "trajectory_meta");
        var stepColumns = GetTableColumns(connection, "steps");
        var metadataBlobColumns = GetTableColumns(connection, "trajectory_metadata_blob");

        using var transaction = connection.BeginTransaction();

        var metaColNames = new List<string>();
        var metaParamNames = new List<string>();
        var metaParams = new List<(string Name, object? Value)>();

        void TryAddMetaColumn(string name, object? value)
        {
            if (existingColumns.Contains(name))
            {
                metaColNames.Add(name);
                var paramName = "@" + name;
                metaParamNames.Add(paramName);
                metaParams.Add((paramName, value ?? DBNull.Value));
            }
        }

        TryAddMetaColumn("trajectory_id", trajectoryId);
        TryAddMetaColumn("conversation_id", conversationId);
        TryAddMetaColumn("cascade_id", conversationId);
        TryAddMetaColumn("trajectory_type", 4);
        TryAddMetaColumn("source", 17);
        TryAddMetaColumn("total_steps", steps.Count);

        using (var metaCommand = connection.CreateCommand())
        {
            metaCommand.Transaction = transaction;
            metaCommand.CommandText =
                $"INSERT OR REPLACE INTO trajectory_meta ({string.Join(", ", metaColNames)}) " +
                $"VALUES ({string.Join(", ", metaParamNames)});";

            foreach (var (paramName, val) in metaParams)
            {
                metaCommand.Parameters.AddWithValue(paramName, val);
            }

            metaCommand.ExecuteNonQuery();
        }

        if (metadataBlobColumns.Contains("id") && metadataBlobColumns.Contains("data"))
        {
            var blobBytes = AntigravityProtobuf.EncodeTrajectoryMetadata(
                workspaceUri: workspaceUri,
                createdAt: createdAt ?? DateTimeOffset.UtcNow,
                cascadeId: conversationId,
                installId: DefaultInstallId);
            using var blobCommand = connection.CreateCommand();
            blobCommand.Transaction = transaction;
            blobCommand.CommandText = "INSERT OR REPLACE INTO trajectory_metadata_blob (id, data) VALUES ('main', @data);";
            blobCommand.Parameters.Add("@data", SqliteType.Blob).Value = blobBytes;
            blobCommand.ExecuteNonQuery();
        }

        using (var cleanCommand = connection.CreateCommand())
        {
            cleanCommand.Transaction = transaction;
            cleanCommand.CommandText = "DELETE FROM steps;";
            cleanCommand.ExecuteNonQuery();
        }

        var includeStepMetadata = stepColumns.Contains("metadata");
        using (var stepCommand = connection.CreateCommand())
        {
            stepCommand.Transaction = transaction;
            stepCommand.CommandText = includeStepMetadata
                ? """
                  INSERT OR REPLACE INTO steps (idx, step_type, status, metadata, step_payload)
                  VALUES (@idx, @type, @status, @metadata, @payload);
                  """
                : """
                  INSERT OR REPLACE INTO steps (idx, step_type, status, step_payload)
                  VALUES (@idx, @type, @status, @payload);
                  """;

            var idxParam = stepCommand.Parameters.Add("@idx", SqliteType.Integer);
            var typeParam = stepCommand.Parameters.Add("@type", SqliteType.Integer);
            var statusParam = stepCommand.Parameters.Add("@status", SqliteType.Integer);
            var metadataParam = includeStepMetadata ? stepCommand.Parameters.Add("@metadata", SqliteType.Blob) : null;
            var payloadParam = stepCommand.Parameters.Add("@payload", SqliteType.Blob);

            foreach (var step in steps)
            {
                var payloadBytes = step.Payload ?? Array.Empty<byte>();
                idxParam.Value = step.Index;
                typeParam.Value = step.StepType;
                statusParam.Value = step.Status;
                if (metadataParam is not null)
                {
                    metadataParam.Value = step.Metadata
                        ?? AntigravityProtobuf.TryExtractStepMetadata(payloadBytes)
                        ?? Array.Empty<byte>();
                }
                payloadParam.Value = payloadBytes;
                stepCommand.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static string FormatSqlDateTime(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff+00:00", CultureInfo.InvariantCulture);

    private static HashSet<string> GetTableColumns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static string GetStringOrDefault(SqliteDataReader reader, Dictionary<string, int> colMap, string name, string fallback)
    {
        if (colMap.TryGetValue(name, out var ordinal) && !reader.IsDBNull(ordinal))
        {
            return reader.GetString(ordinal);
        }
        return fallback;
    }

    private static string? GetNullableString(SqliteDataReader reader, Dictionary<string, int> colMap, string name)
    {
        if (colMap.TryGetValue(name, out var ordinal) && !reader.IsDBNull(ordinal))
        {
            var val = reader.GetString(ordinal);
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        return null;
    }

    private static int GetInt32OrDefault(SqliteDataReader reader, Dictionary<string, int> colMap, string name, int fallback)
    {
        if (colMap.TryGetValue(name, out var ordinal) && !reader.IsDBNull(ordinal))
        {
            return reader.GetInt32(ordinal);
        }
        return fallback;
    }

    private static DateTimeOffset ResolveDateTime(SqliteDataReader reader, Dictionary<string, int> colMap, params string[] candidates)
    {
        foreach (var name in candidates)
        {
            if (colMap.TryGetValue(name, out var ordinal) && !reader.IsDBNull(ordinal))
            {
                return ParseUnixTimestamp(reader.GetValue(ordinal));
            }
        }
        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset ParseUnixTimestamp(object? value)
    {
        if (value is null or DBNull)
        {
            return DateTimeOffset.UtcNow;
        }

        if (value is long l)
        {
            return l > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(l)
                : DateTimeOffset.FromUnixTimeSeconds(l);
        }

        if (value is int i)
        {
            return DateTimeOffset.FromUnixTimeSeconds(i);
        }

        var str = value.ToString();
        if (string.IsNullOrWhiteSpace(str))
        {
            return DateTimeOffset.UtcNow;
        }

        if (long.TryParse(str, out var parsedLong))
        {
            return parsedLong > 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(parsedLong)
                : DateTimeOffset.FromUnixTimeSeconds(parsedLong);
        }

        if (DateTimeOffset.TryParse(str, out var parsedDto))
        {
            return parsedDto;
        }

        return DateTimeOffset.UtcNow;
    }
}
