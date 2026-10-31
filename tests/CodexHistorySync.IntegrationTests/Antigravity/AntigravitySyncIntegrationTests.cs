using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using Xunit;

namespace CodexHistorySync.IntegrationTests.Antigravity;

public sealed class AntigravitySyncIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-antigravity-{Guid.NewGuid():N}");

    [Fact]
    public async Task Writer_ImportAntigravityPackage_MaterializesDatabaseRowsTranscriptsAndAnchor()
    {
        Directory.CreateDirectory(_root);
        var sourceDir = Path.Combine(_root, "source-ag");
        using var fixture = new AntigravityTestFixture(sourceDir);
        var sessionId = Guid.NewGuid().ToString("D");
        fixture.InsertSummaryRow(sessionId, "Test Import Title");
        fixture.InsertUserStep(sessionId, 0, "Hello user step");
        fixture.InsertAssistantStep(sessionId, 1, "Hello assistant reply");
        fixture.WriteTranscript(sessionId, [("user", "Hello user step"), ("model", "Hello assistant reply")]);
        fixture.WriteAnnotation(sessionId, "user_note: important");
        fixture.WriteBrainArtifact(sessionId, "artifacts/plan.md", "# Plan\nDo sync");

        var packageBytes = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        var logicalId = AntigravitySessionPackage.LogicalIdOf(packageBytes);
        var expectedHash = AntigravitySessionPackage.HashPackage(packageBytes);

        var targetHome = Path.Combine(_root, "target-ag");
        var targetPaths = new AntigravityPaths(targetHome);
        Directory.CreateDirectory(targetPaths.ConversationsDirectory);
        Directory.CreateDirectory(targetPaths.AnnotationsDirectory);
        Directory.CreateDirectory(targetPaths.BrainDirectory);
        Directory.CreateDirectory(targetPaths.AnchorRoot);

        var codexHome = Path.Combine(_root, "codex");
        Directory.CreateDirectory(codexHome);
        var codexPaths = CodexPaths.Resolve(codexHome);
        var backups = new BackupStore("repo", Path.Combine(_root, "local"), codexPaths, antigravityPaths: targetPaths);
        var writer = new CodexHistoryWriter(codexPaths, backups, new StoppedCodexDetector(), antigravityPaths: targetPaths);

        var destination = targetPaths.AnchorPath(sessionId);
        var incoming = new LocalObject(
            new LogicalObjectId(logicalId),
            ObjectKind.AntigravitySession,
            destination,
            expectedHash,
            packageBytes.LongLength,
            DateTimeOffset.UtcNow);

        using var stream = new MemoryStream(packageBytes);
        var result = await writer.ImportAsync(incoming, stream, "import-1", ExpectedHistoryState.Absent, CancellationToken.None);
        Assert.Equal(ImportApplyResult.Applied, result);

        Assert.True(File.Exists(targetPaths.ConversationDatabasePath(sessionId)));
        Assert.True(File.Exists(destination));
        Assert.Equal(expectedHash, AntigravitySessionPackage.HashAnchor(destination));

        var readPackage = AntigravitySessionPackage.BuildFromLocal(targetPaths, sessionId);
        Assert.Equal(logicalId, AntigravitySessionPackage.LogicalIdOf(readPackage));
        Assert.Equal(expectedHash, AntigravitySessionPackage.HashPackage(readPackage));
    }

    [Fact]
    public async Task Writer_ApplyAntigravityTombstone_RemovesSessionArtifacts()
    {
        Directory.CreateDirectory(_root);
        var targetHome = Path.Combine(_root, "target-tombstone-ag");
        using var fixture = new AntigravityTestFixture(targetHome);
        var sessionId = Guid.NewGuid().ToString("D");
        fixture.InsertSummaryRow(sessionId, "To Be Tombstoned");
        fixture.InsertUserStep(sessionId, 0, "temporary prompt");
        fixture.WriteTranscript(sessionId, [("user", "temporary prompt")]);

        var packageBytes = AntigravitySessionPackage.BuildFromLocal(fixture.Paths, sessionId);
        var logicalId = AntigravitySessionPackage.LogicalIdOf(packageBytes);
        var hash = AntigravitySessionPackage.HashPackage(packageBytes);
        var destination = fixture.Paths.AnchorPath(sessionId);
        AntigravitySessionPackage.WriteAnchorSnapshot(destination);

        var codexHome = Path.Combine(_root, "codex");
        Directory.CreateDirectory(codexHome);
        var codexPaths = CodexPaths.Resolve(codexHome);
        var backups = new BackupStore("repo", Path.Combine(_root, "local"), codexPaths, antigravityPaths: fixture.Paths);
        var writer = new CodexHistoryWriter(codexPaths, backups, new StoppedCodexDetector(), antigravityPaths: fixture.Paths);

        var incoming = new LocalObject(
            new LogicalObjectId(logicalId),
            ObjectKind.AntigravitySession,
            destination,
            hash,
            packageBytes.LongLength,
            DateTimeOffset.UtcNow);

        var tombstoneResult = await writer.ApplyTombstoneAsync(incoming, hash, "tombstone-1", CancellationToken.None);
        Assert.Equal(TombstoneApplyResult.Applied, tombstoneResult);

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(fixture.Paths.ConversationDatabasePath(sessionId)));
        Assert.Null(AntigravitySessionPackage.TryBuild(fixture.Paths, sessionId));
    }

    [Fact]
    public async Task Sync_TwoDevices_PropagatesAntigravitySessionViaGitRemote()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);

        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key);

        var sessionId = Guid.NewGuid().ToString("D");
        const string secretMarker = "agy marker never stored remotely in plaintext";

        using var firstFixture = new AntigravityTestFixture(first.AntigravityPaths.Home);
        firstFixture.InsertSummaryRow(sessionId, "Remote Sync Session");
        firstFixture.InsertUserStep(sessionId, 0, secretMarker);
        firstFixture.InsertAssistantStep(sessionId, 1, "reply to marker");
        firstFixture.WriteTranscript(sessionId, [("user", secretMarker), ("model", "reply to marker")]);

        var scanner = new AntigravitySessionScanner(TimeSpan.Zero, TimeSpan.Zero);
        var preScan = await scanner.ScanDetailedAsync(first.AntigravityPaths, CancellationToken.None);
        Assert.Single(preScan.Objects);

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.DoesNotContain(secretMarker, ReadAllRemoteBytesAsText(remote), StringComparison.Ordinal);
        Assert.All(Directory.EnumerateFiles(Path.Combine(first.ProviderRoot, "repository", "git"), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")),
            path => Assert.Equal(".chs", Path.GetExtension(path)));

        Assert.True(File.Exists(second.AntigravityPaths.ConversationDatabasePath(sessionId)));
        Assert.True(File.Exists(second.AntigravityPaths.AnchorPath(sessionId)));

        var firstHash = AntigravitySessionPackage.HashAnchor(first.AntigravityPaths.AnchorPath(sessionId));
        var secondHash = AntigravitySessionPackage.HashAnchor(second.AntigravityPaths.AnchorPath(sessionId));
        Assert.NotNull(firstHash);
        Assert.NotNull(secondHash);
        Assert.Equal(firstHash.Value, secondHash.Value);
    }

    [Fact]
    public async Task Sync_Tombstone_RemovesAntigravitySessionOnPeer()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);

        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key);

        var sessionId = Guid.NewGuid().ToString("D");
        using var firstFixture = new AntigravityTestFixture(first.AntigravityPaths.Home);
        firstFixture.InsertSummaryRow(sessionId, "Tombstone Test Session");
        firstFixture.InsertUserStep(sessionId, 0, "session to be deleted");

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.True(File.Exists(second.AntigravityPaths.ConversationDatabasePath(sessionId)));

        AntigravitySessionPackage.DeleteAnchor(first.AntigravityPaths.AnchorPath(sessionId));

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.False(File.Exists(second.AntigravityPaths.ConversationDatabasePath(sessionId)));
        Assert.False(File.Exists(second.AntigravityPaths.AnchorPath(sessionId)));
    }

    [Fact]
    public async Task Sync_DeviceWithoutAntigravityHome_DefersAntigravitySessionWithoutError()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);

        var first = CreateDevice("first", remote, key, withAntigravityHome: true);
        var second = CreateDevice("second", remote, key, withAntigravityHome: false);

        var sessionId = Guid.NewGuid().ToString("D");
        using var firstFixture = new AntigravityTestFixture(first.AntigravityPaths.Home);
        firstFixture.InsertSummaryRow(sessionId, "Deferral Test Session");
        firstFixture.InsertUserStep(sessionId, 0, "prompt");

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        var syncResult = await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        Assert.True(syncResult.SkippedNoAgentHome >= 1);
        Assert.False(Directory.Exists(second.AntigravityPaths.Home));
    }

    private Device CreateDevice(string name, string remote, byte[] key, bool withAntigravityHome = true)
    {
        var home = Path.Combine(_root, name, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        Directory.CreateDirectory(paths.Sessions);
        var local = Path.Combine(_root, name, "local");
        var providerRoot = Path.Combine(_root, name, "provider");

        var antigravityHome = Path.Combine(_root, name, "antigravity-cli");
        var antigravityPaths = new AntigravityPaths(antigravityHome);
        var configuredAntigravityPaths = withAntigravityHome ? antigravityPaths : null;
        if (withAntigravityHome)
        {
            Directory.CreateDirectory(antigravityPaths.ConversationsDirectory);
            Directory.CreateDirectory(antigravityPaths.AnnotationsDirectory);
            Directory.CreateDirectory(antigravityPaths.BrainDirectory);
            Directory.CreateDirectory(antigravityPaths.AnchorRoot);
        }

        var annotations = new SessionAnnotationStore(local);
        var conflicts = new ConflictStore("repository", local, paths);
        var backups = new BackupStore("repository", local, paths,
            annotationsDirectory: annotations.Directory,
            antigravityPaths: configuredAntigravityPaths);

        var writer = new CodexHistoryWriter(paths, backups, new StoppedCodexDetector(),
            annotationsDirectory: annotations.Directory,
            antigravityPaths: configuredAntigravityPaths);

        var engine = new SyncEngine(
            "repository", name, paths, key,
            new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(), new LocalStateStore(local),
            writer, conflicts,
            new GitStorageProvider("repository", remote, GitRemoteKind.Local, providerRoot),
            Path.Combine(local, "staging"),
            annotationsDirectory: annotations.Directory,
            antigravityPaths: configuredAntigravityPaths,
            antigravityScanner: withAntigravityHome
                ? new AntigravitySessionScanner(TimeSpan.Zero, TimeSpan.Zero)
                : null);

        return new Device(paths, antigravityPaths, providerRoot, engine, annotations, conflicts);
    }

    private static string ReadAllRemoteBytesAsText(string remote) =>
        Encoding.UTF8.GetString(Directory.EnumerateFiles(remote, "*", SearchOption.AllDirectories).SelectMany(File.ReadAllBytes).ToArray());

    private static async Task GitAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Device(
        CodexPaths Paths,
        AntigravityPaths AntigravityPaths,
        string ProviderRoot,
        SyncEngine Engine,
        SessionAnnotationStore Annotations,
        ConflictStore Conflicts);

    private sealed class StoppedCodexDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
