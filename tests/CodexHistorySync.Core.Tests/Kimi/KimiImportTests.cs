using System.Text;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Kimi;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Tests.Kimi;

public sealed class KimiImportTests : IDisposable
{
    private const string SessionId = KimiHomeFixture.MainSessionId;
    private const string LocalSessionId = KimiHomeFixture.SecondSessionId;
    private static readonly string WorkDir = Path.Combine(Path.GetTempPath(), "kimi-import-source-work");

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-kimi-import-{Guid.NewGuid():N}");

    [Fact]
    public async Task AnImportWritesTheSessionAndListsItAlongsideTheLocalOnes()
    {
        var fixture = CreateFixture();
        var foreignIndexLine = "{\"sessionId\":\"session_" + LocalSessionId +
            "\",\"sessionDir\":\"C:/other/session_" + LocalSessionId + "\",\"workDir\":\"C:/other\"}";
        File.WriteAllText(fixture.Paths.IndexFilePath, foreignIndexLine);
        var package = SourcePackage("arriving");

        var result = await fixture.ImportAsync(package);

        Assert.Equal(ImportApplyResult.Applied, result);
        var sessionDirectory = fixture.SessionDirectory(SessionId);
        Assert.True(File.Exists(Path.Combine(sessionDirectory, KimiSessionPackage.StateFileName)));
        Assert.True(File.Exists(Path.Combine(sessionDirectory, "agents", "main", KimiSessionPackage.WireFileName)));

        var lines = File.ReadAllLines(fixture.Paths.IndexFilePath);
        Assert.Equal(2, lines.Length);
        Assert.Equal(foreignIndexLine, lines[0]);
        var entry = KimiSessionIndex.Find(KimiSessionIndex.Parse(lines[1]), KimiPaths.SessionIdPrefix + SessionId);
        Assert.NotNull(entry);
        Assert.Equal(sessionDirectory.Replace('\\', '/'), entry["sessionDir"]!.GetValue<string>());
        Assert.Equal(WorkDir.Replace('\\', '/'), entry["workDir"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMalformedIndexStopsTheImportInsteadOfBeingReplaced()
    {
        var fixture = CreateFixture();
        File.WriteAllText(fixture.Paths.IndexFilePath, "{ this is not jsonl");
        var package = SourcePackage("arriving");

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync(package));

        Assert.Equal("{ this is not jsonl", File.ReadAllText(fixture.Paths.IndexFilePath));
        Assert.False(Directory.Exists(fixture.SessionDirectory(SessionId)));
    }

    [Fact]
    public async Task TheIndexIsBackedUpBeforeItIsChanged()
    {
        var fixture = CreateFixture();
        File.WriteAllText(fixture.Paths.IndexFilePath,
            KimiSessionIndex.SerializeEntry(KimiSessionIndex.CreateEntry(
                KimiPaths.SessionIdPrefix + LocalSessionId, "C:/other/session", "C:/other")));
        var before = File.ReadAllText(fixture.Paths.IndexFilePath);

        await fixture.ImportAsync(SourcePackage("arriving"));

        var backups = Directory.GetFiles(fixture.BackupRoot, "content.bin", SearchOption.AllDirectories);
        Assert.Contains(backups, path => File.ReadAllText(path) == before);
    }

    [Fact]
    public async Task AnImportIntoAHomeWithNoIndexCreatesOne()
    {
        var fixture = CreateFixture();

        await fixture.ImportAsync(SourcePackage("first ever"));

        var entry = Assert.Single(KimiSessionIndex.Parse(File.ReadAllText(fixture.Paths.IndexFilePath)));
        Assert.Equal(KimiPaths.SessionIdPrefix + SessionId, KimiSessionIndex.SessionIdOf(entry));
    }

    [Fact]
    public async Task AnImportedSessionRehashesToTheObjectThatWasAuthenticated()
    {
        var fixture = CreateFixture();
        var package = SourcePackage("arriving");

        await fixture.ImportAsync(package);

        var rebuilt = KimiSessionPackage.BuildFromDirectory(
            fixture.SessionDirectory(SessionId), KimiPaths.ComputeWorkDirKey(WorkDir));
        Assert.Equal(KimiSessionPackage.HashPackage(package), KimiSessionPackage.HashPackage(rebuilt));
    }

    [Fact]
    public async Task ASecondIdenticalImportAppliesCleanly()
    {
        var fixture = CreateFixture();
        var package = SourcePackage("arriving");
        await fixture.ImportAsync(package);

        var current = KimiSessionPackage.HashPackage(KimiSessionPackage.BuildFromDirectory(
            fixture.SessionDirectory(SessionId), KimiPaths.ComputeWorkDirKey(WorkDir)));
        var result = await fixture.ImportAsync(package, ExpectedHistoryState.Present(current));

        Assert.Equal(ImportApplyResult.Applied, result);
    }

    [Fact]
    public async Task AHashMismatchIsRefused()
    {
        var fixture = CreateFixture();
        var package = SourcePackage("arriving");
        var tampered = TamperTitle(package);

        // The authenticated hash comes from the repository index, so it describes the original
        // bytes even when the staged plaintext has been altered.
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ImportAsync(
            tampered, hashOverride: KimiSessionPackage.HashPackage(package)));
    }

    [Fact]
    public async Task ADestinationOutsideTheSessionLayoutIsRejected()
    {
        var fixture = CreateFixture();
        var package = SourcePackage("arriving");
        var incoming = new LocalObject(
            new LogicalObjectId(KimiSessionPackage.ToLogicalId(SessionId)),
            ObjectKind.KimiSession,
            fixture.Paths.IndexFilePath,
            KimiSessionPackage.HashPackage(package),
            package.LongLength,
            DateTimeOffset.UtcNow);
        using var stream = new MemoryStream(package);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Writer.ImportAsync(incoming, stream, "operation-1", ExpectedHistoryState.Absent, CancellationToken.None));
    }

