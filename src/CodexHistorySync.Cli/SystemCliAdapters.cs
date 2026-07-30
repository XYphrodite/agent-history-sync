using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using CodexHistorySync.Windows;

namespace CodexHistorySync.Cli;

public static class CliComposition
{
    public static CliApplication CreateDefault()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Codex History Sync currently requires Windows.");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) throw new InvalidOperationException("Local application data is unavailable.");
        var gateway = new GitHubCliRepositoryGateway();
        var local = new FileCliLocalRepository(localAppData, new DpapiKeyStore());
        var runtime = new CoreCliSyncRuntime(localAppData, gateway, new ConservativeCodexProcessDetector());
        return new CliApplication(new DefaultCliServices(gateway, local, runtime, new RepositoryCrypto()), new SystemCliConsole());
    }
}

public sealed class SystemCliConsole : ICliConsole
{
    public void WriteLine(string value) => Console.Out.WriteLine(value);
    public void WriteError(string value) => Console.Error.WriteLine(value);

    public Task<char[]> ReadSecretAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Console.IsInputRedirected) throw new CliGateException("Passphrases require an interactive console.");
        Console.Error.Write(prompt);
        var result = new List<char>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (result.Count != 0) result.RemoveAt(result.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) result.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return Task.FromResult(result.ToArray());
    }
}

public sealed class GitHubCliRepositoryGateway : ICliRepositoryGateway
{
    private const string ManifestFileName = "codex-history-sync.json";
    private readonly GitHubVisibilityVerifier visibilityVerifier;
    private readonly GitCommand git;
    private readonly GitCommand gh;

    public GitHubCliRepositoryGateway(string gitExecutable = "git", string ghExecutable = "gh")
    {
        visibilityVerifier = new GitHubVisibilityVerifier(ghExecutable);
        git = new GitCommand(gitExecutable);
        gh = new GitCommand(ghExecutable);
    }

