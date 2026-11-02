using System.Text;
using CodexHistorySync.Core.Antigravity;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class Challenger2InvariantTests : IDisposable
{
    private readonly string _tempDir;

    public Challenger2InvariantTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "agy-challenger2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Protobuf_TimestampRoundTrip_PreservesSecondsAndSubseconds()
    {
        var original = new DateTimeOffset(2026, 10, 30, 15, 30, 45, 123, TimeSpan.Zero);
        var bytes = AntigravityProtobuf.EncodeTimestamp(original);

        // Decode according to protobuf wire format specification (field 1 = seconds varint, field 2 = nanos varint)
        ulong seconds = 0;
        ulong nanos = 0;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var tag = bytes[offset++];
            var fieldNum = tag >> 3;
            var wireType = tag & 0x07;
            Assert.Equal(0, wireType); // varint

            ulong val = 0;
            var shift = 0;
            while (offset < bytes.Length)
            {
                var b = bytes[offset++];
                val |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }

            if (fieldNum == 1) seconds = val;
            else if (fieldNum == 2) nanos = val;
        }

        Assert.Equal((ulong)original.ToUnixTimeSeconds(), seconds);
        var expectedNanos = (ulong)((original.UtcTicks % TimeSpan.TicksPerSecond) * 100);
        Assert.Equal(expectedNanos, nanos);

        var reconstructed = DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            .AddTicks((long)(nanos / 100));
        Assert.Equal(original, reconstructed);
    }

    [Fact]
    public void Protobuf_LargeUnicodeText_RoundTripsAccurately()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            sb.Append("🌟 Unicode test: Привет мир! 🚀 简体中文 日本語 한국어 \n\t Special chars <>&\"' \r\n");
        }
        var largeText = sb.ToString();

        var (_, payload) = AntigravityProtobuf.EncodeUserStep(
            stepIndex: 1,
            text: largeText,
            timestamp: DateTimeOffset.UtcNow,
            sessionId: Guid.NewGuid().ToString(),
            cascadeId: "cascade-1",
            turnId: "turn-1",
            workspaceUri: "file:///C:/Workspace",
            spanHex: "0123456789abcdef");

        var extracted = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypeUserInput, payload);
        Assert.Equal(largeText, extracted);
    }

    [Fact]
    public void Protobuf_AssistantStep_RoundTripsAccurately()
    {
        var responseText = "Here is an assistant response with structured output:\n```json\n{\"status\":\"ok\"}\n```";
        var (_, payload) = AntigravityProtobuf.EncodeAssistantStep(
            stepIndex: 2,
            text: responseText,
            timestamp: DateTimeOffset.UtcNow,
            sessionId: Guid.NewGuid().ToString(),
            cascadeId: "cascade-1",
            turnId: "turn-2",
            spanHex: "0123456789abcdef");

        var extracted = AntigravityProtobuf.TryExtractStepText(AntigravityProtobuf.StepTypePlannerResponse, payload);
        Assert.Equal(responseText, extracted);
    }

    [Fact]
    public void Database_ReadSummaries_EmptyFile_ReturnsEmptyGracefully()
    {
        var emptyFile = Path.Combine(_tempDir, "zero_bytes.db");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        // 0-byte file returns empty list gracefully
        var summaries = AntigravityDatabase.ReadSummaries(emptyFile);
        Assert.Empty(summaries);
    }

    [Fact]
    public void Database_ReadSummaries_EmptyDatabaseNoTable_ReturnsEmptyGracefully()
    {
        var emptyDb = Path.Combine(_tempDir, "no_tables.db");
        using (var conn = new SqliteConnection($"Data Source={emptyDb};Pooling=false"))
        {
            conn.Open();
            // DB exists but conversation_summaries table does not exist
        }

        var summaries = AntigravityDatabase.ReadSummaries(emptyDb);
        Assert.Empty(summaries);
    }

    [Fact]
    public void Database_FileLocking_NonPooledConnectionReleasesHandleImmediately()
    {
        var dbPath = Path.Combine(_tempDir, "lock_test.db");
        using (var conn = AntigravityDatabase.OpenConnection(dbPath, write: true))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE conversation_summaries (conversation_id TEXT PRIMARY KEY, title TEXT);";
            cmd.ExecuteNonQuery();
        }

        // Read summaries via AntigravityDatabase (Pooling=false)
        var summaries = AntigravityDatabase.ReadSummaries(dbPath);
        Assert.Empty(summaries);

        // Attempt to delete the file immediately without clearing pools;
        // AntigravityDatabase enforces Pooling=false so the OS handle is released immediately on Windows.
        var ex = Record.Exception(() => File.Delete(dbPath));
        Assert.Null(ex);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void Contract_AntigravityProtobuf_MethodInventoryCheck()
    {
        // PROJECT.md § M1 specifies:
        // - public static string? ExtractUserInput(byte[] payload)
        // - public static (string? Response, string? Thinking) ExtractPlannerResponse(byte[] payload)
        // - public static byte[] EncodeUserInput(string text)
        // - public static byte[] EncodePlannerResponse(string response, string? thinking = null)

        var protoType = typeof(AntigravityPaths).Assembly.GetType("CodexHistorySync.Core.Antigravity.AntigravityProtobuf");
        Assert.NotNull(protoType);

        // Check accessibility: AntigravityProtobuf is public
        Assert.True(protoType!.IsPublic, "AntigravityProtobuf should be public per PROJECT.md interface contract.");

        // Check methods defined in PROJECT.md interface contract:
        var extractUser = protoType.GetMethod("ExtractUserInput");
        var extractPlanner = protoType.GetMethod("ExtractPlannerResponse");
        var encodeUser = protoType.GetMethod("EncodeUserInput");
        var encodePlanner = protoType.GetMethod("EncodePlannerResponse");

        Assert.NotNull(extractUser);
        Assert.NotNull(extractPlanner);
        Assert.NotNull(encodeUser);
        Assert.NotNull(encodePlanner);
    }


    [Fact]
    public void Database_ReadSummaries_LegacySchemaWithLastModifiedTime()
    {
        var dbPath = Path.Combine(_tempDir, "legacy_summaries.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE conversation_summaries (
                    conversation_id TEXT PRIMARY KEY,
                    title TEXT,
                    last_modified_time TEXT,
                    last_user_input_time TEXT,
                    preview TEXT
                );
                INSERT INTO conversation_summaries (conversation_id, title, last_modified_time, last_user_input_time, preview)
                VALUES ('11111111-2222-3333-4444-555555555555', 'Legacy Title', '2026-10-30T10:00:00Z', '2026-10-30T09:00:00Z', 'Preview');
                """;
            cmd.ExecuteNonQuery();
        }

        var summaries = AntigravityDatabase.ReadSummaries(dbPath);
        Assert.Single(summaries);
        Assert.Equal("11111111-2222-3333-4444-555555555555", summaries[0].ConversationId);
        Assert.Equal("Legacy Title", summaries[0].Title);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T10:00:00Z"), summaries[0].UpdatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T09:00:00Z"), summaries[0].CreatedAt);
    }

    [Fact]
    public void Database_ReadSteps_ChronologicalOrderingEnforced()
    {
        var dbPath = Path.Combine(_tempDir, "out_of_order_steps.db");
        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE steps (
                    idx INTEGER PRIMARY KEY,
                    step_type INTEGER NOT NULL,
                    status INTEGER NOT NULL,
                    step_payload BLOB NOT NULL
                );
                INSERT INTO steps (idx, step_type, status, step_payload) VALUES (10, 14, 3, X'01');
                INSERT INTO steps (idx, step_type, status, step_payload) VALUES (2, 14, 3, X'02');
                INSERT INTO steps (idx, step_type, status, step_payload) VALUES (0, 14, 3, X'03');
                INSERT INTO steps (idx, step_type, status, step_payload) VALUES (5, 14, 3, X'04');
                """;
            cmd.ExecuteNonQuery();
        }

        var steps = AntigravityDatabase.ReadSteps(dbPath);
        Assert.Equal(4, steps.Count);
        Assert.Equal(0, steps[0].Index);
        Assert.Equal(2, steps[1].Index);
        Assert.Equal(5, steps[2].Index);
        Assert.Equal(10, steps[3].Index);
    }

    [Theory]
    [InlineData("a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1", true)]
    [InlineData("A5C7F89B-8E2B-4FA8-B2A0-47B1C31278E1", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("a5c7f89b8e2b4fa8b2a047b1c31278e1", true)] // 32 hex chars (Guid format N)
    [InlineData("a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1/../../evil", false)]
    [InlineData("a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1\\..\\evil", false)]
    [InlineData("../../../etc/passwd", false)]
    [InlineData("..\\..\\windows\\win.ini", false)]
    [InlineData("{a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1}", false)]
    [InlineData("(a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1)", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Paths_IsSessionId_ValidatesStrictly(string? candidate, bool expected)
    {
        Assert.Equal(expected, AntigravityPaths.IsSessionId(candidate!));
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1/sub")]
    [InlineData("{a5c7f89b-8e2b-4fa8-b2a0-47b1c31278e1}")]
    [InlineData("")]
    public void Paths_StrictlyRejectInvalidGuidsAcrossAllMethods(string invalidId)
    {
        var paths = new AntigravityPaths(_tempDir);

        Assert.Throws<ArgumentException>(() => paths.AnchorPath(invalidId));
        Assert.Throws<ArgumentException>(() => paths.ConversationDatabasePath(invalidId));
        Assert.Throws<ArgumentException>(() => paths.TranscriptPath(invalidId));
        Assert.Throws<ArgumentException>(() => paths.AnnotationPath(invalidId));
        Assert.Throws<ArgumentException>(() => paths.BrainSessionDirectory(invalidId));
    }

    [Fact]
    public void Paths_TryParseAnchor_HandlesTrailingSlashOrSeparators()
    {
        var sessionId = "22222222-3333-4444-5555-666666666666";
        var validAnchor = Path.Combine(_tempDir, AntigravityPaths.AnchorDirectoryName, $"{sessionId}.json");

        var success = AntigravityPaths.TryParseAnchor(validAnchor, out var home, out var id);
        Assert.True(success);
        Assert.Equal(Path.GetFullPath(_tempDir), home);
        Assert.Equal(sessionId, id);

        // With trailing separator
        var withTrailingSlash = validAnchor + Path.DirectorySeparatorChar;
        var success2 = AntigravityPaths.TryParseAnchor(withTrailingSlash, out var home2, out var id2);
        Assert.True(success2);
        Assert.Equal(Path.GetFullPath(_tempDir), home2);
        Assert.Equal(sessionId, id2);
    }

    public void Dispose()
    {
        // Force SQLite connection pools to clear before deleting directory
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
