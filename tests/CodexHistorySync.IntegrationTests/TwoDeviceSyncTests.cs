using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;

namespace CodexHistorySync.IntegrationTests;

public sealed class TwoDeviceSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-engine-{Guid.NewGuid():N}");

    [Fact]
    public async Task BidirectionalSync_TwoDevicesConvergeWithoutRemotePlaintext()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key);
        const string promptMarker = "prompt marker never stored remotely";
        await WriteSessionAsync(first.Paths.Sessions, "session-a", promptMarker);
        await WriteSessionAsync(second.Paths.Sessions, "session-b", "second device text");
        const string claudeProject = "c--Repos-Demo";
        const string claudeId = "40000000-0000-0000-0000-000000000004";
        const string claudeMarker = "claude turn never stored remotely";
        var claudeSource = await WriteClaudeSessionAsync(first.ClaudePaths, claudeProject, claudeId, claudeMarker);

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        var firstHashes = (await new SessionScanner(TimeSpan.Zero).ScanAsync(first.Paths, CancellationToken.None)).Select(x => x.Hash).OrderBy(x => x.Hex).ToArray();
        var secondHashes = (await new SessionScanner(TimeSpan.Zero).ScanAsync(second.Paths, CancellationToken.None)).Select(x => x.Hash).OrderBy(x => x.Hex).ToArray();
        Assert.Equal(2, firstHashes.Length);
        Assert.Equal(firstHashes, secondHashes);
        var claudeCopy = Path.Combine(second.ClaudePaths.Projects, claudeProject, claudeId + ".jsonl");
        Assert.True(File.Exists(claudeCopy), "the Claude transcript was not materialized on the second device");
        Assert.Equal(File.ReadAllBytes(claudeSource), File.ReadAllBytes(claudeCopy));
        Assert.DoesNotContain(claudeMarker, ReadAllRemoteBytesAsText(remote), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(remote, "*.jsonl", SearchOption.AllDirectories));
        Assert.DoesNotContain(promptMarker, ReadAllRemoteBytesAsText(remote), StringComparison.Ordinal);
        Assert.All(Directory.EnumerateFiles(Path.Combine(first.ProviderRoot, "repository", "git"), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")),
            path => Assert.Equal(".chs", Path.GetExtension(path)));
    }

    [Fact]
    public async Task ASessionExcludedAsASubagentThreadIsNotDeletedFromTheOtherDevice()
    {
        // Dropping a session from the scan must not read as a deletion. A tombstone published
        // for an excluded session would erase the transcript on every device that pulls it.
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key);
        await WriteSessionAsync(first.Paths.Sessions, "session-a", "shared text");

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        var copy = Path.Combine(second.Paths.Sessions, "session-a.jsonl");
        Assert.True(File.Exists(copy), "the first sync did not materialize the session on the second device");

        // The same id, now carrying a subagent marker: the scan stops returning it.
        await WriteSubagentSessionAsync(first.Paths.Sessions, "session-a", "shared text");
        await WriteSubagentSessionAsync(first.Paths.Sessions, "session-b", "subagent only");
        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.True(File.Exists(copy), "the second device lost a transcript because an excluded session read as deleted");
        Assert.False(File.Exists(Path.Combine(second.Paths.Sessions, "session-b.jsonl")),
            "a subagent thread reached the second device");
        Assert.DoesNotContain("subagent only", ReadAllRemoteBytesAsText(remote), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnnotationReachesTheOtherDeviceAndIsNeverStoredInPlaintext()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key);
        const string titleMarker = "QR unlock on the club machines";
        const string descriptionMarker = "description never stored remotely";
        var annotated = new SessionAnnotationKey(ManagedAgent.Claude, "40000000-0000-0000-0000-000000000004");
        await first.Annotations.SaveAsync(annotated, new SessionAnnotation(
            titleMarker, descriptionMarker, SessionAnnotationSource.Generated, "digest-hash", "qwen3:8b",
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)), CancellationToken.None);

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        var arrived = Assert.Single(await second.Annotations.LoadAsync(CancellationToken.None));
        Assert.Equal(annotated, arrived.Key);
        Assert.Equal(titleMarker, arrived.Value.Title);
        Assert.Equal(descriptionMarker, arrived.Value.Description);
        Assert.Equal(SessionAnnotationSource.Generated, arrived.Value.Source);
        Assert.Equal("digest-hash", arrived.Value.DigestHash);
        // A title is conversation-shaped text; it is encrypted like everything else.
        var remoteText = ReadAllRemoteBytesAsText(remote);
        Assert.DoesNotContain(titleMarker, remoteText, StringComparison.Ordinal);
        Assert.DoesNotContain(descriptionMarker, remoteText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoDevicesEditingOneClaudeSessionRecordAConflictRatherThanFailing()
    {
        // A conflict has to be preparable for every kind, and Claude is the case that proves it:
        // its object hash covers an assembled package, not the transcript on disk. Comparing the
        // raw file against that hash could never match, so preparing this conflict threw
        // "Local conflict version changed after stable scanning" about a file nobody had touched -
        // and because that throw landed mid-apply, it also left an unrecoverable mutation journal.
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key);
        const string project = "c--Repos-Demo";
        const string sessionId = "50000000-0000-0000-0000-000000000005";

        await WriteClaudeSessionAsync(first.ClaudePaths, project, sessionId, "written on the first device");
        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        // The same session id, never synchronized here, with different content: local and remote
        // both claim it and no baseline reconciles them.
        await WriteClaudeSessionAsync(second.ClaudePaths, project, sessionId, "written on the second device");
        var result = await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.Equal(1, result.Conflicts);
        // The local side of the record must be the package, the same bytes staging would have
        // sent - storing the raw transcript here would export the wrong side on resolution.
        var conflict = Assert.Single(await second.Conflicts.ListAsync(CancellationToken.None));
        var expected = ClaudeSessionPackage.HashPackage(ClaudeSessionPackage.BuildFromFile(
            Path.Combine(second.ClaudePaths.Projects, project, sessionId + ".jsonl")));
        Assert.Equal(expected.Hex, conflict.Provenance.LocalHash.Hex);
    }

    [Fact]
    public async Task ARemovedAnnotationIsRemovedFromTheOtherDevice()
    {
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key);
        var annotated = new SessionAnnotationKey(ManagedAgent.Codex, "codex-session");
        await first.Annotations.SaveAsync(annotated, new SessionAnnotation(
            "Named once", null, SessionAnnotationSource.Edited, "digest-hash", null,
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)), CancellationToken.None);
        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        Assert.Single(await second.Annotations.LoadAsync(CancellationToken.None));

        await first.Annotations.DeleteAsync(annotated, CancellationToken.None);
        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.Empty(await second.Annotations.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASessionWhoseAgentHasNoHomeIsDeferredWhileTheRestOfTheRepositoryArrives()
    {
        // A machine that never installed Claude Code still has to receive its Codex history.
        // Staging a Claude session there threw "Claude paths are not configured." out of the
        // whole run, so one foreign session withheld the entire repository from the node - all
        // 1073 sessions of it, and the only workaround was to create the directory by hand.
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key, withClaudeHome: false);
        const string project = "c--Repos-Demo";
        const string sessionId = "60000000-0000-0000-0000-000000000006";

        await WriteSessionAsync(first.Paths.Sessions, "session-a", "codex text");
        await WriteClaudeSessionAsync(first.ClaudePaths, project, sessionId, "claude text");
        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        var result = await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.Equal(1, result.SkippedNoAgentHome);
        Assert.Equal(1, result.Downloaded);
        Assert.Equal(0, result.Conflicts);
        Assert.True(File.Exists(Path.Combine(second.Paths.Sessions, "session-a.jsonl")),
            "a Claude session this machine cannot place withheld the Codex session as well");
        Assert.False(Directory.Exists(second.ClaudePaths.Projects),
            "a Claude home was invented on a machine that has none");

        // The deferral must stay out of the baseline. Installing Claude Code later can only bring
        // the session down if the next run still plans the download it skipped.
        var again = await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.Equal(1, again.SkippedNoAgentHome);
        Assert.Equal(0, again.Downloaded);
    }

    [Fact]
    public async Task LosingAnAgentHomeDoesNotEraseThatAgentsSessionsFromTheRepository()
    {
        // An absence can only be read as a deletion when the scan actually looked. A machine with
        // no home for an agent never scans that kind at all, so every one of its sessions in the
        // baseline reads as locally deleted, and one run publishes tombstones that erase them on
        // every other machine.
        Directory.CreateDirectory(_root);
        var remote = Path.Combine(_root, "remote.git");
        await GitAsync(_root, "init", "--bare", "--initial-branch=main", remote);
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var first = CreateDevice("first", remote, key);
        var second = CreateDevice("second", remote, key);
        const string project = "c--Repos-Demo";
        const string sessionId = "70000000-0000-0000-0000-000000000007";
        var original = Path.Combine(first.ClaudePaths.Projects, project, sessionId + ".jsonl");

        await WriteClaudeSessionAsync(first.ClaudePaths, project, sessionId, "written before the uninstall");
        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(second.ClaudePaths.Projects, project, sessionId + ".jsonl")),
            "the session never reached the second device, so the test proves nothing");

        // Claude Code is uninstalled on the second machine: ClaudePaths.TryResolve finds no home
        // and returns null, so the engine is built without it. The device state is untouched and
        // still lists the session.
        second.Engine.Dispose();
        Directory.Delete(second.ClaudePaths.Home, recursive: true);
        var uninstalled = CreateDevice("second", remote, key, withClaudeHome: false);
        var run = await uninstalled.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        // A tombstone publication counts as an upload. This machine wrote nothing and deleted
        // nothing, so it has nothing to publish.
        Assert.Equal(0, run.Uploaded);

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        Assert.True(File.Exists(original),
            "a machine that lost its Claude home published a tombstone and erased the session everywhere");
    }

    /// <param name="withClaudeHome">
    /// False models a machine where Claude Code was never installed: the resolver finds no
    /// <c>projects</c> directory, so the engine is handed no Claude paths at all. The device
    /// still carries the paths it would have had, so a test can assert nothing was written there.
    /// </param>
    private Device CreateDevice(string name, string remote, byte[] key, bool withClaudeHome = true)
    {
        var home = Path.Combine(_root, name, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        Directory.CreateDirectory(paths.Sessions);
        var local = Path.Combine(_root, name, "local");
        var providerRoot = Path.Combine(_root, name, "provider");
        var claudeHome = Path.Combine(_root, name, "claude");
        var claudePaths = new ClaudePaths(claudeHome, Path.Combine(claudeHome, "projects"));
        var configuredClaudePaths = withClaudeHome ? claudePaths : null;
        if (withClaudeHome) Directory.CreateDirectory(claudePaths.Projects);
        var annotations = new SessionAnnotationStore(local);
        var conflicts = new ConflictStore("repository", local, paths);
        var backups = new BackupStore("repository", local, paths, claudePaths: configuredClaudePaths,
            annotationsDirectory: annotations.Directory);
        var engine = new SyncEngine(
            "repository", name, paths, key,
            new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(), new LocalStateStore(local),
            new CodexHistoryWriter(paths, backups, new StoppedCodexDetector(), claudePaths: configuredClaudePaths,
                annotationsDirectory: annotations.Directory),
            conflicts,
            new GitStorageProvider("repository", remote, GitRemoteKind.Local, providerRoot),
            Path.Combine(local, "staging"),
            claudePaths: configuredClaudePaths,
            // A real process probe would defer every freshly written transcript (design D3).
            claudeScanner: withClaudeHome
                ? new ClaudeSessionScanner(_ => Task.CompletedTask, isClaudeRunning: () => false)
                : null,
            annotationsDirectory: annotations.Directory);
        return new Device(paths, claudePaths, providerRoot, engine, annotations, conflicts);
    }

    private static async Task WriteSessionAsync(string directory, string id, string text)
    {
        Directory.CreateDirectory(directory);
        var content = $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}\n{{\"type\":\"message\",\"payload\":{{\"text\":\"{text}\"}}}}\n";
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".jsonl"), content, new UTF8Encoding(false));
    }

    private static async Task WriteSubagentSessionAsync(string directory, string id, string text)
    {
        Directory.CreateDirectory(directory);
        var content = $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\",\"thread_source\":\"subagent\"}}}}\n" +
            $"{{\"type\":\"message\",\"payload\":{{\"text\":\"{text}\"}}}}\n";
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".jsonl"), content, new UTF8Encoding(false));
    }

    private static async Task<string> WriteClaudeSessionAsync(ClaudePaths paths, string project, string sessionId, string text)
    {
        var directory = Path.Combine(paths.Projects, project);
        Directory.CreateDirectory(directory);
        var transcript = Path.Combine(directory, sessionId + ".jsonl");
        var record = $"{{\"type\":\"user\",\"cwd\":\"{JsonEncodedText.Encode(directory)}\",\"sessionId\":\"{sessionId}\"," +
            $"\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}\n";
        await File.WriteAllTextAsync(transcript, record, new UTF8Encoding(false));
        return transcript;
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
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private sealed record Device(
        CodexPaths Paths,
        ClaudePaths ClaudePaths,
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
