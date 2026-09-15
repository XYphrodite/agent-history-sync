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
using CodexHistorySync.Core.Kimi;
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
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) throw new InvalidOperationException("Local application data is unavailable.");
        var gateway = new StorageRepositoryGateway();
        var local = new FileCliLocalRepository(localAppData,
            new DpapiKeyStore(Path.Combine(localAppData, "CodexHistorySync", "keys")));
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
        return new CliApplication(services, console, agent, selfUpdate: new DefaultSelfUpdateOperations(),
            localAppDataDirectory: localAppData);
    }

    private static ISessionManagerRunner CreateSessionViewerRunner()
    {
        var codexPaths = TryResolveCodexPaths();
        var grokPaths = GrokPaths.TryResolve();
        var claudePaths = ClaudePaths.TryResolve();
        var continuePaths = ContinuePaths.TryResolve();
        var kimiPaths = KimiPaths.TryResolve();
        IManagedSessionActiveState activeState = OperatingSystem.IsWindows()
            ? new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths, kimiPaths)
            : new ReadOnlySessionActiveState();
        var annotations = new SessionAnnotationStore();
        var catalog = new AnnotatedSessionCatalog(
            new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths, continuePaths, kimiPaths), annotations);
        var conversations = new SessionContentReader();
        var titling = SessionTitleConfiguration.Load(Environment.GetEnvironmentVariable("LOCALAPPDATA"));
        ILocalSessionOperations? operations = OperatingSystem.IsWindows()
            ? new LocalSessionOperations(codexPaths, grokPaths, activeState, new WindowsManagedSessionDirectoryDeleter(),
                null, null, claudePaths, null, continuePaths, null, kimiPaths, null)
            : null;
        return new DesktopSessionViewerRunner(new Desktop.DesktopSessionServices(catalog,
            new Core.Viewing.SessionTraceReader(conversations), new Core.Viewing.CodexSessionFamilyReader(codexPaths),
            conversations, annotations, operations,
            titling.IsConfigured ? new OllamaSessionTitleSuggester(titling.Options) : null, new SessionSearchIndex(),
            $"{CliBuildInfo.Version} · {CliBuildInfo.Commit}"));
    }

    // Retained as source for reference and existing terminal-view tests; no public command routes here.
    private static ISessionManagerRunner CreateTerminalSessionViewerRunner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent History Sync currently requires Windows.");
        var codexPaths = TryResolveCodexPaths();
        var grokPaths = GrokPaths.TryResolve();
        var claudePaths = ClaudePaths.TryResolve();
        var continuePaths = ContinuePaths.TryResolve();
        var kimiPaths = KimiPaths.TryResolve();
        var activeState = new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths, kimiPaths);
        var catalog = new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths, continuePaths, kimiPaths);
        // Only the viewer wears this machine's own titles; --manage stays exactly as it was.
        var annotationStore = new SessionAnnotationStore();
        var annotated = new AnnotatedSessionCatalog(catalog, annotationStore);
        // No endpoint configured means no suggester at all: the key says so and nothing is sent.
        var titling = SessionTitleConfiguration.Load(Environment.GetEnvironmentVariable("LOCALAPPDATA"));
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
            null,
            kimiPaths,
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
        var kimiPaths = KimiPaths.TryResolve();
        var activeState = new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths, kimiPaths);
        var catalog = new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths, continuePaths, kimiPaths);
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
        var kimiPaths = KimiPaths.TryResolve();
        var resolution = new CodexExecutableLocator().ResolveWithSource();
        var executable = ToCodexExecutableOption(resolution);
        var activeState = new WindowsManagedSessionActiveState(codexPaths, grokPaths, claudePaths, kimiPaths);
        var catalog = new LocalSessionCatalog(codexPaths, grokPaths, activeState, claudePaths, continuePaths, kimiPaths);
        var codexWriter = codexPaths is null
            ? null
            : new CodexConversationWriter(codexPaths, executable, new CodexCompatibilityProbe());
        var grokWriter = grokPaths is null ? null : new GrokConversationWriter(grokPaths);
        var claudeWriter = claudePaths is null ? null : new ClaudeConversationWriter(claudePaths);
        var continueWriter = continuePaths is null ? null : new ContinueConversationWriter(continuePaths);
        var kimiWriter = kimiPaths is null ? null : new KimiConversationWriter(kimiPaths);
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
            continueWriter,
            kimiPaths,
            kimiWriter);
        var annotationStore = new SessionAnnotationStore();
        var annotatedCatalog = new AnnotatedSessionCatalog(catalog, annotationStore);
        var conversations = new CodexHistorySync.Core.Management.SessionContentReader();
        return new DesktopSessionManagerRunner(new Desktop.DesktopSessionServices(annotatedCatalog,
            new Core.Viewing.SessionTraceReader(conversations), new Core.Viewing.CodexSessionFamilyReader(codexPaths),
            conversations, annotationStore, operations, null, new SessionSearchIndex(),
            $"{CliBuildInfo.Version} · {CliBuildInfo.Commit}"));
    }

    // Kept for existing TUI tests; no longer wired to --manage.
    private static ISessionManagerRunner CreateTerminalSessionManagerRunner()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Agent History Sync currently requires Windows.");
        var codexPaths2 = TryResolveCodexPaths();
        var grokPaths2 = GrokPaths.TryResolve();
        var claudePaths2 = ClaudePaths.TryResolve();
        var continuePaths2 = ContinuePaths.TryResolve();
        var kimiPaths2 = KimiPaths.TryResolve();
        var resolution2 = new CodexExecutableLocator().ResolveWithSource();
        var executable2 = ToCodexExecutableOption(resolution2);
        var activeState2 = new WindowsManagedSessionActiveState(codexPaths2, grokPaths2, claudePaths2, kimiPaths2);
        var catalog2 = new LocalSessionCatalog(codexPaths2, grokPaths2, activeState2, claudePaths2, continuePaths2, kimiPaths2);
        var codexWriter2 = codexPaths2 is null ? null : new CodexConversationWriter(codexPaths2, executable2, new CodexCompatibilityProbe());
        var grokWriter2 = grokPaths2 is null ? null : new GrokConversationWriter(grokPaths2);
        var claudeWriter2 = claudePaths2 is null ? null : new ClaudeConversationWriter(claudePaths2);
        var continueWriter2 = continuePaths2 is null ? null : new ContinueConversationWriter(continuePaths2);
        var kimiWriter2 = kimiPaths2 is null ? null : new KimiConversationWriter(kimiPaths2);
        var operations2 = new LocalSessionOperations(codexPaths2, grokPaths2, activeState2, new WindowsManagedSessionDirectoryDeleter(), codexWriter2, grokWriter2, claudePaths2, claudeWriter2, continuePaths2, continueWriter2, kimiPaths2, kimiWriter2);
        var ansiConsole2 = AnsiConsole.Console;
        var view2 = new SpectreSessionManagerView(ansiConsole2, new SpectreSessionManagerInput(ansiConsole2));
        return new DefaultSessionManagerRunner(new SessionManagerApplication(catalog2, operations2, view2));
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

