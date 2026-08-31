using System.Text;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Continue;

namespace CodexHistorySync.Core.Tests.Continue;

public sealed class ContinueSessionPackageTests
{
    private const string SessionId = "9490954d-d7dd-4cbe-984c-6172d60bf3dc";

    [Fact]
    public void PackageCarriesTheSessionAndItsIndexEntry()
    {
        using var fixture = new ContinueHomeFixture();
        var path = fixture.WriteSession(SessionId, "hi");
        fixture.WriteIndex(fixture.Entry(SessionId, "hi", "1788134536812"));

        var info = ContinueSessionPackage.Parse(
            ContinueSessionPackage.BuildFromFile(path, fixture.ReadIndex()));

        Assert.Equal(SessionId, info.SessionId);
        Assert.Equal("hi", (string?)info.Entry["title"]);
        Assert.Equal("1788134536812", (string?)info.Entry["dateCreated"]);
        var session = Assert.IsType<JsonObject>(JsonNode.Parse(Encoding.UTF8.GetString(info.Session)));
        Assert.Equal(SessionId, (string?)session["sessionId"]);
        Assert.Equal(2, Assert.IsType<JsonArray>(session["history"]).Count);
    }

    [Fact]
    public void ASessionMissingFromTheIndexGetsASynthesizedEntry()
    {
        // Continue tolerates a session file the index does not list — it simply does not show it.
        // Refusing to package it would make the one session most in need of a copy unsyncable.
        using var fixture = new ContinueHomeFixture();
        var path = fixture.WriteSession(SessionId, "orphaned");
        fixture.WriteIndex();
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(1788134536812));

        var info = ContinueSessionPackage.Parse(
            ContinueSessionPackage.BuildFromFile(path, fixture.ReadIndex()));

