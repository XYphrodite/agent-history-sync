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

public static class CliComposition
{
    internal static CliApplication CreateForArguments(
        string[] args,
        ICliConsole console,
        Func<ICliConsole, CliApplication> createSynchronizedApplication,
        Func<ISessionManagerRunner> createSessionManagerRunner,
        Func<ISessionManagerRunner>? createSessionViewerRunner = null,
        Func<ISelfUpdateOperations>? createSelfUpdateOperations = null,
        Func<ISessionSearchCommand>? createSearchCommand = null,
        Func<ISessionMcpCommand>? createMcpCommand = null)
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
            ["search", ..] when createSearchCommand is not null =>
                new CliApplication(console, createSearchCommand()),
            ["mcp", ..] when createMcpCommand is not null =>
                new CliApplication(console, createMcpCommand()),
            _ => createSynchronizedApplication(console)
        };
    }

    public static CliApplication CreateDefault(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var console = new SystemCliConsole();
        return CreateForArguments(args, console, CreateSynchronizedApplication, CreateSessionManagerRunner,
            CreateSessionViewerRunner, static () => new DefaultSelfUpdateOperations(),
            () => CreateSearchCommand(console),
            () => new SessionMcpCommand(CreateReadOnlySessionCatalog(), new SessionContentReader()));
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
        var continuePaths = ContinuePaths.TryResolve();
        var activeState = new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths);
        var catalog = new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths, continuePaths);
        // Only the viewer wears this machine's own titles; --manage stays exactly as it was.
        var annotationStore = new SessionAnnotationStore();
        var annotated = new AnnotatedSessionCatalog(catalog, annotationStore);
        // No endpoint configured means no suggester at all: the key says so and nothing is sent.
        var titling = SessionTitleConfiguration.Load();
        var suggester = titling.IsConfigured ? new OllamaSessionTitleSuggester(titling.Options) : null;
        // The viewer never copies, so no conversation writers are composed for it (design D6).
        var operations = new LocalSessionOperations(
            codexPaths,
            grokPaths,
            activeState,
            new WindowsManagedSessionDirectoryDeleter(),
            null,
            null,
            claudePaths,
            null,
            continuePaths,
            null);
        var ansiConsole = AnsiConsole.Console;
        var view = new SpectreSessionViewerView(ansiConsole, new SpectreSessionManagerInput(ansiConsole));
        var searchIndex = new SessionSearchIndex();
        var contentReader = new SessionContentReader();
        return new DefaultSessionViewerRunner(new SessionViewerApplication(
            annotated, contentReader, new SessionExporter(), operations, view,
            annotationStore, suggester, titling.Rejection, searchIndex));
    }

    private static ISessionSearchCommand CreateSearchCommand(ICliConsole console) =>
        new SessionSearchCommand(CreateReadOnlySessionCatalog(), new SessionSearchIndex(), new SessionContentReader(), console);

    private static ILocalSessionCatalog CreateReadOnlySessionCatalog()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent History Sync currently requires Windows.");
        var codexPaths = TryResolveCodexPaths();
        var grokPaths = GrokPaths.TryResolve();
        var claudePaths = ClaudePaths.TryResolve();
        var continuePaths = ContinuePaths.TryResolve();
        var activeState = new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths);
        var catalog = new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths, continuePaths);
        var annotationStore = new SessionAnnotationStore();
        return new AnnotatedSessionCatalog(catalog, annotationStore);
    }

    private static ISessionManagerRunner CreateSessionManagerRunner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent History Sync currently requires Windows.");
        var codexPaths = TryResolveCodexPaths();
        var grokPaths = GrokPaths.TryResolve();
        var claudePaths = ClaudePaths.TryResolve();
        var continuePaths = ContinuePaths.TryResolve();
        var resolution = new CodexExecutableLocator().ResolveWithSource();
        var executable = ToCodexExecutableOption(resolution);
        var activeState = new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths);
        var catalog = new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths, continuePaths);
        var codexWriter = codexPaths is null
            ? null
            : new CodexConversationWriter(codexPaths, executable, new CodexCompatibilityProbe());
        var grokWriter = grokPaths is null ? null : new GrokConversationWriter(grokPaths);
        var claudeWriter = claudePaths is null ? null : new ClaudeConversationWriter(claudePaths);
        var continueWriter = continuePaths is null ? null : new ContinueConversationWriter(continuePaths);
        var operations = new LocalSessionOperations(
            codexPaths,
            grokPaths,
            activeState,
            new WindowsManagedSessionDirectoryDeleter(),
            codexWriter,
            grokWriter,
            claudePaths,
            claudeWriter,
            continuePaths,
            continueWriter);
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

