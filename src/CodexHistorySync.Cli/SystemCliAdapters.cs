using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using CodexHistorySync.Windows;
using Spectre.Console;

namespace CodexHistorySync.Cli;

public static class CliComposition
{
    internal static CliApplication CreateForArguments(
        string[] args,
        ICliConsole console,
        Func<ICliConsole, CliApplication> createSynchronizedApplication,
        Func<ISessionManagerRunner> createSessionManagerRunner,
        Func<ISessionManagerRunner>? createSessionViewerRunner = null,
        Func<ISelfUpdateOperations>? createSelfUpdateOperations = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(createSynchronizedApplication);
        ArgumentNullException.ThrowIfNull(createSessionManagerRunner);

        return args switch
        {
            ["--manage"] => new CliApplication(console, createSessionManagerRunner()),
            ["--sessions"] when createSessionViewerRunner is not null =>
                new CliApplication(console, createSessionViewerRunner()),
            ["update", ..] when createSelfUpdateOperations is not null =>
                new CliApplication(console, createSelfUpdateOperations()),
            _ => createSynchronizedApplication(console)
        };
    }

    public static CliApplication CreateDefault(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var console = new SystemCliConsole();
        return CreateForArguments(args, console, CreateSynchronizedApplication, CreateSessionManagerRunner,
            CreateSessionViewerRunner, static () => new DefaultSelfUpdateOperations());
    }

    public static CliApplication CreateDefault() => CreateSynchronizedApplication(new SystemCliConsole());

    internal static CodexExecutableOption ToCodexExecutableOption(CodexExecutableResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return new CodexExecutableOption(resolution.ExecutablePath, resolution.Source switch
        {
            CodexExecutableSource.Configured => CodexExecutableAvailability.Configured,
            CodexExecutableSource.Discovered => CodexExecutableAvailability.Discovered,
            CodexExecutableSource.AutomaticDiscoveryAbsent => CodexExecutableAvailability.AutomaticDiscoveryAbsent,
            _ => throw new InvalidOperationException("The Codex executable source is invalid.")
        });
    }

