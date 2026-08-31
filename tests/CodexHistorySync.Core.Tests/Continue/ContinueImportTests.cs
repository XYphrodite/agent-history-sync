using System.Text;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Tests.Continue;

public sealed class ContinueImportTests : IDisposable
{
    private const string SessionId = "9490954d-d7dd-4cbe-984c-6172d60bf3dc";
    private const string LocalId = "11111111-1111-1111-1111-111111111111";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-continue-import-{Guid.NewGuid():N}");

    [Fact]
    public async Task AnImportWritesTheSessionAndListsItAlongsideTheLocalOnes()
    {
        var fixture = CreateFixture();
        fixture.WriteIndex(Entry(LocalId, "local only", "1700000000000"));
        var package = SourcePackage("arriving");

        var result = await fixture.ImportAsync(package);

        Assert.Equal(ImportApplyResult.Applied, result);
        Assert.True(File.Exists(fixture.Paths.SessionFilePath(SessionId)));
        var entries = ContinueSessionIndex.Parse(fixture.ReadIndex());
        Assert.Equal(2, entries.Count);
        Assert.Equal("local only", (string?)entries[0]["title"]);
        Assert.Equal("arriving", (string?)entries[1]["title"]);
    }

    [Fact]
    public async Task AMalformedIndexStopsTheImportInsteadOfBeingReplaced()
    {
        // Continue refuses to create a session at all when this file does not parse. Overwriting
        // it would turn one broken file into a lost list of every session on the machine.
        var fixture = CreateFixture();
        File.WriteAllText(fixture.Paths.IndexFilePath, "{ this is not an array");
        var package = SourcePackage("arriving");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync(package));

        Assert.Equal("{ this is not an array", fixture.ReadIndex());
        Assert.False(File.Exists(fixture.Paths.SessionFilePath(SessionId)));
    }

    [Fact]
    public async Task TheIndexIsBackedUpBeforeItIsChanged()
    {
        var fixture = CreateFixture();
        fixture.WriteIndex(Entry(LocalId, "local only", "1700000000000"));
        var before = fixture.ReadIndex();

        await fixture.ImportAsync(SourcePackage("arriving"));

        var backups = Directory.GetFiles(fixture.BackupRoot, "content.bin", SearchOption.AllDirectories);
        Assert.Contains(backups, path => File.ReadAllText(path) == before);
    }

    [Fact]
    public async Task AnImportIntoAHomeWithNoIndexCreatesOne()
    {
        var fixture = CreateFixture();

        await fixture.ImportAsync(SourcePackage("first ever"));

        var entry = Assert.Single(ContinueSessionIndex.Parse(fixture.ReadIndex()));
        Assert.Equal("first ever", (string?)entry["title"]);
    }

    [Fact]
    public async Task AnImportedSessionRehashesToTheObjectThatWasAuthenticated()
    {
        // The writer verifies this itself, so a mismatch would surface as a failed import; the
        // assertion is here to say plainly what the index merge has to preserve.
        var fixture = CreateFixture();
        fixture.WriteIndex(Entry(LocalId, "local only", "1700000000000"));
        var package = SourcePackage("arriving");

        await fixture.ImportAsync(package);

        var rebuilt = ContinueSessionPackage.BuildFromFile(
            fixture.Paths.SessionFilePath(SessionId), fixture.ReadIndex());
        Assert.Equal(ContinueSessionPackage.HashPackage(package), ContinueSessionPackage.HashPackage(rebuilt));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private byte[] SourcePackage(string title)
    {
        var sourceHome = Path.Combine(root, "source", ".continue");
        var sourceSessions = Path.Combine(sourceHome, "sessions");
        Directory.CreateDirectory(sourceSessions);
        var paths = new ContinuePaths(sourceHome, sourceSessions);
        var path = Path.Combine(sourceSessions, SessionId + ".json");
        File.WriteAllText(path, Session(SessionId, title).ToJsonString());
        File.WriteAllText(paths.IndexFilePath,
            ContinueSessionIndex.Serialize([Entry(SessionId, title, "1788134536812")]));
        return ContinueSessionPackage.BuildFromFile(path, File.ReadAllText(paths.IndexFilePath));
    }

    private ImportFixture CreateFixture()
    {
        var codexHome = Path.Combine(root, "codex");
        Directory.CreateDirectory(codexHome);
        var codexPaths = CodexPaths.Resolve(codexHome);
        var continueHome = Path.Combine(root, "target", ".continue");
        var continueSessions = Path.Combine(continueHome, "sessions");
        Directory.CreateDirectory(continueSessions);
        var continuePaths = new ContinuePaths(continueHome, continueSessions);
        var local = Path.Combine(root, "local");
        var backups = new BackupStore("repo", local, codexPaths, continuePaths: continuePaths);
        var writer = new CodexHistoryWriter(codexPaths, backups, new StoppedDetector(),
            continuePaths: continuePaths);
        return new ImportFixture(continuePaths, writer, backups.RootPath);
    }

    private static JsonObject Entry(string sessionId, string title, string dateCreated) => new()
    {
        ["sessionId"] = sessionId,
        ["title"] = title,
        ["dateCreated"] = dateCreated,
        ["workspaceDirectory"] = "file:///c%3A/Repos/Demo",
        ["messageCount"] = 2
    };

    private static JsonObject Session(string sessionId, string title) => new()
    {
        ["sessionId"] = sessionId,
        ["title"] = title,
        ["workspaceDirectory"] = "file:///c%3A/Repos/Demo",
        ["history"] = new JsonArray
        {
            new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = title } }
                }
            }
        },
        ["mode"] = "agent"
    };

    private sealed record ImportFixture(ContinuePaths Paths, CodexHistoryWriter Writer, string BackupRoot)
    {
        public async Task<ImportApplyResult> ImportAsync(byte[] package)
        {
            var destination = Paths.SessionFilePath(SessionId);
            var incoming = new LocalObject(
                new LogicalObjectId(ContinueSessionPackage.ToLogicalId(SessionId)),
                ObjectKind.ContinueSession,
                destination,
                ContinueSessionPackage.HashPackage(package),
                package.LongLength,
                DateTimeOffset.UtcNow);
            var expected = new ExpectedHistoryState(false, null);
            using var stream = new MemoryStream(package);
            return await Writer.ImportAsync(incoming, stream, "operation-1", expected, CancellationToken.None);
        }

        public string ReadIndex() =>
            File.Exists(Paths.IndexFilePath) ? File.ReadAllText(Paths.IndexFilePath, Encoding.UTF8) : string.Empty;

        public void WriteIndex(params JsonObject[] entries) =>
            File.WriteAllText(Paths.IndexFilePath, ContinueSessionIndex.Serialize(entries));
    }

    private sealed class StoppedDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