        Assert.Equal("orphaned", (string?)info.Entry["title"]);
        Assert.Equal("1788134536812", (string?)info.Entry["dateCreated"]);
        Assert.Equal(SessionId, (string?)info.Entry["sessionId"]);
    }

    [Fact]
    public void ABrokenIndexDoesNotStopASessionFromBeingPackaged()
    {
        using var fixture = new ContinueHomeFixture();
        var path = fixture.WriteSession(SessionId, "still mine");

        var info = ContinueSessionPackage.Parse(
            ContinueSessionPackage.BuildFromFile(path, "{ not an array"));

        Assert.Equal("still mine", (string?)info.Entry["title"]);
    }

    [Fact]
    public void ASessionFileThatNamesAnotherSessionIsRefused()
    {
        using var fixture = new ContinueHomeFixture();
        var path = fixture.WriteSession(SessionId, "hi",
            declaredId: "00000000-0000-0000-0000-000000000000");

        Assert.Throws<InvalidDataException>(() => ContinueSessionPackage.BuildFromFile(path, null));
    }

    [Fact]
    public void TheIndexIsNeverPackagedAsASession()
    {
        using var fixture = new ContinueHomeFixture();
        fixture.WriteIndex();

        Assert.Throws<InvalidDataException>(() =>
            ContinueSessionPackage.BuildFromFile(Path.Combine(fixture.Paths.Sessions, "sessions.json"), null));
        Assert.Throws<ArgumentException>(() => fixture.Paths.SessionFilePath("sessions"));
    }

    [Fact]
    public void APackageEntryNamingAnotherSessionIsRefused()
    {
        using var fixture = new ContinueHomeFixture();
        var path = fixture.WriteSession(SessionId, "hi");
        var package = Assert.IsType<JsonObject>(
            JsonNode.Parse(ContinueSessionPackage.BuildFromFile(path, fixture.ReadIndex())));
        var entry = Assert.IsType<JsonObject>(JsonNode.Parse((string)package["entry"]!));
        entry["sessionId"] = "00000000-0000-0000-0000-000000000000";
        package["entry"] = entry.ToJsonString();

        Assert.Throws<InvalidDataException>(() =>
            ContinueSessionPackage.Parse(Encoding.UTF8.GetBytes(package.ToJsonString())));
    }

    [Fact]
    public void MaterializeWritesTheSessionAndListsItInTheIndex()
    {
        using var source = new ContinueHomeFixture();
        var path = source.WriteSession(SessionId, "hi");
        source.WriteIndex(source.Entry(SessionId, "hi", "1788134536812"));
        var package = ContinueSessionPackage.Parse(
            ContinueSessionPackage.BuildFromFile(path, source.ReadIndex()));

        using var target = new ContinueHomeFixture();
        target.WriteIndex();
        ContinueSessionPackage.Materialize(package, target.Paths);

        Assert.True(File.Exists(target.Paths.SessionFilePath(SessionId)));
        var entry = Assert.Single(ContinueSessionIndex.Parse(target.ReadIndex()));
        Assert.Equal("hi", (string?)entry["title"]);
        Assert.Equal("1788134536812", (string?)entry["dateCreated"]);
    }

    [Fact]
    public void AnImportReplacesItsOwnEntryRatherThanMergingIntoIt()
    {
        // The object hash covers the session together with its entry. If an import left a local
        // member behind, this machine would rebuild a different package than the one it received
        // and each side would keep seeing the other's copy as changed.
        using var source = new ContinueHomeFixture();
        var path = source.WriteSession(SessionId, "arriving");
        source.WriteIndex(source.Entry(SessionId, "arriving", "1788134536812"));
        var package = ContinueSessionPackage.Parse(
            ContinueSessionPackage.BuildFromFile(path, source.ReadIndex()));

        using var target = new ContinueHomeFixture();
        var stale = target.Entry(SessionId, "older title", "1600000000000");
        stale["localOnly"] = "left over";
        target.WriteIndex(stale);
        target.WriteSession(SessionId, "arriving");
        ContinueSessionPackage.Materialize(package, target.Paths);

        var entry = Assert.Single(ContinueSessionIndex.Parse(target.ReadIndex()));
        Assert.Equal("arriving", (string?)entry["title"]);
        Assert.Equal("1788134536812", (string?)entry["dateCreated"]);
        Assert.False(entry.ContainsKey("localOnly"));

        // The point of the replacement: rebuilding here reproduces the package byte for byte.
        var rebuilt = ContinueSessionPackage.BuildFromFile(
            target.Paths.SessionFilePath(SessionId), target.ReadIndex());
        Assert.Equal(
            ContinueSessionPackage.HashPackage(ContinueSessionPackage.BuildFromFile(path, source.ReadIndex())),
            ContinueSessionPackage.HashPackage(rebuilt));
    }

    [Fact]
    public void MaterializeKeepsTheEntriesTheTargetAlreadyHad()
    {
        // The index is shared. Replacing it with our own view would delete every session this
        // repository has never seen — which is most of them, on a machine that has just joined.
        using var source = new ContinueHomeFixture();
        var path = source.WriteSession(SessionId, "arriving");
        var package = ContinueSessionPackage.Parse(ContinueSessionPackage.BuildFromFile(path, null));

        using var target = new ContinueHomeFixture();
        target.WriteIndex(target.Entry("11111111-1111-1111-1111-111111111111", "local", "1700000000000"));
        ContinueSessionPackage.Materialize(package, target.Paths);

        var entries = ContinueSessionIndex.Parse(target.ReadIndex());
        Assert.Equal(2, entries.Count);
        Assert.Equal("local", (string?)entries[0]["title"]);
        Assert.Equal("arriving", (string?)entries[1]["title"]);
    }

    [Fact]
    public void AnImportThatChangesNothingLeavesTheIndexByteIdentical()
    {
        // Written out the way Continue writes it rather than through our own serializer: the
        // first version of this passed while emitting CRLF, because both sides of the comparison
        // were ours. The real file is produced by JSON.stringify and is LF on every platform.
        const string Written = "[\n  {\n    \"sessionId\": \"9490954d-d7dd-4cbe-984c-6172d60bf3dc\",\n" +
                               "    \"title\": \"hi\",\n    \"dateCreated\": \"1788134536812\",\n" +
                               "    \"workspaceDirectory\": \"file:///c%3A/Repos/Reborn\",\n" +
                               "    \"messageCount\": 7\n  }\n]";
        using var fixture = new ContinueHomeFixture();
        var path = fixture.WriteSession(SessionId, "hi");
        File.WriteAllText(fixture.Paths.IndexFilePath, Written);
        var before = File.ReadAllBytes(fixture.Paths.IndexFilePath);
        var package = ContinueSessionPackage.Parse(
            ContinueSessionPackage.BuildFromFile(path, fixture.ReadIndex()));

        ContinueSessionPackage.Materialize(package, fixture.Paths);

        Assert.Equal(Written, File.ReadAllText(fixture.Paths.IndexFilePath));
        Assert.Equal(before, File.ReadAllBytes(fixture.Paths.IndexFilePath));
    }

    [Fact]
    public void ANonEnglishTitleIsNotRewrittenAsEscapeSequences()
    {
        // JSON.stringify leaves these characters alone; escaping them would rewrite entries that
        // this import never touched.
        const string Written = "[\n  {\n    \"sessionId\": \"11111111-1111-1111-1111-111111111111\",\n" +
                               "    \"title\": \"Сколько ног у воробья?\",\n    \"dateCreated\": \"1700000000000\",\n" +
                               "    \"workspaceDirectory\": \"\",\n    \"messageCount\": 2\n  }\n]";
        using var fixture = new ContinueHomeFixture();
        var path = fixture.WriteSession(SessionId, "hi");
        File.WriteAllText(fixture.Paths.IndexFilePath, Written);
        var package = ContinueSessionPackage.Parse(
            ContinueSessionPackage.BuildFromFile(path, fixture.ReadIndex()));

        ContinueSessionPackage.Materialize(package, fixture.Paths);

        var index = File.ReadAllText(fixture.Paths.IndexFilePath);
        Assert.Contains("\"Сколько ног у воробья?\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", index, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', index);
    }

    [Fact]
    public void CarriageReturnsAreNormalizedSoTheHashDoesNotDependOnCheckout()
    {
        using var fixture = new ContinueHomeFixture();
        var path = fixture.WriteSession(SessionId, "hi");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n", StringComparison.Ordinal));

        var info = ContinueSessionPackage.Parse(ContinueSessionPackage.BuildFromFile(path, null));

        Assert.DoesNotContain('\r', Encoding.UTF8.GetString(info.Session));
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("9490954d-d7dd-4cbe-984c")]
    public void ASessionFileNameThatIsNotAUuidIsRefused(string name)
    {
        using var fixture = new ContinueHomeFixture();
        var path = Path.Combine(fixture.Paths.Sessions, name + ".json");
        File.WriteAllText(path, "{\"sessionId\":\"" + name + "\",\"history\":[]}");

        Assert.Throws<InvalidDataException>(() => ContinueSessionPackage.BuildFromFile(path, null));
    }

    [Fact]
    public void LogicalIdsStayInTheirOwnNamespace()
    {
        var logicalId = ContinueSessionPackage.ToLogicalId(SessionId.ToUpperInvariant());

        Assert.Equal("co-" + SessionId, logicalId);
        Assert.True(ContinueSessionPackage.IsContinueLogicalId(logicalId));
        Assert.False(ContinueSessionPackage.IsContinueLogicalId("cl-" + SessionId));
        Assert.False(ContinueSessionPackage.IsContinueLogicalId("g-" + SessionId));
        Assert.Equal(SessionId, ContinueSessionPackage.SessionIdFromLogicalId(logicalId));
    }

    internal sealed class ContinueHomeFixture : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), $"codex-history-sync-continue-{Guid.NewGuid():N}");

        public ContinueHomeFixture()
        {
            var home = Path.Combine(root, ".continue");
            var sessions = Path.Combine(home, "sessions");
            Directory.CreateDirectory(sessions);
            Paths = new ContinuePaths(home, sessions);
        }

        public ContinuePaths Paths { get; }

        public string WriteSession(string sessionId, string title, string? declaredId = null)
        {
            var path = Path.Combine(Paths.Sessions, sessionId + ".json");
            var document = new JsonObject
            {
                ["sessionId"] = declaredId ?? sessionId,
                ["title"] = title,
                ["workspaceDirectory"] = "file:///c%3A/Repos/Reborn",
                ["history"] = new JsonArray
                {
                    Turn("user", new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "hi" } }),
                    Turn("assistant", "Hello!")
                },
                ["mode"] = "agent",
                ["chatModelTitle"] = "qwen3 8b"
            };
            File.WriteAllText(path, document.ToJsonString());
            return path;
        }

        public JsonObject Entry(string sessionId, string title, string dateCreated) => new()
        {
            ["sessionId"] = sessionId,
            ["title"] = title,
            ["dateCreated"] = dateCreated,
            ["workspaceDirectory"] = "file:///c%3A/Repos/Reborn",
            ["messageCount"] = 2
        };

        public void WriteIndex(params JsonObject[] entries) =>
            File.WriteAllText(Paths.IndexFilePath, ContinueSessionIndex.Serialize(entries));

        public string ReadIndex() =>
            File.Exists(Paths.IndexFilePath) ? File.ReadAllText(Paths.IndexFilePath) : string.Empty;

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static JsonObject Turn(string role, JsonNode content) => new()
        {
            ["message"] = new JsonObject { ["role"] = role, ["content"] = content },
            ["contextItems"] = new JsonArray()
        };
    }
}