    private static CliApplication CreateSynchronizedApplication(ICliConsole console)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent History Sync currently requires Windows.");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) throw new InvalidOperationException("Local application data is unavailable.");
        var gateway = new GitHubCliRepositoryGateway();
        var local = new FileCliLocalRepository(localAppData, new DpapiKeyStore());
        var scheduler = new AgentScheduler();
        var codexResolution = new CodexExecutableLocator().ResolveWithSource();
        var codexExecutable = codexResolution.ExecutablePath ?? string.Empty;
        var detectorExecutable = string.IsNullOrWhiteSpace(codexExecutable) ? Path.GetFullPath("codex.exe") : codexExecutable;
        var detector = new CodexProcessDetector(new CodexProcessDetectorOptions(detectorExecutable));
        Stopwatch? syncTimer = null;
        void ReportSyncProgress(SyncProgress progress)
        {
            if (progress.Phase == SyncProgressPhase.WaitingForLock) syncTimer = Stopwatch.StartNew();
            var elapsed = syncTimer?.Elapsed ?? TimeSpan.Zero;
            console.WriteLine($"sync [{elapsed:mm\\:ss}] {progress.Message}...");
        }
        var runtime = new CoreCliSyncRuntime(localAppData, gateway, detector,
            (fixture, cancellationToken) => new CodexCompatibilityProbe().ProbeAsync(
                string.IsNullOrWhiteSpace(codexExecutable) ? "codex.exe" : codexExecutable, fixture, cancellationToken),
            null, scheduler, codexResolution.Source, syncProgress: ReportSyncProgress);
        var services = new DefaultCliServices(gateway, local, runtime, new RepositoryCrypto());
        var worker = new AgentWorker(detector, new CliAgentSyncOperations(services), new SystemAgentClock(),
            new WindowsNotifier(), new RotatingAgentLogger(localAppData));
        var agent = new DefaultAgentCliOperations(worker, scheduler, () => Environment.ProcessPath);
        return new CliApplication(services, console, agent, selfUpdate: new DefaultSelfUpdateOperations());
    }

    private static ISessionManagerRunner CreateSessionViewerRunner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent History Sync currently requires Windows.");
        var codexPaths = TryResolveCodexPaths();
        var grokPaths = GrokPaths.TryResolve();
        var claudePaths = ClaudePaths.TryResolve();
        var activeState = new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths);
        var catalog = new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths);
        // The viewer never copies, so no conversation writers are composed for it (design D6).
        var operations = new LocalSessionOperations(
            codexPaths,
            grokPaths,
            activeState,
            new WindowsManagedSessionDirectoryDeleter(),
            null,
            null,
            claudePaths,
            null);
        var ansiConsole = AnsiConsole.Console;
        var view = new SpectreSessionViewerView(ansiConsole, new SpectreSessionManagerInput(ansiConsole));
        return new DefaultSessionViewerRunner(new SessionViewerApplication(
            catalog, new SessionContentReader(), new SessionExporter(), operations, view));
    }

    private static ISessionManagerRunner CreateSessionManagerRunner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent History Sync currently requires Windows.");
        var codexPaths = TryResolveCodexPaths();
        var grokPaths = GrokPaths.TryResolve();
        var claudePaths = ClaudePaths.TryResolve();
        var resolution = new CodexExecutableLocator().ResolveWithSource();
        var executable = ToCodexExecutableOption(resolution);
        var activeState = new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths);
        var catalog = new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths);
        var codexWriter = codexPaths is null
            ? null
            : new CodexConversationWriter(codexPaths, executable, new CodexCompatibilityProbe());
        var grokWriter = grokPaths is null ? null : new GrokConversationWriter(grokPaths);
        var claudeWriter = claudePaths is null ? null : new ClaudeConversationWriter(claudePaths);
        var operations = new LocalSessionOperations(
            codexPaths,
            grokPaths,
            activeState,
            new WindowsManagedSessionDirectoryDeleter(),
            codexWriter,
            grokWriter,
            claudePaths,
            claudeWriter);
        var ansiConsole = AnsiConsole.Console;
        var view = new SpectreSessionManagerView(ansiConsole, new SpectreSessionManagerInput(ansiConsole));
        return new DefaultSessionManagerRunner(new SessionManagerApplication(catalog, operations, view));
    }

    internal static CodexPaths? TryResolveCodexPaths(string? configuredHome = null)
    {
        try
        {
            return CodexPaths.ResolveLayout(configuredHome);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class DefaultSessionViewerRunner(SessionViewerApplication application) : ISessionManagerRunner
{
    private readonly SessionViewerApplication application = application ?? throw new ArgumentNullException(nameof(application));

    public Task RunAsync(CancellationToken cancellationToken) => application.RunAsync(cancellationToken);
}

internal sealed class DefaultSessionManagerRunner(SessionManagerApplication application) : ISessionManagerRunner
{
    private readonly SessionManagerApplication application = application ?? throw new ArgumentNullException(nameof(application));

    public Task RunAsync(CancellationToken cancellationToken) => application.RunAsync(cancellationToken);
}

internal sealed class WindowsManagedSessionActiveState : IManagedSessionActiveState
{
    private const string GrokProcessName = "grok";
    private const string ClaudeProcessName = "claude";
    private static readonly IReadOnlySet<string> EmptyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly CodexPaths? codexPaths;
    private readonly GrokPaths? grokPaths;
    private readonly ClaudePaths? claudePaths;
    private readonly Func<int, string, bool> isNamedProcessRunning;
    private readonly Func<string, bool> isExclusiveLockHeld;
    private readonly Func<string, string, IReadOnlyList<string>> enumerateFiles;
    private readonly Func<string, string?> readAllText;
    private readonly Func<string, bool> isAnyNamedProcessRunning;
    private readonly Func<string, DateTime?> lastWriteTimeUtc;
    private readonly Func<DateTime> utcNow;

    public WindowsManagedSessionActiveState(CodexPaths? codexPaths, GrokPaths? grokPaths, ClaudePaths? claudePaths = null)
        : this(
            codexPaths,
            grokPaths,
            IsNamedProcessRunning,
            IsExclusiveLockHeld,
            EnumerateFiles,
            ReadAllText,
            claudePaths,
            IsAnyNamedProcessRunning,
            path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null,
            () => DateTime.UtcNow)
    {
    }

    internal WindowsManagedSessionActiveState(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        Func<int, string, bool> isNamedProcessRunning,
        Func<string, bool> isExclusiveLockHeld,
        Func<string, string, IReadOnlyList<string>> enumerateFiles,
        Func<string, string?> readAllText,
        ClaudePaths? claudePaths = null,
        Func<string, bool>? isAnyNamedProcessRunning = null,
        Func<string, DateTime?>? lastWriteTimeUtc = null,
        Func<DateTime>? utcNow = null)
    {
        this.codexPaths = codexPaths;
        this.grokPaths = grokPaths;
        this.claudePaths = claudePaths;
        this.isAnyNamedProcessRunning = isAnyNamedProcessRunning ?? IsAnyNamedProcessRunning;
        this.lastWriteTimeUtc = lastWriteTimeUtc ?? (path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null);
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.isNamedProcessRunning = isNamedProcessRunning
            ?? throw new ArgumentNullException(nameof(isNamedProcessRunning));
        this.isExclusiveLockHeld = isExclusiveLockHeld
            ?? throw new ArgumentNullException(nameof(isExclusiveLockHeld));
        this.enumerateFiles = enumerateFiles ?? throw new ArgumentNullException(nameof(enumerateFiles));
        this.readAllText = readAllText ?? throw new ArgumentNullException(nameof(readAllText));
    }

    public Task<bool> IsActiveAsync(
        ManagedAgent agent,
        string sessionId,
        string nativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sessionId)) return Task.FromResult(true);
        return Task.FromResult(ReadActiveIds(agent).Contains(sessionId));
    }

    public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(
        ManagedAgent agent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadActiveIds(agent));
    }

    private IReadOnlySet<string> ReadActiveIds(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => ReadCodexActiveIds(),
        ManagedAgent.Grok => ReadGrokActiveIds(),
        ManagedAgent.Claude => ReadClaudeActiveIds(),
        _ => EmptyIds
    };

    /// <summary>
    /// Claude publishes no active-session file, so liveness is a running claude process plus a
    /// recent write, mirroring the scanner's rule (design D3). Without a running process nothing
    /// is active; with one, only transcripts touched inside the window are.
    /// </summary>
    private IReadOnlySet<string> ReadClaudeActiveIds()
    {
        if (claudePaths is null || !isAnyNamedProcessRunning(ClaudeProcessName)) return EmptyIds;

        var activeSince = utcNow() - ClaudeSessionScanner.DefaultActivityWindow;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in EnumerateDirectories(claudePaths.Projects))
            foreach (var path in enumerateFiles(project, "*.jsonl"))
            {
                var sessionId = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(sessionId)) continue;
                if (lastWriteTimeUtc(path) is { } written && written >= activeSince) ids.Add(sessionId);
            }

        return ids;
    }

    private IReadOnlySet<string> ReadGrokActiveIds()
    {
        if (grokPaths is null) return EmptyIds;
        var path = Path.Combine(grokPaths.Home, "active_sessions.json");
        var json = readAllText(path);
        if (json is null) return EmptyIds;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in GrokActiveSessionList.Parse(json))
        {
            if (isNamedProcessRunning(record.ProcessId, GrokProcessName))
                ids.Add(record.SessionId);
        }

        return ids;
    }

    private IReadOnlySet<string> ReadCodexActiveIds()
    {
        if (codexPaths is null) return EmptyIds;
        var directory = Path.Combine(codexPaths.Home, "thread-writer-locks");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in enumerateFiles(directory, "*.lock"))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) continue;
            var sessionId = Path.GetFileNameWithoutExtension(name);
            if (!IsSafeSessionId(sessionId)) continue;
            if (isExclusiveLockHeld(path)) ids.Add(sessionId);
        }

        return ids;
    }

    private static bool IsAnyNamedProcessRunning(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or
                                          UnauthorizedAccessException or SecurityException)
        {
            // Fail closed: an unreadable process list must not make a live session look copyable.
            return true;
        }
    }

    private static IReadOnlyList<string> EnumerateDirectories(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        try { return Directory.GetDirectories(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool IsNamedProcessRunning(int processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    private static bool IsExclusiveLockHeld(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.SequentialScan);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    private static IReadOnlyList<string> EnumerateFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, pattern);
    }

    private static string? ReadAllText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    private static bool IsSafeSessionId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

internal sealed class WindowsManagedSessionDirectoryDeleter : IManagedSessionDirectoryDeleter
{
    private readonly Func<bool>? afterContainmentValidation;
    private readonly Func<bool>? afterRootPathValidation;
    private readonly Func<bool>? afterPathValidation;
    private readonly Func<bool>? afterTreeCapture;

    public WindowsManagedSessionDirectoryDeleter()
    {
    }

    internal WindowsManagedSessionDirectoryDeleter(
        Func<bool>? afterPathValidation,
        Func<bool>? afterTreeCapture)
        : this(null, null, afterPathValidation, afterTreeCapture)
    {
    }

    internal WindowsManagedSessionDirectoryDeleter(
        Func<bool>? afterRootPathValidation,
        Func<bool>? afterPathValidation,
        Func<bool>? afterTreeCapture)
        : this(null, afterRootPathValidation, afterPathValidation, afterTreeCapture)
    {
    }

    internal WindowsManagedSessionDirectoryDeleter(
        Func<bool>? afterContainmentValidation,
        Func<bool>? afterRootPathValidation,
        Func<bool>? afterPathValidation,
        Func<bool>? afterTreeCapture)
    {
        this.afterContainmentValidation = afterContainmentValidation;
        this.afterRootPathValidation = afterRootPathValidation;
        this.afterPathValidation = afterPathValidation;
        this.afterTreeCapture = afterTreeCapture;
    }

    public Task DeleteAsync(string sessionsRoot, string sessionDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Managed session deletion requires Windows.");
        if (!WindowsOwnedTreeDeleter.TryGetIdentity(sessionsRoot, out var expectedRootIdentity))
            throw new IOException("The sessions root identity is unavailable.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
        if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase) ||
            !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected session directory is outside the sessions root.");
        if (afterContainmentValidation is not null && !afterContainmentValidation())
            throw new IOException("The sessions root changed after containment validation.");

        RequireConcreteAncestors(root, target, afterRootPathValidation);
        if (afterPathValidation is not null && !afterPathValidation())
            throw new IOException("The selected session directory changed before deletion.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!WindowsOwnedTreeDeleter.TryDeleteDescendantTree(
                root,
                target,
                expectedRootIdentity,
                afterTreeCapture,
                () => { cancellationToken.ThrowIfCancellationRequested(); return true; }))
            throw new IOException("The selected session directory could not be deleted safely.");
        return Task.CompletedTask;
    }

    private static void RequireConcreteAncestors(
        string root,
        string target,
        Func<bool>? afterRootPathValidation)
    {
        for (var current = target;; current = Path.GetDirectoryName(current)
                 ?? throw new InvalidDataException("The selected session directory has no sessions-root ancestor."))
        {
            var attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The selected session directory is not a concrete directory.");
            if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) continue;
            if (afterRootPathValidation is not null && !afterRootPathValidation())
                throw new IOException("The sessions root changed during validation.");
            return;
        }
    }

}

