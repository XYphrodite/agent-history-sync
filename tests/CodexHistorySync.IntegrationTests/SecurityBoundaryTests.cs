using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexHistorySync.Cli;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;

namespace CodexHistorySync.IntegrationTests;

public sealed class SecurityBoundaryTests : IDisposable
{
    private const string RepositoryId = "security-audit";
    private static readonly EnvelopeMetadata IndexMetadata =
        new(1, new LogicalObjectId("__repository_index__"), ObjectKind.RepositoryIndex);
    private static readonly Regex CredentialUrl = new(@"https?://[^\s/:@]+:[^\s/@]+@", RegexOptions.IgnoreCase);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-security-{Guid.NewGuid():N}");

    [Fact]
    public void Cli_release_uses_documented_executable_identity()
    {
        Assert.Equal("codex-sync", typeof(CliApplication).Assembly.GetName().Name);
    }

    [Fact]
    public async Task Every_reachable_history_blob_is_public_authenticated_setup_or_authenticated_CHS1()
    {
        Directory.CreateDirectory(root);
        var remote = Path.Combine(root, "remote.git");
        await GitTextAsync(root, "init", "--bare", "--initial-branch=main", remote);
        var suffix = Guid.NewGuid().ToString("N");
        var canaries = new[]
        {
            $"prompt-{suffix}", $"auth-{suffix}", $"sqlite-{suffix}", $"log-{suffix}",
            $"sandbox-{suffix}", $"attachment-{suffix}", root
        };
        var passphrase = $"passphrase-{suffix}".ToCharArray();
        var credentialUrl = new UriBuilder("https", "github.com")
        {
            UserName = $"fixture-user-{suffix}", Password = $"fixture-credential-{suffix}",
            Path = "example/private-history.git"
        }.Uri.AbsoluteUri;
        var crypto = new RepositoryCrypto();
        var created = await RepositoryManifestAuthenticator.CreateAsync(RepositoryId, passphrase, crypto, CancellationToken.None);
        var key = created.MasterKey;
        try
        {
            var emptyIndex = await RepositoryManifestAuthenticator.CreateEmptyIndexAsync(
                RepositoryId, key, crypto, CancellationToken.None);
            await new GitHubCliRepositoryGateway().PublishInitializationAsync(
                remote, RepositoryId, created.Manifest, emptyIndex, CancellationToken.None);
            var device = CreateDevice(remote, key);
            await SeedExcludedFilesAsync(device.Paths, canaries, credentialUrl);
            await WriteSessionAsync(device.Paths.Sessions, "audit-session", canaries[0]);
            await device.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);
            await File.AppendAllTextAsync(Path.Combine(device.Paths.Sessions, "audit-session.jsonl"),
                $"{{\"type\":\"message\",\"payload\":{{\"text\":\"second-{suffix}\"}}}}\n", new UTF8Encoding(false));
            await device.Engine.SynchronizeAsync(SyncMode.Bidirectional, CancellationToken.None);

            var logger = new RotatingAgentLogger(Path.Combine(root, "audit-logs"), 1024 * 1024, 2);
            await logger.WriteAsync(new AgentLogEntry(AgentLogKind.Success, Guid.NewGuid(), SyncMode.Bidirectional,
                1, 0, 0, 0, 1, "revision-safe", "NONE", 1), CancellationToken.None);

            var forbidden = canaries.Append(passphrase.AsSpan().ToString()).Append(credentialUrl).ToArray();
            await AuditAllReachableCommitsAsync(remote, passphrase, key, forbidden, crypto);
            await AuditWorkingCloneAsync(device.ClonePath, passphrase, key, forbidden, crypto);
            AuditFiles(Path.Combine(root, "audit-logs"), forbidden);
            Assert.False(Directory.Exists(device.StagingRoot) &&
                         Directory.EnumerateFileSystemEntries(device.StagingRoot).Any());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Array.Fill(passphrase, '\0');
        }
    }

    private Device CreateDevice(string remote, byte[] key)
    {
        var home = Path.Combine(root, "device", "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        Directory.CreateDirectory(paths.Sessions);
        var local = Path.Combine(root, "device", "local");
        var providerRoot = Path.Combine(root, "device", "provider");
        var backups = new BackupStore(RepositoryId, local, paths);
        var engine = new SyncEngine(RepositoryId, "device-a", paths, key,
            new SessionScanner(TimeSpan.Zero), new RepositoryCrypto(), new LocalStateStore(local),
            new CodexHistoryWriter(paths, backups, new StoppedDetector()),
            new ConflictStore(RepositoryId, local, paths),
            new GitStorageProvider(RepositoryId, remote, GitRemoteKind.Local, providerRoot),
            Path.Combine(local, "staging"));
        return new Device(paths, Path.Combine(providerRoot, RepositoryId, "git"), Path.Combine(local, "staging"), engine);
    }

    private static async Task SeedExcludedFilesAsync(CodexPaths paths, string[] canaries, string credentialUrl)
    {
        await File.WriteAllTextAsync(Path.Combine(paths.Home, "auth.json"), canaries[1] + credentialUrl);
        await File.WriteAllTextAsync(Path.Combine(paths.Home, "state_5.sqlite"), canaries[2]);
        Directory.CreateDirectory(Path.Combine(paths.Home, "logs"));
        await File.WriteAllTextAsync(Path.Combine(paths.Home, "logs", "history.log"), canaries[3]);
        Directory.CreateDirectory(Path.Combine(paths.Home, ".sandbox-secrets"));
        await File.WriteAllTextAsync(Path.Combine(paths.Home, ".sandbox-secrets", "token"), canaries[4]);
        Directory.CreateDirectory(paths.Attachments);
        await File.WriteAllTextAsync(Path.Combine(paths.Attachments, "excluded.bin"), canaries[5]);
    }

    private static async Task AuditAllReachableCommitsAsync(string remote, char[] passphrase, byte[] key,
        string[] forbidden, RepositoryCrypto crypto)
    {
        var listed = await GitTextAsync(remote, "rev-list", "--objects", "--all");
        foreach (var line in Lines(listed))
        {
            var split = line.Split(' ', 2);
            if (await GitTextAsync(remote, "cat-file", "-t", split[0]) != "blob") continue;
            Assert.True(split.Length == 2 && !string.IsNullOrWhiteSpace(split[1]),
                $"Reachable blob {split[0]} has no repository path.");
            AssertAllowedRepositoryPath(split[1]);
            var bytes = await GitBytesAsync(remote, "cat-file", "blob", split[0]);
            AssertNoForbidden(bytes, forbidden);
        }

        foreach (var commit in Lines(await GitTextAsync(remote, "rev-list", "--all")))
        {
            var tree = Lines(await GitTextAsync(remote, "ls-tree", "-r", "--full-tree", commit));
            var blobs = tree.ToDictionary(
                line => line[(line.IndexOf('\t') + 1)..],
                line => line.Split(' ', '\t')[2], StringComparer.Ordinal);
            Assert.True(blobs.TryGetValue("codex-history-sync.json", out var manifestId));
            Assert.True(blobs.TryGetValue("repository.chs", out var indexId));
            var manifest = await GitBytesAsync(remote, "cat-file", "blob", manifestId!);
            var authenticated = await RepositoryManifestAuthenticator.AuthenticateAsync(
                manifest, passphrase, crypto, CancellationToken.None);
            try
            {
                Assert.Equal(RepositoryId, authenticated.Manifest.RepositoryId);
                Assert.Equal(key, authenticated.MasterKey);
            }
            finally { CryptographicOperations.ZeroMemory(authenticated.MasterKey); }

            var index = await GitBytesAsync(remote, "cat-file", "blob", indexId!);
            AssertChs1(index);
            var entries = await ReadIndexEntriesAsync(index, key, crypto);
            foreach (var pair in blobs.Where(pair => pair.Key.StartsWith("objects/", StringComparison.Ordinal)))
            {
                var opaque = pair.Key["objects/".Length..].Replace("/", "", StringComparison.Ordinal);
                opaque = Path.GetFileNameWithoutExtension(opaque);
                var entry = entries[opaque];
                var ciphertext = await GitBytesAsync(remote, "cat-file", "blob", pair.Value);
                AssertChs1(ciphertext);
                await AuthenticateObjectAsync(ciphertext, key, entry, crypto);
            }
        }
    }

    private static async Task<Dictionary<string, IndexEntry>> ReadIndexEntriesAsync(byte[] encrypted, byte[] key,
        RepositoryCrypto crypto)
    {
        await using var input = new MemoryStream(encrypted, false);
        await using var output = new MemoryStream();
        await crypto.DecryptAsync(input, output, key, IndexMetadata, CancellationToken.None);
        using var document = JsonDocument.Parse(output.ToArray());
        return document.RootElement.GetProperty("objects").EnumerateArray().ToDictionary(
            value => value.GetProperty("opaqueObjectId").GetString()!,
            value => new IndexEntry(value.GetProperty("id").GetString()!,
                (ObjectKind)value.GetProperty("kind").GetInt32()));
    }

    private static async Task AuthenticateObjectAsync(byte[] encrypted, byte[] key, IndexEntry entry,
        RepositoryCrypto crypto)
    {
        await using var input = new MemoryStream(encrypted, false);
        await using var output = new MemoryStream();
        await crypto.DecryptAsync(input, output, key,
            new EnvelopeMetadata(1, new LogicalObjectId(entry.Id), entry.Kind), CancellationToken.None);
    }

    private static async Task AuditWorkingCloneAsync(string clone, char[] passphrase, byte[] key,
        string[] forbidden, RepositoryCrypto crypto)
    {
        foreach (var path in Directory.EnumerateFiles(clone, "*", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")))
        {
            var relative = Path.GetRelativePath(clone, path).Replace('\\', '/');
            AssertAllowedRepositoryPath(relative);
            var bytes = await File.ReadAllBytesAsync(path);
            AssertNoForbidden(bytes, forbidden);
            if (relative == "codex-history-sync.json")
            {
                var authenticated = await RepositoryManifestAuthenticator.AuthenticateAsync(
                    bytes, passphrase, crypto, CancellationToken.None);
                CryptographicOperations.ZeroMemory(authenticated.MasterKey);
            }
            else AssertChs1(bytes);
        }
    }

    private static void AuditFiles(string directory, string[] forbidden)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            AssertNoForbidden(File.ReadAllBytes(path), forbidden);
    }

    private static void AssertAllowedRepositoryPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var allowed = normalized is "codex-history-sync.json" or "repository.chs" ||
                      Regex.IsMatch(normalized, "^objects/[a-f0-9]{2}/[a-f0-9]{62}\\.chs$");
        Assert.True(allowed, $"Forbidden repository path: {normalized}");
        Assert.DoesNotContain(normalized.Split('/'), segment =>
            segment.Equals("auth.json", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("sqlite", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith(".sandbox", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertNoForbidden(byte[] bytes, IEnumerable<string> forbidden)
    {
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var marker in forbidden) Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
        Assert.DoesNotMatch(CredentialUrl, text);
    }

    private static void AssertChs1(byte[] bytes) =>
        Assert.True(bytes.Length > 4 && bytes.AsSpan(0, 4).SequenceEqual("CHS1"u8));

    private static async Task WriteSessionAsync(string directory, string id, string text) =>
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".jsonl"),
            $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}\n" +
            $"{{\"type\":\"message\",\"payload\":{{\"text\":\"{text}\"}}}}\n", new UTF8Encoding(false));

    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static async Task<string> GitTextAsync(string directory, params string[] arguments) =>
        Encoding.UTF8.GetString(await GitBytesAsync(directory, arguments)).Trim();

    private static async Task<byte[]> GitBytesAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(copy, process.WaitForExitAsync());
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        return output.ToArray();
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private sealed record Device(CodexPaths Paths, string ClonePath, string StagingRoot, SyncEngine Engine);
    private sealed record IndexEntry(string Id, ObjectKind Kind);
    private sealed class StoppedDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
