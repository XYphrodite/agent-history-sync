using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Remote;
using CodexHistorySync.Windows;

namespace CodexHistorySync.Server.Tests;

public sealed class ServerSyncTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Canary = "SLICE3-PRIVATE-TEXT-TITLE-PATH-773f1b";
    private const string Passphrase = "only clients know this synthetic passphrase";
    private static readonly CancellationToken Ct = CancellationToken.None;

    [PostgresFact]
    public async Task InitJoinAndTwoClientsConvergeForAllScannableKindsWithoutGitOrPlaintext()
    {
        var endpoint = fixture.Endpoint();
        await AssertTwoClientsConvergeAsync(endpoint);
        await AssertNoPlaintextAsync(endpoint.Name);
    }

    internal static async Task AssertTwoClientsConvergeAsync(StoreEndpoint endpoint)
    {
        using var client = new StoreClient(endpoint);
        Assert.Null(await client.ReadSetupAsync(Ct)); // External drills must never reuse an existing repository.
        using var root = new TestDirectory();
        var gateway = new StorageRepositoryGateway(() => throw new InvalidOperationException("Git must not be constructed."));
        var first = CreateDevice(root.Path, "first", gateway);
        var second = CreateDevice(root.Path, "second", gateway);
        Assert.True((await first.Services.VerifyInitializationTargetAsync(endpoint.RepositoryUri.AbsoluteUri, Ct)).Passed);
        var initialized = await first.Services.InitializeAsync(endpoint.RepositoryUri.AbsoluteUri, Passphrase.AsMemory(), Ct);
        var expected = await WriteAllKindsAsync(first);
        var initialPush = await first.Services.SynchronizeAsync(SyncMode.Push, Ct);
        foreach (var kind in new[] { ObjectKind.ActiveSession, ObjectKind.ArchivedSession, ObjectKind.GrokSession,
            ObjectKind.ClaudeSession, ObjectKind.ContinueSession, ObjectKind.SessionAnnotations, ObjectKind.ClaudeMemory })
            Assert.True(initialPush.LocalByKind.TryGetValue(kind, out var totals) && totals.Count > 0, $"Missing synthetic {kind} fixture.");
        Assert.Equal(7, initialPush.Uploaded); // active, archive, Grok, Claude, Continue, annotation, memory
        Assert.True((await second.Services.VerifyPrivateRepositoryAsync(endpoint.RepositoryUri.AbsoluteUri, Ct)).Passed);
        var authentication = await second.Services.AuthenticateRepositoryAsync(endpoint.RepositoryUri.AbsoluteUri, Passphrase.AsMemory(), Ct);
        Assert.True((await second.Services.ProbeCompatibilityAsync(authentication, Ct)).Passed);
        var joinPlan = await second.Services.PlanJoinAsync(authentication, Ct);
        var joined = await second.Services.ApplyJoinAsync(authentication, joinPlan, Ct);
        Assert.Equal(7, joined.Downloaded);
        foreach (var (relative, bytes) in expected)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(System.IO.Path.Combine(second.Root, relative)));
        var annotation = Assert.Single(await second.Annotations.LoadAsync(Ct)).Value;
        Assert.Equal(Canary, annotation.Title);
        var preview = await second.Services.GetStatusAsync(Ct);
        Assert.Equal(0, preview.Pending);

        // A change travels back; two independent edits preserve both versions as a conflict.
        var firstSession = System.IO.Path.Combine(first.Codex.Sessions, "active.jsonl");
        var secondSession = System.IO.Path.Combine(second.Codex.Sessions, "active.jsonl");
        await File.AppendAllTextAsync(secondSession, "{\"type\":\"message\",\"payload\":{\"text\":\"second edit\"}}\n");
        await second.Services.SynchronizeAsync(SyncMode.Bidirectional, Ct);
        await first.Services.SynchronizeAsync(SyncMode.Bidirectional, Ct);
        Assert.Equal(await File.ReadAllBytesAsync(firstSession), await File.ReadAllBytesAsync(secondSession));
        await File.AppendAllTextAsync(firstSession, "{\"type\":\"message\",\"payload\":{\"text\":\"first diverges\"}}\n");
        await File.AppendAllTextAsync(secondSession, "{\"type\":\"message\",\"payload\":{\"text\":\"second diverges\"}}\n");
        await first.Services.SynchronizeAsync(SyncMode.Push, Ct);
        var conflict = await second.Services.SynchronizeAsync(SyncMode.Bidirectional, Ct);
        Assert.True(conflict.Conflicts > 0);
        Assert.NotEmpty(await second.Services.ListConflictsAsync(Ct));

        // Deletion is an encrypted tombstone; no session text or title becomes SQL metadata.
        File.Delete(System.IO.Path.Combine(first.Codex.ArchivedSessions, "archived.jsonl"));
        await first.Services.SynchronizeAsync(SyncMode.Push, Ct);
        await second.Services.SynchronizeAsync(SyncMode.Pull, Ct);
        Assert.False(File.Exists(System.IO.Path.Combine(second.Codex.ArchivedSessions, "archived.jsonl")));
        var config = await first.Local.LoadConfigurationAsync(Ct);
        Assert.Equal(initialized.RepositoryId, config.RepositoryId);
        Assert.Equal(endpoint.RepositoryUri.AbsoluteUri, config.RemoteUrl);
        Assert.Empty(Directory.EnumerateDirectories(root.Path, "git", SearchOption.AllDirectories));
    }

    internal static async Task AssertRestoredProfileAsync(StoreEndpoint endpoint)
    {
        using var root = new TestDirectory();
        var gateway = new StorageRepositoryGateway(() => throw new InvalidOperationException("Git must not be constructed."));
        var restored = CreateDevice(root.Path, "restored", gateway);
        var authentication = await restored.Services.AuthenticateRepositoryAsync(endpoint.RepositoryUri.AbsoluteUri, Passphrase.AsMemory(), Ct);
        Assert.True((await restored.Services.ProbeCompatibilityAsync(authentication, Ct)).Passed);
        var plan = await restored.Services.PlanJoinAsync(authentication, Ct);
        var joined = await restored.Services.ApplyJoinAsync(authentication, plan, Ct);
        Assert.True(joined.Downloaded >= 6); // The preceding drill deleted the archived session.
        foreach (var (directory, pattern) in new[]
        {
            (restored.Codex.Sessions, "*.jsonl"), (restored.Grok.Sessions, "chat_history.jsonl"),
            (restored.Claude.Projects, "*.jsonl"), (restored.Claude.Projects, "MEMORY.md"),
            (restored.Continue.Sessions, "*.json")
        })
        {
            var files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
            Assert.Contains(files, file => File.ReadAllText(file).Contains(Canary, StringComparison.Ordinal));
        }
        Assert.Empty(Directory.EnumerateFiles(restored.Codex.ArchivedSessions));
        Assert.Equal(Canary, Assert.Single(await restored.Annotations.LoadAsync(Ct)).Value.Title);
        Assert.Equal(0, (await restored.Services.GetStatusAsync(Ct)).Pending);
        Assert.Empty(Directory.EnumerateDirectories(root.Path, "git", SearchOption.AllDirectories));
    }

    [PostgresFact]
    public async Task WrongPassphraseAndChangingTargetDoNotWriteLocalConfiguration()
    {
        using var root = new TestDirectory();
        var endpoint = fixture.Endpoint();
        var gateway = new StorageRepositoryGateway(() => throw new InvalidOperationException("Unexpected Git."));
        var first = CreateDevice(root.Path, "first", gateway);
        var second = CreateDevice(root.Path, "second", gateway);
        await first.Services.InitializeAsync(endpoint.RepositoryUri.AbsoluteUri, Passphrase.AsMemory(), Ct);
        await Assert.ThrowsAsync<CliGateException>(() => second.Services.AuthenticateRepositoryAsync(
            endpoint.RepositoryUri.AbsoluteUri, "wrong".AsMemory(), Ct));
        await Assert.ThrowsAsync<CliNotJoinedException>(() => second.Local.LoadConfigurationAsync(Ct));
        var authenticated = await second.Services.AuthenticateRepositoryAsync(endpoint.RepositoryUri.AbsoluteUri, Passphrase.AsMemory(), Ct);
        var plan = await second.Services.PlanJoinAsync(authenticated, Ct);
        var previous = new CliLocalConfiguration(1, "old-repository", "old-device", "https://github.com/example/existing.git", "old-revision");
        await second.Local.SaveConfigurationAsync(previous, Ct);
        await Assert.ThrowsAsync<CliGateException>(() => second.Services.ApplyJoinAsync(authenticated, plan, Ct));
        await Assert.ThrowsAsync<CliGateException>(() => second.Services.InitializeAsync(endpoint.RepositoryUri.AbsoluteUri, Passphrase.AsMemory(), Ct));
        Assert.False((await second.Services.VerifyPrivateRepositoryAsync(endpoint.RepositoryUri.AbsoluteUri, Ct)).Passed);
        Assert.False((await second.Services.VerifyInitializationTargetAsync(endpoint.RepositoryUri.AbsoluteUri, Ct)).Passed);
        Assert.Equal(previous, await second.Local.LoadConfigurationAsync(Ct));
        Assert.Empty(second.Keys.Values);
    }

    private async Task AssertNoPlaintextAsync(string repositoryName)
    {
        // Checking text and its hex encoding alone misses a hex-encoded logical path inside
        // the legacy public CHS1 header. Validate the actual stored binary header as well.
        await using (var command = fixture.Source.CreateCommand("SELECT ciphertext FROM blobs WHERE repository_name = $1"))
        {
            command.Parameters.AddWithValue(repositoryName);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var envelope = (byte[])reader[0];
                Assert.Equal((int)ObjectKind.Attachment, BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(9, 4)));
                Assert.Equal(64, BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(13, 4)));
                Assert.True(StoreProtocol.IsHash(Encoding.UTF8.GetString(envelope, 37, 64)));
            }
        }
        foreach (var table in new[] { "repositories", "blobs", "object_refs" })
        {
            await using var command = fixture.Source.CreateCommand($"SELECT row_to_json(t)::text FROM {table} AS t");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var text = reader.GetString(0);
                foreach (var marker in new[] { Canary, Passphrase, "active.jsonl", "project-private-path" })
                {
                    Assert.DoesNotContain(marker, text);
                    Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(marker)), text);
                    Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(marker)), text);
                }
            }
        }
    }

    [PostgresFact]
    public async Task CorruptBlobDoesNotPartiallyImportOrAdvanceBaseline()
    {
        using var root = new TestDirectory();
        var endpoint = fixture.Endpoint();
        var gateway = new StorageRepositoryGateway(() => throw new InvalidOperationException("Unexpected Git."));
        var first = CreateDevice(root.Path, "first", gateway);
        var second = CreateDevice(root.Path, "second", gateway);
        await first.Services.InitializeAsync(endpoint.RepositoryUri.AbsoluteUri, Passphrase.AsMemory(), Ct);
        var authenticated = await second.Services.AuthenticateRepositoryAsync(endpoint.RepositoryUri.AbsoluteUri, Passphrase.AsMemory(), Ct);
        await second.Services.ApplyJoinAsync(authenticated, await second.Services.PlanJoinAsync(authenticated, Ct), Ct);
        var before = await second.Local.LoadConfigurationAsync(Ct);
        await WriteAllKindsAsync(first);
        await first.Services.SynchronizeAsync(SyncMode.Push, Ct);
        await using (var corrupt = fixture.Source.CreateCommand("UPDATE blobs SET ciphertext = $1 WHERE repository_name = $2"))
        {
            corrupt.Parameters.AddWithValue(PostgresApiTests.Envelope());
            corrupt.Parameters.AddWithValue(endpoint.Name);
            Assert.True(await corrupt.ExecuteNonQueryAsync() > 0);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => second.Services.SynchronizeAsync(SyncMode.Pull, Ct));
        Assert.Equal(before, await second.Local.LoadConfigurationAsync(Ct));
        Assert.Empty(Directory.EnumerateFiles(second.Codex.Sessions));
        Assert.Empty(Directory.EnumerateFiles(second.Claude.Projects, "*", SearchOption.AllDirectories));
        Assert.Empty(await second.Annotations.LoadAsync(Ct));
    }

    private static Device CreateDevice(string root, string name, ICliRepositoryGateway gateway)
    {
        root = System.IO.Path.Combine(root, name);
        var codexHome = System.IO.Path.Combine(root, "codex");
        Directory.CreateDirectory(codexHome);
        var codex = CodexPaths.Resolve(codexHome);
        Directory.CreateDirectory(codex.Sessions);
        Directory.CreateDirectory(codex.ArchivedSessions);
        var grok = new GrokPaths(System.IO.Path.Combine(root, "grok"), System.IO.Path.Combine(root, "grok", "sessions"));
        var claude = new ClaudePaths(System.IO.Path.Combine(root, "claude"), System.IO.Path.Combine(root, "claude", "projects"));
        var continuation = new ContinuePaths(System.IO.Path.Combine(root, "continue"), System.IO.Path.Combine(root, "continue", "sessions"));
        foreach (var directory in new[] { grok.Sessions, claude.Projects, continuation.Sessions }) Directory.CreateDirectory(directory);
        var localPath = System.IO.Path.Combine(root, "local");
        var keys = new MemoryKeys();
        var local = new FileCliLocalRepository(localPath, keys);
        var runtime = new CoreCliSyncRuntime(localPath, gateway, new Stopped(), (_, _) => throw new InvalidOperationException("No external Codex probe."),
            null, null, CodexExecutableSource.AutomaticDiscoveryAbsent, codexHome, grok.Home, null, claude.Home, continuation.Home);
        return new Device(root, codex, grok, claude, continuation, new SessionAnnotationStore(localPath), keys, local,
            new DefaultCliServices(gateway, local, runtime, new RepositoryCrypto()));
    }

    private static async Task<Dictionary<string, byte[]>> WriteAllKindsAsync(Device device)
    {
        var result = new Dictionary<string, byte[]>();
        async Task Write(string file, string text)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            var bytes = Encoding.UTF8.GetBytes(text);
            await File.WriteAllBytesAsync(file, bytes);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
            result[System.IO.Path.GetRelativePath(device.Root, file)] = bytes;
        }
        foreach (var (directory, id) in new[] { (device.Codex.Sessions, "active"), (device.Codex.ArchivedSessions, "archived") })
            await Write(System.IO.Path.Combine(directory, id + ".jsonl"), $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}\n{{\"type\":\"message\",\"payload\":{{\"text\":\"{Canary}\"}}}}\n");
        const string grokId = "30000000-0000-0000-0000-000000000003";
        var grokCwd = System.IO.Path.Combine(Directory.GetParent(device.Root)!.FullName, "project-private-path");
        var grokDirectory = device.Grok.SessionDirectory(grokCwd, grokId);
        await Write(System.IO.Path.Combine(grokDirectory, "chat_history.jsonl"), $"{{\"role\":\"user\",\"content\":\"{Canary}\"}}\n");
        await Write(System.IO.Path.Combine(grokDirectory, "summary.json"), JsonSerializer.Serialize(new { info = new { id = grokId, cwd = grokCwd } }));
        const string claudeId = "40000000-0000-0000-0000-000000000004";
        await Write(System.IO.Path.Combine(device.Claude.Projects, "project-private-path", claudeId + ".jsonl"),
            $"{{\"type\":\"user\",\"cwd\":\"project-private-path\",\"sessionId\":\"{claudeId}\",\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":\"{Canary}\"}}]}}}}\n");
        await Write(System.IO.Path.Combine(device.Claude.Projects, "project-private-path", "memory", "MEMORY.md"), Canary);
        const string continueId = "50000000-0000-0000-0000-000000000005";
        await Write(device.Continue.SessionFilePath(continueId), JsonSerializer.Serialize(new
        {
            sessionId = continueId, title = Canary, workspaceDirectory = "project-private-path",
            history = new[] { new { message = new { role = "user", content = Canary } } }
        }));
        await File.WriteAllTextAsync(device.Continue.IndexFilePath, ContinueSessionIndex.Serialize([
            ContinueSessionIndex.Synthesize(continueId, Canary, "project-private-path", DateTimeOffset.UtcNow.AddHours(-1), 1)]));
        await device.Annotations.SaveAsync(new SessionAnnotationKey(ManagedAgent.Claude, claudeId),
            new SessionAnnotation(Canary, Canary, SessionAnnotationSource.Generated, "digest", "model", DateTimeOffset.UtcNow.AddHours(-1)), Ct);
        return result;
    }

    private sealed record Device(string Root, CodexPaths Codex, GrokPaths Grok, ClaudePaths Claude, ContinuePaths Continue,
        SessionAnnotationStore Annotations, MemoryKeys Keys, FileCliLocalRepository Local, DefaultCliServices Services);
    private sealed class Stopped : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class MemoryKeys : IKeyStore
    {
        public Dictionary<string, byte[]> Values { get; } = [];
        public Task SaveAsync(string id, ReadOnlyMemory<byte> key, CancellationToken ct) { Values[id] = key.ToArray(); return Task.CompletedTask; }
        public Task<byte[]?> LoadAsync(string id, CancellationToken ct) => Task.FromResult(Values.TryGetValue(id, out var value) ? value.ToArray() : null);
        public Task DeleteAsync(string id, CancellationToken ct) { Values.Remove(id); return Task.CompletedTask; }
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-sync-server-test-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
