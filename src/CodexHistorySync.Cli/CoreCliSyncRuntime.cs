using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli.Management;
using CodexHistorySync.Cli.Search;
using CodexHistorySync.Cli.Mcp;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using CodexHistorySync.Windows;
using Spectre.Console;

namespace CodexHistorySync.Cli;

public sealed class CoreCliSyncRuntime : ICliSyncRuntime
{
    private readonly string localAppData;
    private readonly ICliRepositoryGateway gateway;
    private readonly ICodexProcessDetector processDetector;
    private readonly IAgentInstallationChecker agentInstallationChecker;
    private readonly Func<string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe;
    private readonly Func<CliLocalConfiguration, ReadOnlyMemory<byte>, SyncEngine>? engineFactory;
    private readonly CodexExecutableSource codexExecutableSource;
    private readonly string? codexHome;
    private readonly string? grokHome;
    private readonly string? claudeHome;
    private readonly string? continueHome;
    private readonly Action<SyncProgress>? syncProgress;

    public CoreCliSyncRuntime(string localAppData, ICliRepositoryGateway gateway, ICodexProcessDetector processDetector)
        : this(localAppData, gateway, processDetector,
            (fixture, cancellationToken) => new CodexCompatibilityProbe().ProbeAsync("codex", fixture, cancellationToken), null, null)
    {
    }

    public CoreCliSyncRuntime(string localAppData, ICliRepositoryGateway gateway, ICodexProcessDetector processDetector,
        Func<string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe)
        : this(localAppData, gateway, processDetector, compatibilityProbe, null, null)
    {
    }

    public CoreCliSyncRuntime(string localAppData, ICliRepositoryGateway gateway, ICodexProcessDetector processDetector,
        Func<string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe,
        Func<CliLocalConfiguration, ReadOnlyMemory<byte>, SyncEngine>? engineFactory)
        : this(localAppData, gateway, processDetector, compatibilityProbe, engineFactory, null)
    {
    }

    public CoreCliSyncRuntime(string localAppData, ICliRepositoryGateway gateway, ICodexProcessDetector processDetector,
        Func<string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe,
        Func<CliLocalConfiguration, ReadOnlyMemory<byte>, SyncEngine>? engineFactory,
        IAgentInstallationChecker? agentInstallationChecker,
        CodexExecutableSource codexExecutableSource = CodexExecutableSource.Discovered,
        string? codexHome = null,
        string? grokHome = null,
        Action<SyncProgress>? syncProgress = null,
        string? claudeHome = null,
        string? continueHome = null)
    {
        this.localAppData = Path.GetFullPath(localAppData ?? throw new ArgumentNullException(nameof(localAppData)));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.processDetector = processDetector ?? throw new ArgumentNullException(nameof(processDetector));
        this.compatibilityProbe = compatibilityProbe ?? throw new ArgumentNullException(nameof(compatibilityProbe));
        this.engineFactory = engineFactory;
        this.agentInstallationChecker = agentInstallationChecker ?? UnconfiguredAgentInstallationChecker.Instance;
        this.codexExecutableSource = codexExecutableSource;
        this.codexHome = codexHome;
        this.grokHome = grokHome;
        this.claudeHome = claudeHome;
        this.continueHome = continueHome;
        this.syncProgress = syncProgress;
    }