public sealed class DefaultAgentCliOperations : IAgentCliOperations
{
    private readonly AgentWorker worker;
    private readonly AgentScheduler scheduler;
    private readonly Func<string?> executablePath;

    public DefaultAgentCliOperations(AgentWorker worker, AgentScheduler scheduler, Func<string?> executablePath)
    {
        this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
    }

    public Task RunAsync(CancellationToken cancellationToken) => worker.RunAsync(cancellationToken);

    public Task InstallAsync(CancellationToken cancellationToken)
    {
        var executable = executablePath();
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("The agent executable path is unavailable.");
        return scheduler.InstallAsync(executable, cancellationToken);
    }

    public Task UninstallAsync(CancellationToken cancellationToken) => scheduler.UninstallAsync(cancellationToken);
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
        var ownedTemporary = OwnedTemporaryDirectory.Create(Path.GetTempPath(), "codex-history-sync-init-");
        var temporaryRoot = ownedTemporary.RootPath;
        var clone = Path.Combine(temporaryRoot, "repository");
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
            await RequireSuccessAsync(await git.RunAsync(["config", "user.name", "Agent History Sync"], clone, cancellationToken), "Unable to configure Git identity.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["add", "--", ManifestFileName, "repository.chs"], clone, cancellationToken), "Unable to stage initialization metadata.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["commit", "--no-gpg-sign", "-m", "Initialize encrypted Codex history"], clone, cancellationToken), "Unable to commit initialization metadata.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["push", "origin", "HEAD:main"], clone, cancellationToken), "Unable to publish initialization metadata.").ConfigureAwait(false);
            var revision = await git.RunAsync(["rev-parse", "HEAD"], clone, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(revision, "Unable to resolve initialization revision.").ConfigureAwait(false);
            return new CliPublishedInitialization(manifest.ToArray(), encryptedIndex.ToArray(), revision.StandardOutput.Trim());
        }
        finally
        {
            // Publication is authoritative. A failed ownership/safety check deliberately leaves the tree for
            // later or manual recovery and never replaces the primary result.
            ownedTemporary.TryDelete();
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
    private readonly IAgentInstallationChecker agentInstallationChecker;
    private readonly Func<string, CancellationToken, Task<CompatibilityResult>> compatibilityProbe;
    private readonly Func<CliLocalConfiguration, ReadOnlyMemory<byte>, SyncEngine>? engineFactory;
    private readonly CodexExecutableSource codexExecutableSource;
    private readonly string? codexHome;
    private readonly string? grokHome;
    private readonly string? claudeHome;
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
        string? claudeHome = null)
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
            var fixture = Path.Combine(fixtureRoot, "compatibility-fixture.jsonl");
            await File.WriteAllTextAsync(fixture,
                "{\"type\":\"session_meta\",\"payload\":{\"id\":\"compatibility-fixture\"}}\n",
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
            ClaudeUncertain = preview.UncertainKinds.Contains(ObjectKind.ClaudeSession)
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
        var scanner = new SessionScanner();
        var state = new LocalStateStore(localAppData);
        var backups = new BackupStore(configuration.RepositoryId, localAppData, paths, grokPaths: grokPaths,
            claudePaths: claudePaths);
        var conflicts = new ConflictStore(configuration.RepositoryId, localAppData, paths);
        if (!requireKey) return new Components(paths, scanner, conflicts, null!);
        var writer = new CodexHistoryWriter(paths, backups, processDetector, grokPaths: grokPaths,
            claudePaths: claudePaths);
        // First-time history upload can stage hundreds of objects; the default 30s git timeout is too short.
        IStorageProvider provider = new GitStorageProvider(configuration.RepositoryId, configuration.RemoteUrl, GitRemoteKind.GitHub,
            Path.Combine(localAppData, "CodexHistorySync", "repositories"),
            commandTimeout: TimeSpan.FromMinutes(30));
        if (pinnedRevision is not null) provider = new RevisionPinnedProvider(provider, pinnedRevision);
        var staging = Path.Combine(localAppData, "CodexHistorySync", "repositories", configuration.RepositoryId, "staging");
        var engine = engineFactory?.Invoke(configuration, key) ?? new SyncEngine(configuration.RepositoryId,
            configuration.DeviceId, paths, key, scanner, new RepositoryCrypto(), state, writer, conflicts, provider, staging,
            grokPaths: grokPaths, progress: syncProgress, claudePaths: claudePaths);
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