    [Fact]
    public async Task ATombstoneDeletesTheSessionAndItsIndexLineAndKeepsTheRest()
    {
        var fixture = CreateFixture();
        var package = SourcePackage("leaving");
        await fixture.ImportAsync(package);
        var foreignIndexLine = "{\"sessionId\":\"session_" + LocalSessionId +
            "\",\"sessionDir\":\"C:/other/session_" + LocalSessionId + "\",\"workDir\":\"C:/other\"}";
        File.WriteAllText(fixture.Paths.IndexFilePath,
            File.ReadAllText(fixture.Paths.IndexFilePath) + "\n" + foreignIndexLine);
        var sessionDirectory = fixture.SessionDirectory(SessionId);
        var baseline = KimiSessionPackage.HashPackage(package);

        var result = await fixture.Writer.ApplyTombstoneAsync(
            new LocalObject(
                new LogicalObjectId(KimiSessionPackage.ToLogicalId(SessionId)),
                ObjectKind.KimiSession,
                Path.Combine(sessionDirectory, KimiSessionPackage.StateFileName),
                baseline,
                package.LongLength,
                DateTimeOffset.UtcNow),
            baseline, "delete-1", CancellationToken.None);

        Assert.Equal(TombstoneApplyResult.Applied, result);
        Assert.False(Directory.Exists(sessionDirectory));
        var remaining = KimiSessionIndex.Parse(File.ReadAllText(fixture.Paths.IndexFilePath));
        var entry = Assert.Single(remaining);
        Assert.Equal(KimiPaths.SessionIdPrefix + LocalSessionId, KimiSessionIndex.SessionIdOf(entry));
        var backups = Directory.GetFiles(fixture.BackupRoot, "content.bin", SearchOption.AllDirectories);
        Assert.NotEmpty(backups);
    }