    public async Task<CliGateResult> ProbeCompatibilityAsync(CancellationToken cancellationToken)
    {
        if (codexExecutableSource == CodexExecutableSource.AutomaticDiscoveryAbsent)
            return new CliGateResult(true, "codex-compatibility",
                "skipped-no-codex: Codex executable was not found during automatic discovery.");

        var fixtureRoot = Path.Combine(Path.GetTempPath(), "codex-history-sync-compatibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            // Codex lists a thread only when the file looks like one of its own. Measured against
            // Codex 0.146 and 0.151, which agree exactly: the name has to be
            // rollout-<timestamp>-<uuid>.jsonl, the metadata has to carry cwd, originator and
            // cli_version, and a user message has to follow it. Drop any one of those and no
            // Codex lists the thread - which is what the gate was doing to every machine it was
            // asked about, since the fixture was a lone session_meta under a name of our own.
            var threadId = Guid.NewGuid().ToString();
            var stamp = DateTime.UtcNow;
            var written = stamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var fixture = Path.Combine(fixtureRoot,
                $"rollout-{stamp:yyyy-MM-dd}T{stamp:HH-mm-ss}-{threadId}.jsonl");
            // Serialized rather than hand-quoted: a path carries backslashes, and one of them
            // escaped wrongly is a line Codex silently skips.
            var meta = JsonSerializer.Serialize(new
            {
                timestamp = written,
                type = "session_meta",
                payload = new
                {
                    id = threadId,
                    session_id = threadId,
                    timestamp = written,
                    cwd = fixtureRoot,
                    originator = "codex_history_sync",
                    cli_version = CliVersion.Current.ToString()
                }
            });
            var message = JsonSerializer.Serialize(new
            {
                timestamp = written,
                type = "event_msg",
                payload = new { type = "user_message", message = "agent-sync compatibility fixture" }
            });
            await File.WriteAllTextAsync(fixture, meta + "\n" + message + "\n",
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var result = await compatibilityProbe(fixture, cancellationToken).ConfigureAwait(false);
            if (result.IsCompatible)
                return new CliGateResult(true, "codex-compatibility", result.Diagnostic);
            return new CliGateResult(false, "codex-compatibility", result.Diagnostic);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new CliGateResult(false, "codex-compatibility", "The Codex compatibility probe could not run.");
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
        await using var components = Build(configuration, key, pinnedRevision: setup.Revision);
        var preview = await components.Engine!.PreviewAsync(SyncMode.Pull, cancellationToken).ConfigureAwait(false);
        return new CliJoinPlan(preview.LocalObjects, preview.RemoteObjects, preview.PendingChanges, preview.Conflicts);
    }

    public async Task<SyncResult> SynchronizeAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        SyncMode mode, CancellationToken cancellationToken)
    {
        await using var components = Build(configuration, key);
        return await components.Engine!.SynchronizeAsync(mode, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CliStatusReport> GetStatusAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        await using var components = Build(configuration, key);
        var preview = await components.Engine!.PreviewAsync(SyncMode.Bidirectional, cancellationToken).ConfigureAwait(false);
        var conflictIdentities = preview.ConflictIdentities.ToHashSet(StringComparer.Ordinal);
        foreach (var conflict in await components.Conflicts.ListAsync(cancellationToken).ConfigureAwait(false))
            conflictIdentities.Add(ConflictStore.GetIdentity(conflict.Provenance));
        return new CliStatusReport(preview.LocalObjects, preview.RemoteObjects, preview.PendingChanges,
            conflictIdentities.Count, preview.RemoteRevision,
            configuration.LastSuccessfulRevision)
        {
            ClaudeHome = ClaudePaths.TryResolve(claudeHome)?.Projects,
            ClaudeSessions = preview.LocalByKind.TryGetValue(ObjectKind.ClaudeSession, out var claudeCount) ? claudeCount : 0,
            ClaudeMemory = preview.LocalByKind.TryGetValue(ObjectKind.ClaudeMemory, out var claudeMemoryCount) ? claudeMemoryCount : 0,
            ClaudeUncertain = preview.UncertainKinds.Contains(ObjectKind.ClaudeSession)
                || preview.UncertainKinds.Contains(ObjectKind.ClaudeMemory),
            ContinueHome = ContinuePaths.TryResolve(continueHome)?.Sessions,
            ContinueSessions = preview.LocalByKind.TryGetValue(ObjectKind.ContinueSession, out var continueCount) ? continueCount : 0,
            ContinueUncertain = preview.UncertainKinds.Contains(ObjectKind.ContinueSession)
        };
    }

    public async Task<CliDoctorReport> RunDoctorAsync(CliLocalConfiguration? configuration, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        var checks = new List<CliDoctorCheck>();
        CodexPaths? paths = null;
        try { paths = CodexPaths.Resolve(null); checks.Add(new("codex-paths", true)); }
        catch { checks.Add(new("codex-paths", false)); }
        // Not a failure when Claude is not installed: the check reports whether a home was found
        // at all, which is what tells the user why no Claude panel or sessions appear.
        checks.Add(new("claude-paths", ClaudePaths.TryResolve(claudeHome) is not null));
        checks.Add(new("continue-paths", ContinuePaths.TryResolve(continueHome) is not null));
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
        var agentInstalled = false;
        try { agentInstalled = await agentInstallationChecker.IsInstalledAsync(cancellationToken).ConfigureAwait(false); }
        catch { }
        checks.Add(new("agent-installation", agentInstalled));
        return new CliDoctorReport(checks);
    }

    public async Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CliLocalConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var components = Build(configuration, ReadOnlyMemory<byte>.Empty, requireKey: false);
        var conflicts = components.Conflicts;
        return (await conflicts.ListAsync(cancellationToken).ConfigureAwait(false)).Select(record => new CliConflictInfo(
            record.Id, record.Provenance.LocalHash.Hex, record.Provenance.RemoteHash.Hex,
            record.Provenance.LocalDeviceId, record.Provenance.RemoteDeviceId,
            record.Provenance.LocalTimestampUtc, record.Provenance.RemoteTimestampUtc)).ToArray();
    }

    public async Task<CliResolutionResult> ResolveAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key, string conflictId,
        CliResolution resolution, string? exportDirectory, CancellationToken cancellationToken)
    {
        await using var components = Build(configuration, key);
        var mapped = resolution switch
        {
            CliResolution.KeepLocal => ConflictResolution.KeepLocal,
            CliResolution.KeepRemote => ConflictResolution.KeepRemote,
            CliResolution.ExportBoth => ConflictResolution.ExportBoth,
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        };
        var result = await components.Engine!.ResolveConflictAsync(conflictId, mapped, exportDirectory, cancellationToken).ConfigureAwait(false);
        return new CliResolutionResult(result.RemainingConflicts, result.Exported);
    }

