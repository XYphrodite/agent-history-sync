using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
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

        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await second.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
        await first.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

        var firstHashes = (await new SessionScanner(TimeSpan.Zero).ScanAsync(first.Paths, CancellationToken.None)).Select(x => x.Hash).OrderBy(x => x.Hex).ToArray();
        var secondHashes = (await new SessionScanner(TimeSpan.Zero).ScanAsync(second.Paths, CancellationToken.None)).Select(x => x.Hash).OrderBy(x => x.Hex).ToArray();
        Assert.Equal(2, firstHashes.Length);
        Assert.Equal(firstHashes, secondHashes);
        Assert.Empty(Directory.EnumerateFiles(remote, "*.jsonl", SearchOption.AllDirectories));
        Assert.DoesNotContain(promptMarker, ReadAllRemoteBytesAsText(remote), StringComparison.Ordinal);
        Assert.All(Directory.EnumerateFiles(Path.Combine(first.ProviderRoot, "repository", "git"), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")),
            path => Assert.Equal(".chs", Path.GetExtension(path)));
    }

    private Device CreateDevice(string name, string remote, byte[] key)
    {
        var home = Path.Combine(_root, name, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        Directory.CreateDirectory(paths.Sessions);
        var local = Path.Combine(_root, name, "local");
        var providerRoot = Path.Combine(_root, name, "provider");
        var backups = new BackupStore("repository", local, paths);
        var engine = new SyncEngine(
            "repository", name, paths, key,
            new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(), new LocalStateStore(local),
            new CodexHistoryWriter(paths, backups, new StoppedCodexDetector()),
            new ConflictStore("repository", local, paths),
            new GitStorageProvider("repository", remote, GitRemoteKind.Local, providerRoot),
            Path.Combine(local, "staging"));
        return new Device(paths, providerRoot, engine);
    }

    private static async Task WriteSessionAsync(string directory, string id, string text)
    {
        Directory.CreateDirectory(directory);
        var content = $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}\n{{\"type\":\"message\",\"payload\":{{\"text\":\"{text}\"}}}}\n";
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".jsonl"), content, new UTF8Encoding(false));
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

    private sealed record Device(CodexPaths Paths, string ProviderRoot, SyncEngine Engine);
    private sealed class StoppedCodexDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