    [Fact]
    public async Task ATombstoneConflictsWhenTheSessionChangedSinceBaseline()
    {
        var fixture = CreateFixture();
        var package = SourcePackage("leaving");
        await fixture.ImportAsync(package);
        var sessionDirectory = fixture.SessionDirectory(SessionId);
        var statePath = Path.Combine(sessionDirectory, KimiSessionPackage.StateFileName);
        File.WriteAllText(statePath, File.ReadAllText(statePath).Replace("leaving", "changed afterwards"));

        var result = await fixture.Writer.ApplyTombstoneAsync(
            new LocalObject(
                new LogicalObjectId(KimiSessionPackage.ToLogicalId(SessionId)),
                ObjectKind.KimiSession,
                statePath,
                KimiSessionPackage.HashPackage(package),
                package.LongLength,
                DateTimeOffset.UtcNow),
            KimiSessionPackage.HashPackage(package), "delete-1", CancellationToken.None);

        Assert.Equal(TombstoneApplyResult.Conflict, result);
        Assert.True(Directory.Exists(sessionDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private byte[] SourcePackage(string title)
    {
        var sourceHome = Path.Combine(root, "source", ".kimi-code");
        var sourceSessions = Path.Combine(sourceHome, "sessions");
        Directory.CreateDirectory(sourceSessions);
        var workDirKey = KimiPaths.ComputeWorkDirKey(WorkDir);
        var sessionDirectory = Path.Combine(sourceSessions, workDirKey, KimiPaths.SessionIdPrefix + SessionId);
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "agents", "main"));
        File.WriteAllText(Path.Combine(sessionDirectory, KimiSessionPackage.StateFileName), """
            {"id":"session_10000000-0000-0000-0000-000000000001","version":2,"cwd":"__WORKDIR__","createdAt":1789426633555,"updatedAt":1789426974020,"archived":false,"agents":{"main":{"homedir":"$KIMI_SESSION_DIR/agents/main","type":"main"}},"custom":{},"lastPrompt":"arriving","title":"__TITLE__","titleKind":"replaceable","isCustomTitle":false}
            """.Replace("__WORKDIR__", WorkDir.Replace('\\', '/')).Replace("__TITLE__", title), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(sessionDirectory, "agents", "main", KimiSessionPackage.WireFileName),
            "{\"type\":\"metadata\",\"protocol_version\":\"1.5\"}\n" +
            "{\"type\":\"agent.message.appended\",\"message\":{\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"arriving\"}]},\"meta\":{}},\"time\":1}\n",
            new UTF8Encoding(false));
        return KimiSessionPackage.BuildFromDirectory(sessionDirectory, workDirKey);
    }

    private static byte[] TamperTitle(byte[] package)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(package))!;
        var files = (System.Text.Json.Nodes.JsonArray)root["files"]!;
        var state = System.Text.Json.Nodes.JsonNode.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(files[0]!["contentBase64"]!.GetValue<string>())))!;
        state["title"] = "tampered";
        files[0]!["contentBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(state.ToJsonString()));
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private ImportFixture CreateFixture()
    {
        var codexHome = Path.Combine(root, "codex");
        Directory.CreateDirectory(codexHome);
        var codexPaths = CodexPaths.Resolve(codexHome);
        var kimiHome = Path.Combine(root, "target", ".kimi-code");
        var kimiSessions = Path.Combine(kimiHome, "sessions");
        Directory.CreateDirectory(kimiSessions);
        var kimiPaths = new KimiPaths(kimiHome, kimiSessions);
        var local = Path.Combine(root, "local");
        var backups = new BackupStore("repo", local, codexPaths, kimiPaths: kimiPaths);
        var writer = new CodexHistoryWriter(codexPaths, backups, new StoppedDetector(), kimiPaths: kimiPaths);
        return new ImportFixture(kimiPaths, writer, backups.RootPath, WorkDir);
    }

    private sealed record ImportFixture(KimiPaths Paths, CodexHistoryWriter Writer, string BackupRoot, string WorkDir)
    {
        public string SessionDirectory(string sessionId) =>
            Paths.SessionDirectory(KimiPaths.ComputeWorkDirKey(WorkDir), KimiPaths.SessionIdPrefix + sessionId);

        public async Task<ImportApplyResult> ImportAsync(byte[] package, ExpectedHistoryState? expected = null,
            ContentHash? hashOverride = null)
        {
            var destination = Path.Combine(SessionDirectory(SessionId), KimiSessionPackage.StateFileName);
            var incoming = new LocalObject(
                new LogicalObjectId(KimiSessionPackage.ToLogicalId(SessionId)),
                ObjectKind.KimiSession,
                destination,
                hashOverride ?? KimiSessionPackage.HashPackage(package),
                package.LongLength,
                DateTimeOffset.UtcNow);
            using var stream = new MemoryStream(package);
            return await Writer.ImportAsync(incoming, stream, "operation-1", expected ?? ExpectedHistoryState.Absent, CancellationToken.None);
        }
    }

    private sealed class StoppedDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