    private Components Build(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key, bool requireKey = true,
        string? pinnedRevision = null)
    {
        if (requireKey && key.Length != RepositoryCrypto.MasterKeySize) throw new CliGateException("The repository key is unavailable.");
        var paths = codexExecutableSource == CodexExecutableSource.AutomaticDiscoveryAbsent
            ? CodexPaths.ResolveLayout(codexHome)
            : CodexPaths.Resolve(codexHome);
        var grokPaths = CodexHistorySync.Core.Grok.GrokPaths.TryResolve(grokHome);
        var claudePaths = ClaudePaths.TryResolve(claudeHome);
        var continuePaths = ContinuePaths.TryResolve(continueHome);
        var scanner = new SessionScanner();
        var state = new LocalStateStore(localAppData);
        var annotationsDirectory = new SessionAnnotationStore(localAppData).Directory;
        var backups = new BackupStore(configuration.RepositoryId, localAppData, paths, grokPaths: grokPaths,
            claudePaths: claudePaths, continuePaths: continuePaths, annotationsDirectory: annotationsDirectory);
        var conflicts = new ConflictStore(configuration.RepositoryId, localAppData, paths);
        if (!requireKey) return new Components(paths, scanner, conflicts, null!);
        var writer = new CodexHistoryWriter(paths, backups, processDetector, grokPaths: grokPaths,
            claudePaths: claudePaths, continuePaths: continuePaths, annotationsDirectory: annotationsDirectory);
        // First-time history upload can stage hundreds of objects; the default 30s git timeout is too short.
        IStorageProvider provider = new GitStorageProvider(configuration.RepositoryId, configuration.RemoteUrl, GitRemoteKind.GitHub,
            Path.Combine(localAppData, "CodexHistorySync", "repositories"),
            commandTimeout: TimeSpan.FromMinutes(30));
        if (pinnedRevision is not null) provider = new RevisionPinnedProvider(provider, pinnedRevision);
        var staging = Path.Combine(localAppData, "CodexHistorySync", "repositories", configuration.RepositoryId, "staging");
        var engine = engineFactory?.Invoke(configuration, key) ?? new SyncEngine(configuration.RepositoryId,
            configuration.DeviceId, paths, key, scanner, new RepositoryCrypto(), state, writer, conflicts, provider, staging,
            grokPaths: grokPaths, progress: syncProgress, claudePaths: claudePaths, continuePaths: continuePaths,
            annotationsDirectory: annotationsDirectory);
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

    private sealed record Components(CodexPaths Paths, SessionScanner Scanner, ConflictStore Conflicts, SyncEngine? Engine)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Engine?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class RevisionPinnedProvider(IStorageProvider inner, string expectedRevision) : IStorageProvider
    {
        public async Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            var snapshot = await inner.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Validate(snapshot);
            return snapshot;
        }

        public async Task<RemoteSnapshot> ReadSnapshotMetadataAsync(CancellationToken cancellationToken)
        {
            var snapshot = await inner.ReadSnapshotMetadataAsync(cancellationToken).ConfigureAwait(false);
            Validate(snapshot);
            return snapshot;
        }

        public Task<byte[]> ReadObjectAsync(RemoteSnapshot snapshot, LogicalObjectId objectId,
            CancellationToken cancellationToken) => inner.ReadObjectAsync(snapshot, objectId, cancellationToken);

        private void Validate(RemoteSnapshot snapshot)
        {
            if (!StringComparer.Ordinal.Equals(snapshot.Revision, expectedRevision))
                throw new CliGateException("The repository changed after join authentication; retry the join.");
        }

        public Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A pinned preview provider cannot publish.");
    }

    private sealed class UnconfiguredAgentInstallationChecker : IAgentInstallationChecker
    {
        public static UnconfiguredAgentInstallationChecker Instance { get; } = new();
        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