    public async Task<CliGateResult> VerifyPrivateAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var result = await visibilityVerifier.VerifyPrivateAsync(ParseRepository(remoteUrl), cancellationToken).ConfigureAwait(false);
        return new CliGateResult(result.IsPrivate, "private-visibility");
    }

    public async Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var visibility = await VerifyPrivateAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
        if (!visibility.Passed) return visibility;
        var refs = await git.RunAsync(["ls-remote", remoteUrl],
            Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(refs, "Unable to inspect the initialization repository.").ConfigureAwait(false);
        return new CliGateResult(string.IsNullOrWhiteSpace(refs.StandardOutput), "empty-private-repository");
    }

    public async Task<CliPublishedInitialization> PublishInitializationAsync(string remoteUrl, string repositoryId,
        byte[] manifest, byte[] encryptedIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(encryptedIndex);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "codex-history-sync-init-" + Guid.NewGuid().ToString("N"));
        var clone = Path.Combine(temporaryRoot, "repository");
        Directory.CreateDirectory(temporaryRoot);
        Exception? primary = null;
        try
        {
            await RequireSuccessAsync(await git.RunAsync(["clone", "--no-checkout", "--origin", "origin", remoteUrl, clone], temporaryRoot, cancellationToken),
                "Unable to clone the private initialization repository.").ConfigureAwait(false);
            var refs = await git.RunAsync(["ls-remote", "origin"], clone, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(refs, "Unable to inspect the initialization repository.").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(refs.StandardOutput)) throw new InvalidOperationException("Initialization requires an empty private repository.");
            await RequireSuccessAsync(await git.RunAsync(["checkout", "--orphan", "main"], clone, cancellationToken), "Unable to create the initialization branch.").ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(clone, ManifestFileName), manifest, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(clone, "repository.chs"), encryptedIndex, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["config", "user.email", "codex-history-sync@localhost"], clone, cancellationToken), "Unable to configure Git identity.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["config", "user.name", "Codex History Sync"], clone, cancellationToken), "Unable to configure Git identity.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["add", "--", ManifestFileName, "repository.chs"], clone, cancellationToken), "Unable to stage initialization metadata.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["commit", "--no-gpg-sign", "-m", "Initialize encrypted Codex history"], clone, cancellationToken), "Unable to commit initialization metadata.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["push", "origin", "HEAD:main"], clone, cancellationToken), "Unable to publish initialization metadata.").ConfigureAwait(false);
            var revision = await git.RunAsync(["rev-parse", "HEAD"], clone, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(revision, "Unable to resolve initialization revision.").ConfigureAwait(false);
            return new CliPublishedInitialization(manifest.ToArray(), encryptedIndex.ToArray(), revision.StandardOutput.Trim());
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch (Exception cleanup) when (primary is not null && cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    public async Task<CliRemoteSetup> ReadSetupAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var repository = ParseRepository(remoteUrl);
        var revision = await ReadCurrentRevisionAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadGitHubFileAsync(repository, ManifestFileName, revision, cancellationToken).ConfigureAwait(false);
        var index = await ReadGitHubFileAsync(repository, "repository.chs", revision, cancellationToken).ConfigureAwait(false);
        return new CliRemoteSetup(manifest, index, revision);
    }

    public async Task<string> ReadCurrentRevisionAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var repository = ParseRepository(remoteUrl);
        var revision = await gh.RunAsync(["api", $"repos/{repository}/git/ref/heads/main", "--jq", ".object.sha"],
            Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(revision, "Unable to read the repository revision.").ConfigureAwait(false);
        var value = revision.StandardOutput.Trim();
        if (value.Length is not (40 or 64) || value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            throw new InvalidDataException("GitHub returned an invalid repository revision.");
        return value;
    }

    private async Task<byte[]> ReadGitHubFileAsync(string repository, string fileName, string revision,
        CancellationToken cancellationToken)
    {
        var result = await gh.RunAsync(["api", $"repos/{repository}/contents/{fileName}?ref={revision}", "--jq", ".content"], Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(result, "Unable to read encrypted repository setup metadata.").ConfigureAwait(false);
        try { return Convert.FromBase64String(string.Concat(result.StandardOutput.Where(character => !char.IsWhiteSpace(character)))); }
        catch (FormatException exception) { throw new InvalidDataException("GitHub returned malformed setup metadata.", exception); }
    }

    private static string ParseRepository(string remoteUrl)
    {
        var canonical = DefaultCliServices.CanonicalRemoteUrl(remoteUrl);
        var uri = new Uri(canonical);
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (path.Split('/').Length != 2) throw new CliGateException("GitHub repository URL must identify owner/repository.");
        return path;
    }

    private static Task RequireSuccessAsync(GitCommandResult result, string message)
    {
        if (result.ExitCode != 0 || result.TimedOut) throw new InvalidOperationException(message);
        return Task.CompletedTask;
    }
}

public sealed class FileCliLocalRepository : ICliLocalRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string configurationPath;
    private readonly IKeyStore keyStore;
    private readonly LocalStateStore stateStore;

    public FileCliLocalRepository(string localAppData, IKeyStore keyStore)
    {
        if (string.IsNullOrWhiteSpace(localAppData)) throw new ArgumentException("Local application data is required.", nameof(localAppData));
        this.keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        stateStore = new LocalStateStore(localAppData);
        configurationPath = Path.Combine(Path.GetFullPath(localAppData), "CodexHistorySync", "config.json");
    }

    public Task SaveKeyAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
        keyStore.SaveAsync(repositoryId, key, cancellationToken);

    public Task<byte[]?> LoadKeyAsync(string repositoryId, CancellationToken cancellationToken) =>
        keyStore.LoadAsync(repositoryId, cancellationToken);

    public async Task SaveConfigurationAsync(CliLocalConfiguration configuration, CancellationToken cancellationToken)
    {
        Validate(configuration);
        var directory = Path.GetDirectoryName(configurationPath)!;
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
        var temporary = Path.Combine(directory, ".config." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, configuration, JsonOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            RejectReparsePoints(temporary);
            File.Move(temporary, configurationPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<CliLocalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        RejectReparsePoints(configurationPath);
        await using var input = new FileStream(configurationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var configuration = await JsonSerializer.DeserializeAsync<CliLocalConfiguration>(input, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Local configuration is empty.");
        Validate(configuration);
        return configuration;
    }

    public Task SaveInitialStateAsync(string repositoryId, CancellationToken cancellationToken) =>
        stateStore.SaveAsync(new DeviceState(LocalStateStore.CurrentSchemaVersion, repositoryId, []), cancellationToken);

    private static void Validate(CliLocalConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 1) throw new InvalidDataException("Local configuration schema is unsupported.");
        if (string.IsNullOrWhiteSpace(configuration.RepositoryId) || string.IsNullOrWhiteSpace(configuration.DeviceId))
            throw new InvalidDataException("Local configuration identity is invalid.");
        if (!StringComparer.Ordinal.Equals(configuration.RemoteUrl, DefaultCliServices.CanonicalRemoteUrl(configuration.RemoteUrl)))
            throw new InvalidDataException("Local configuration remote URL is not canonical.");
    }

    private static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Directory.GetParent(current)?.FullName)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Local configuration path contains a reparse point.");
        }
    }
}

public sealed class CoreCliSyncRuntime : ICliSyncRuntime
{
    private readonly string localAppData;
    private readonly ICliRepositoryGateway gateway;
    private readonly ICodexProcessDetector processDetector;
    private readonly Func<string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe;

    public CoreCliSyncRuntime(string localAppData, ICliRepositoryGateway gateway, ICodexProcessDetector processDetector)
        : this(localAppData, gateway, processDetector,
            (fixture, cancellationToken) => new CodexCompatibilityProbe().ProbeAsync("codex", fixture, cancellationToken))
    {
    }

    public CoreCliSyncRuntime(string localAppData, ICliRepositoryGateway gateway, ICodexProcessDetector processDetector,
        Func<string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe)
    {
        this.localAppData = Path.GetFullPath(localAppData ?? throw new ArgumentNullException(nameof(localAppData)));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.processDetector = processDetector ?? throw new ArgumentNullException(nameof(processDetector));
        this.compatibilityProbe = compatibilityProbe ?? throw new ArgumentNullException(nameof(compatibilityProbe));
    }

    public async Task<CliGateResult> ProbeCompatibilityAsync(CancellationToken cancellationToken)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), "codex-history-sync-compatibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            var fixture = Path.Combine(fixtureRoot, "compatibility-fixture.jsonl");
            await File.WriteAllTextAsync(fixture,
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"compatibility-fixture\"}}\n",
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var result = await compatibilityProbe(fixture, cancellationToken).ConfigureAwait(false);
            return new CliGateResult(result.IsCompatible, "codex-compatibility");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new CliGateResult(false, "codex-compatibility");
        }
        finally
        {
            try { if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<CliJoinPlan> PreviewJoinAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        CliRemoteSetup setup, CancellationToken cancellationToken)
    {
        var preview = await Build(configuration, key, pinnedRevision: setup.Revision).Engine
            .PreviewAsync(SyncMode.Pull, cancellationToken).ConfigureAwait(false);
        return new CliJoinPlan(preview.LocalObjects, preview.RemoteObjects, preview.PendingChanges, preview.Conflicts);
    }

    public Task<SyncResult> SynchronizeAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        SyncMode mode, CancellationToken cancellationToken) => Build(configuration, key).Engine.SynchronizeAsync(mode, cancellationToken);

    public async Task<CliStatusReport> GetStatusAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        var components = Build(configuration, key);
        var preview = await components.Engine.PreviewAsync(SyncMode.Bidirectional, cancellationToken).ConfigureAwait(false);
        var conflictCount = (await components.Conflicts.ListAsync(cancellationToken).ConfigureAwait(false)).Count;
        return new CliStatusReport(preview.LocalObjects, preview.RemoteObjects, preview.PendingChanges,
            Math.Max(preview.Conflicts, conflictCount), preview.RemoteRevision);
    }

    public async Task<CliDoctorReport> RunDoctorAsync(CliLocalConfiguration? configuration, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        var checks = new List<CliDoctorCheck>();
        CodexPaths? paths = null;
        try { paths = CodexPaths.Resolve(null); checks.Add(new("codex-paths", true)); }
        catch { checks.Add(new("codex-paths", false)); }
        checks.Add(new("codex-version", await CommandSucceedsAsync("codex", ["--version"], cancellationToken).ConfigureAwait(false)));
        checks.Add(new("git-version", await CommandSucceedsAsync("git", ["--version"], cancellationToken).ConfigureAwait(false)));
        checks.Add(new("github-private", configuration is not null && (await gateway.VerifyPrivateAsync(configuration.RemoteUrl, cancellationToken).ConfigureAwait(false)).Passed));
        checks.Add(new("key-access", configuration is not null && key.Length == RepositoryCrypto.MasterKeySize));
        checks.Add(new("repository-schema", await RepositorySchemaIsValidAsync(configuration, key, cancellationToken).ConfigureAwait(false)));
        var processStateChecked = false;
        try { _ = processDetector.IsRunning(); processStateChecked = true; }
        catch { }
        checks.Add(new("process-state", processStateChecked));
        checks.Add(new("free-disk-space", HasFreeDiskSpace(paths?.Home ?? localAppData)));
        checks.Add(new("agent-installation", File.Exists(Path.Combine(localAppData, "CodexHistorySync", "agent.installed"))));
        return new CliDoctorReport(checks);
    }

    public async Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CliLocalConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var conflicts = Build(configuration, ReadOnlyMemory<byte>.Empty, requireKey: false).Conflicts;
        return (await conflicts.ListAsync(cancellationToken).ConfigureAwait(false)).Select(record => new CliConflictInfo(
            record.Id, record.Provenance.LocalHash.Hex, record.Provenance.RemoteHash.Hex,
            record.Provenance.LocalDeviceId, record.Provenance.RemoteDeviceId,
            record.Provenance.LocalTimestampUtc, record.Provenance.RemoteTimestampUtc)).ToArray();
    }

    public async Task<CliResolutionResult> ResolveAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key, string conflictId,
        CliResolution resolution, string? exportDirectory, CancellationToken cancellationToken)
    {
        var components = Build(configuration, key);
        var mapped = resolution switch
        {
            CliResolution.KeepLocal => ConflictResolution.KeepLocal,
            CliResolution.KeepRemote => ConflictResolution.KeepRemote,
            CliResolution.ExportBoth => ConflictResolution.ExportBoth,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        };
        var result = await components.Engine.ResolveConflictAsync(conflictId, mapped, exportDirectory, cancellationToken).ConfigureAwait(false);
        return new CliResolutionResult(result.RemainingConflicts, result.Exported);
    }

    private Components Build(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key, bool requireKey = true,
        string? pinnedRevision = null)
    {
        if (requireKey && key.Length != RepositoryCrypto.MasterKeySize) throw new CliGateException("The repository key is unavailable.");
        var paths = CodexPaths.Resolve(null);
        var scanner = new SessionScanner();
        var state = new LocalStateStore(localAppData);
        var backups = new BackupStore(configuration.RepositoryId, localAppData, paths);
        var conflicts = new ConflictStore(configuration.RepositoryId, localAppData, paths);
        if (!requireKey) return new Components(paths, scanner, conflicts, null!);
        var writer = new CodexHistoryWriter(paths, backups, processDetector);
        IStorageProvider provider = new GitStorageProvider(configuration.RepositoryId, configuration.RemoteUrl, GitRemoteKind.GitHub,
            Path.Combine(localAppData, "CodexHistorySync", "repositories"));
        if (pinnedRevision is not null) provider = new RevisionPinnedProvider(provider, pinnedRevision);
        var staging = Path.Combine(localAppData, "CodexHistorySync", "repositories", configuration.RepositoryId, "staging");
        var engine = new SyncEngine(configuration.RepositoryId, configuration.DeviceId, paths, key, scanner,
            new RepositoryCrypto(), state, writer, conflicts, provider, staging);
        return new Components(paths, scanner, conflicts, engine);
    }

    private async Task<bool> RepositorySchemaIsValidAsync(CliLocalConfiguration? configuration, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        if (configuration is null || key.Length != RepositoryCrypto.MasterKeySize) return false;
        try
        {
            var setup = await gateway.ReadSetupAsync(configuration.RemoteUrl, cancellationToken).ConfigureAwait(false);
            _ = await RepositoryManifestAuthenticator.AuthenticateIndexAsync(setup.Index, configuration.RepositoryId,
                key, new RepositoryCrypto(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> CommandSucceedsAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try { return (await new GitCommand(executable, TimeSpan.FromSeconds(10)).RunAsync(arguments, Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false)).ExitCode == 0; }
        catch { return false; }
    }

    private static bool HasFreeDiskSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace >= 64L * 1024 * 1024; }
        catch { return false; }
    }

    private sealed record Components(CodexPaths Paths, SessionScanner Scanner, ConflictStore Conflicts, SyncEngine Engine);

    private sealed class RevisionPinnedProvider(IStorageProvider inner, string expectedRevision) : IStorageProvider
    {
        public async Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            var snapshot = await inner.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(snapshot.Revision, expectedRevision))
                throw new CliGateException("The repository changed after join authentication; retry the join.");
            return snapshot;
        }

        public Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A pinned preview provider cannot publish.");
    }
}

public sealed class ConservativeCodexProcessDetector : ICodexProcessDetector
{
    public bool IsRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("codex");
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return true; }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (IsRunning()) await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
    }
}
