using System.Globalization;
using System.Security.Cryptography;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Core.Update;

namespace CodexHistorySync.Cli;

public sealed record CliGateResult(bool Passed, string Name, string? Diagnostic = null);
public sealed record CliInitializationResult(string RepositoryId);
public sealed record CliAuthenticatedRepository(string RepositoryId, string RemoteRevision);
public sealed record CliJoinPlan(int Local, int Remote, int Pending, int Conflicts);
public sealed record CliStatusReport(int Local, int Remote, int Pending, int Conflicts, string RemoteRevision,
    string LastSuccessfulRevision)
{
    /// <summary>Resolved Claude projects root, or null when no Claude home was found.</summary>
    public string? ClaudeHome { get; init; }

    /// <summary>Resolved Continue sessions directory, or null when no Continue home was found.</summary>
    public string? ContinueHome { get; init; }

    public int ContinueSessions { get; init; }

    /// <summary>True when the Continue scan could not confirm what it did not find.</summary>
    public bool ContinueUncertain { get; init; }

    public int ClaudeSessions { get; init; }

    /// <summary>True when the Claude scan could not confirm what it did not find.</summary>
    public bool ClaudeUncertain { get; init; }
}
public sealed record CliDoctorCheck(string Name, bool Passed);
public sealed record CliDoctorReport(IReadOnlyList<CliDoctorCheck> Checks);
public sealed record CliConflictInfo(string Id, string LocalHash, string RemoteHash, string LocalDeviceId,
    string RemoteDeviceId, DateTimeOffset LocalTimestampUtc, DateTimeOffset RemoteTimestampUtc);
public sealed record CliResolutionResult(int RemainingConflicts, bool Exported);

public enum CliResolution { KeepLocal, KeepRemote, ExportBoth }

public sealed class CliGateException : Exception
{
    public CliGateException(string message) : base(message) { }
    public CliGateException(string message, Exception innerException) : base(message, innerException) { }
}

public interface ICliConsole
{
    void WriteLine(string value);
    void WriteError(string value);
    Task<char[]> ReadSecretAsync(string prompt, CancellationToken cancellationToken);
}

public interface ICliServices
{
    Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken);
    Task<CliGateResult> VerifyPrivateRepositoryAsync(string remoteUrl, CancellationToken cancellationToken);
    Task<CliInitializationResult> InitializeAsync(string remoteUrl, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken);
    Task<CliAuthenticatedRepository> AuthenticateRepositoryAsync(string remoteUrl, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken);
    Task<CliGateResult> ProbeCompatibilityAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken);
    Task<CompatibilityResult> ProbeCompatibilitySessionAsync(string sourceSession, string codexExecutable,
        CancellationToken cancellationToken);
    Task<CliJoinPlan> PlanJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken);
    Task<SyncResult> ApplyJoinAsync(CliAuthenticatedRepository repository, CliJoinPlan plan, CancellationToken cancellationToken);
    Task AbortJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken);
    Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken);
    Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken);
    Task<CliDoctorReport> RunDoctorAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CancellationToken cancellationToken);
    Task<CliResolutionResult> ResolveAsync(string conflictId, CliResolution resolution, string? exportDirectory, CancellationToken cancellationToken);
}

public interface IAgentCliOperations
{
    Task RunAsync(CancellationToken cancellationToken);
    Task InstallAsync(CancellationToken cancellationToken);
    Task UninstallAsync(CancellationToken cancellationToken);
}

public interface ISessionManagerRunner
{
    Task RunAsync(CancellationToken cancellationToken);
}

public interface ISelfUpdateOperations
{
    Task<SelfUpdateReport> UpdateAsync(SelfUpdateRequest request, CancellationToken cancellationToken);
}

public sealed class CliApplication
{
    private readonly ICliServices? services;
    private readonly ICliConsole console;
    private readonly IAgentCliOperations? agentOperations;
    private readonly ISessionManagerRunner? managerRunner;
    private readonly ISelfUpdateOperations? selfUpdate;
    private readonly string? localAppDataDirectory;

    public CliApplication(
        ICliServices services,
        ICliConsole console,
        IAgentCliOperations? agentOperations = null,
        ISessionManagerRunner? managerRunner = null,
        ISelfUpdateOperations? selfUpdate = null,
        string? localAppDataDirectory = null)
    {
        this.localAppDataDirectory = localAppDataDirectory;
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.agentOperations = agentOperations;
        this.managerRunner = managerRunner;
        this.selfUpdate = selfUpdate;
    }

    internal CliApplication(ICliConsole console, ISessionManagerRunner managerRunner)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.managerRunner = managerRunner ?? throw new ArgumentNullException(nameof(managerRunner));
    }

    /// <summary>
    /// Update-only application. The moment a machine most needs a newer binary is the moment
    /// its Git, GitHub, or Codex setup is broken, so this path constructs none of them.
    /// </summary>
    internal CliApplication(ICliConsole console, ISelfUpdateOperations selfUpdate)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.selfUpdate = selfUpdate ?? throw new ArgumentNullException(nameof(selfUpdate));
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            // Both screens are composed the same way and differ only in which runner arrived.
            if (args is ["--manage"] or ["--sessions"])
            {
                if (managerRunner is null) return Usage();
                await managerRunner.RunAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            return args.Length == 0 ? Usage() : args[0] switch
            {
                "--help" or "-h" when args.Length == 1 => Help(),
                "--version" when args.Length == 1 => ReportVersion(),
                "init" => await RunInitAsync(args, cancellationToken).ConfigureAwait(false),
                "join" => await RunJoinAsync(args, cancellationToken).ConfigureAwait(false),
                "sync" when args.Length == 1 => await RunSyncAsync(SyncMode.Bidirectional, cancellationToken).ConfigureAwait(false),
                "pull" when args.Length == 1 => await RunSyncAsync(SyncMode.Pull, cancellationToken).ConfigureAwait(false),
                "push" when args.Length == 1 => await RunSyncAsync(SyncMode.Push, cancellationToken).ConfigureAwait(false),
                "status" when args.Length == 1 => await RunStatusAsync(cancellationToken).ConfigureAwait(false),
                "doctor" => await RunDoctorAsync(args, cancellationToken).ConfigureAwait(false),
                "conflicts" when args.Length == 1 => await RunConflictsAsync(cancellationToken).ConfigureAwait(false),
                "resolve" => await RunResolveAsync(args, cancellationToken).ConfigureAwait(false),
                "agent" => await RunAgentAsync(args, cancellationToken).ConfigureAwait(false),
                "update" => await RunUpdateAsync(args, cancellationToken).ConfigureAwait(false),
                "titles" => await RunTitlesAsync(args, cancellationToken).ConfigureAwait(false),
                _ => Usage()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CliGateException)
        {
            console.WriteError("Security or compatibility gate failed.");
            return 3;
        }
        catch (Exception exception)
        {
            // Keep output free of paths/secrets; surface only a stable type token for support.
            console.WriteError($"Operation failed: {SafeToken(exception.GetType().Name)}.");
            // A type name on its own says nothing about which of a dozen directories was missing.
            // The detail goes to a file on the machine that failed, where the paths in it are
            // already visible anyway, rather than onto a screen that may be shared.
            if (WriteFailureReport(exception) is { } report) console.WriteError($"Details: {report}");
            return 1;
        }
    }

    private async Task<int> RunInitAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1])) return Usage();
        var gate = await Services.VerifyInitializationTargetAsync(args[1], cancellationToken).ConfigureAwait(false);
        if (!gate.Passed) return GateFailure(gate.Name);

        char[]? first = null;
        char[]? confirmation = null;
        try
        {
            first = await console.ReadSecretAsync("Passphrase: ", cancellationToken).ConfigureAwait(false);
            confirmation = await console.ReadSecretAsync("Confirm passphrase: ", cancellationToken).ConfigureAwait(false);
            if (first.Length == 0 || !FixedTimeEquals(first, confirmation))
            {
                console.WriteError("Passphrases must be non-empty and match.");
                return 2;
            }
            var result = await Services.InitializeAsync(args[1], first, cancellationToken).ConfigureAwait(false);
            console.WriteLine($"Initialized repository {SafeToken(result.RepositoryId)}.");
            return 0;
        }
        finally
        {
            if (first is not null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(first.AsSpan()));
            if (confirmation is not null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(confirmation.AsSpan()));
        }
    }

    private async Task<int> RunJoinAsync(string[] args, CancellationToken cancellationToken)
    {
        var apply = args.Length == 3 && args[2] == "--apply";
        if ((args.Length != 2 && !apply) || string.IsNullOrWhiteSpace(args[1])) return Usage();
        var gate = await Services.VerifyPrivateRepositoryAsync(args[1], cancellationToken).ConfigureAwait(false);
        if (!gate.Passed) return GateFailure(gate.Name);

        char[]? passphrase = null;
        CliAuthenticatedRepository? repository = null;
        try
        {
            passphrase = await console.ReadSecretAsync("Passphrase: ", cancellationToken).ConfigureAwait(false);
            if (passphrase.Length == 0) return Usage();
            repository = await Services.AuthenticateRepositoryAsync(args[1], passphrase, cancellationToken).ConfigureAwait(false);
            var compatibility = await Services.ProbeCompatibilityAsync(repository, cancellationToken).ConfigureAwait(false);
            if (!compatibility.Passed) return GateFailure(compatibility.Name, compatibility.Diagnostic);
            if (!string.IsNullOrWhiteSpace(compatibility.Diagnostic) &&
                compatibility.Diagnostic.StartsWith("skipped-no-codex", StringComparison.Ordinal))
            {
                console.WriteLine("warning: codex-compatibility skipped (Codex executable not found). " +
                    "Install the OpenAI Codex IDE extension or set CODEX_EXE so Codex can reindex imported sessions.");
            }
            var plan = await Services.PlanJoinAsync(repository, cancellationToken).ConfigureAwait(false);
            console.WriteLine($"Join plan: local={plan.Local} remote={plan.Remote} pending={plan.Pending} conflicts={plan.Conflicts}.");
            if (!apply)
            {
                console.WriteLine("Dry run only; repeat with --apply to perform the first import.");
                return plan.Conflicts == 0 ? 0 : 4;
            }
            var result = await Services.ApplyJoinAsync(repository, plan, cancellationToken).ConfigureAwait(false);
            console.WriteLine($"Join applied: revision={SafeToken(result.RemoteRevision)} downloaded={result.Downloaded} deleted={result.Deleted} conflicts={result.Conflicts}.");
            return result.Conflicts == 0 ? 0 : 4;
        }
        finally
        {
            if (repository is not null) await Services.AbortJoinAsync(repository, CancellationToken.None).ConfigureAwait(false);
            if (passphrase is not null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(passphrase.AsSpan()));
        }
    }

    private async Task<int> RunSyncAsync(SyncMode mode, CancellationToken cancellationToken)
    {
        var result = await Services.SynchronizeAsync(mode, cancellationToken).ConfigureAwait(false);
        console.WriteLine($"revision={SafeToken(result.RemoteRevision)} uploaded={result.Uploaded} downloaded={result.Downloaded} deleted={result.Deleted} conflicts={result.Conflicts} skipped-oversized={result.SkippedOversized}");
        WriteLocalBreakdown(result.LocalByKind, result.LocalIgnored);
        return result.Conflicts == 0 ? 0 : 4;
    }

    /// <summary>
    /// The counters above say what moved; this says what the run actually holds. Sessions are
    /// grouped by the agent that owns them, because that is the unit a reader thinks in — the
    /// object kinds are an implementation detail everywhere except inside Codex, where the
    /// archived and attachment splits are worth naming.
    /// </summary>
    private void WriteLocalBreakdown(IReadOnlyDictionary<ObjectKind, SessionKindTotals> byKind, int ignored)
    {
        if (byKind.Count == 0) return;
        var groups = new (string Name, ObjectKind[] Kinds)[]
        {
            ("codex", [ObjectKind.ActiveSession, ObjectKind.ArchivedSession, ObjectKind.Attachment]),
            ("grok", [ObjectKind.GrokSession]),
            ("claude", [ObjectKind.ClaudeSession]),
            ("annotations", [ObjectKind.SessionAnnotations]),
        };
        var grouped = groups.Select(group => (group.Name, group.Kinds, Totals: Sum(byKind, group.Kinds))).ToArray();
        var accounted = groups.SelectMany(group => group.Kinds).ToHashSet();
        var other = Sum(byKind, byKind.Keys.Where(kind => !accounted.Contains(kind)).ToArray());
        var total = Sum(byKind, byKind.Keys.ToArray());

        var excluded = ignored == 0 ? string.Empty : $" excluded={ignored}";
        console.WriteLine($"local={total.Count} size={FormatSize(total.Bytes)}{excluded}");
        foreach (var (name, kinds, totals) in grouped)
        {
            if (totals.Count == 0) continue;
            var detail = name == "codex"
                ? $" (active={Sum(byKind, [kinds[0]]).Count} archived={Sum(byKind, [kinds[1]]).Count} attachments={Sum(byKind, [kinds[2]]).Count})"
                : string.Empty;
            console.WriteLine($"  {name}={totals.Count} size={FormatSize(totals.Bytes)}{detail}");
        }
        if (other.Count > 0) console.WriteLine($"  other={other.Count} size={FormatSize(other.Bytes)}");
    }

    private static SessionKindTotals Sum(IReadOnlyDictionary<ObjectKind, SessionKindTotals> byKind, ObjectKind[] kinds)
    {
        var count = 0;
        var bytes = 0L;
        foreach (var kind in kinds)
            if (byKind.TryGetValue(kind, out var totals))
            {
                count += totals.Count;
                bytes += totals.Bytes;
            }
        return new SessionKindTotals(count, bytes);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        double value = bytes;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        var formatted = index == 0
            ? bytes.ToString(CultureInfo.InvariantCulture)
            : value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture);
        return formatted + " " + units[index];
    }

    private async Task<int> RunStatusAsync(CancellationToken cancellationToken)
    {
        var result = await Services.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        console.WriteLine($"local={result.Local} remote={result.Remote} pending={result.Pending} conflicts={result.Conflicts} " +
            $"remote-revision={SafeToken(result.RemoteRevision)} last-successful-revision={SafeToken(result.LastSuccessfulRevision)}");
        console.WriteLine($"claude-home={(result.ClaudeHome is null ? "none" : SafeToken(result.ClaudeHome))} " +
            $"claude-sessions={result.ClaudeSessions} claude-uncertain={(result.ClaudeUncertain ? "yes" : "no")}");
        console.WriteLine($"continue-home={(result.ContinueHome is null ? "none" : SafeToken(result.ContinueHome))} " +
            $"continue-sessions={result.ContinueSessions} continue-uncertain={(result.ContinueUncertain ? "yes" : "no")}");
        return result.Conflicts == 0 ? 0 : 4;
    }

    private async Task<int> RunDoctorAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 1)
        {
            if (!TryParseCompatibilityDoctorArguments(args, out var sourceSession, out var codexExecutable))
                return Usage();
            var compatibility = await Services.ProbeCompatibilitySessionAsync(sourceSession!, codexExecutable!, cancellationToken)
                .ConfigureAwait(false);
            console.WriteLine($"codex-compatibility: {(compatibility.IsCompatible ? "PASS" : "FAIL")} " +
                $"version={SafeToken(compatibility.CodexVersion)} diagnostic={SafeToken(compatibility.Diagnostic)}");
            return compatibility.IsCompatible ? 0 : 3;
        }

        var report = await Services.RunDoctorAsync(cancellationToken).ConfigureAwait(false);
        foreach (var check in report.Checks)
            console.WriteLine($"{SafeToken(check.Name)}: {(check.Passed ? "PASS" : "FAIL")}");
        return report.Checks.All(check => check.Passed) ? 0 : 3;
    }

    private static bool TryParseCompatibilityDoctorArguments(string[] args, out string? sourceSession,
        out string? codexExecutable)
    {
        sourceSession = null;
        codexExecutable = null;
        if (args.Length != 5) return false;
        for (var index = 1; index < args.Length; index += 2)
        {
            var option = args[index];
            var value = args[index + 1];
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (option == "--compatibility-session" && sourceSession is null) sourceSession = value;
            else if (option == "--codex-exe" && codexExecutable is null) codexExecutable = value;
            else return false;
        }
        return sourceSession is not null && codexExecutable is not null;
    }

    private async Task<int> RunConflictsAsync(CancellationToken cancellationToken)
    {
        var conflicts = await Services.ListConflictsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var conflict in conflicts)
        {
            console.WriteLine($"id={SafeToken(conflict.Id)} local-hash={SafeToken(conflict.LocalHash)} remote-hash={SafeToken(conflict.RemoteHash)} " +
                $"local-device={SafeToken(conflict.LocalDeviceId)} remote-device={SafeToken(conflict.RemoteDeviceId)} " +
                $"local-time={conflict.LocalTimestampUtc:O} remote-time={conflict.RemoteTimestampUtc:O}");
        }
        return conflicts.Count == 0 ? 0 : 4;
    }

    private async Task<int> RunResolveAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 3 || string.IsNullOrWhiteSpace(args[1])) return Usage();
        CliResolution resolution;
        string? exportDirectory = null;
        if (args.Length == 3 && args[2] == "--keep-local") resolution = CliResolution.KeepLocal;
        else if (args.Length == 3 && args[2] == "--keep-remote") resolution = CliResolution.KeepRemote;
        else if (args.Length == 4 && args[2] == "--export-both" && !string.IsNullOrWhiteSpace(args[3]))
        {
            resolution = CliResolution.ExportBoth;
            exportDirectory = args[3];
        }
        else return Usage();

        var result = await Services.ResolveAsync(args[1], resolution, exportDirectory, cancellationToken).ConfigureAwait(false);
        console.WriteLine(result.Exported
            ? $"Exported conflict {SafeToken(args[1])}; it remains unresolved."
            : $"Resolved conflict {SafeToken(args[1])}.");
        return result.RemainingConflicts == 0 ? 0 : 4;
    }

    private async Task<int> RunAgentAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2 || agentOperations is null) return Usage();
        switch (args[1])
        {
            case "run":
                await agentOperations.RunAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "install":
                await agentOperations.InstallAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "uninstall":
                await agentOperations.UninstallAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                return Usage();
        }
        return 0;
    }

    /// <summary>
    /// Turns session titling on, off, and shows what it is pointed at. It is the only command
    /// that writes titling configuration: hand-editing the file is not the supported way in.
    /// </summary>
    private async Task<int> RunTitlesAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 1) return ShowTitles();

        switch (args[1])
        {
            case "set" when args.Length >= 3:
                return SetTitles(args);
            case "off" when args.Length == 2:
                console.WriteLine(SessionTitleConfiguration.Disable(localAppDataDirectory)
                    ? "titling=off (configuration removed)"
                    : "titling=off (nothing was configured)");
                return 0;
            case "test" when args.Length == 2:
                return await TestTitlesAsync(cancellationToken).ConfigureAwait(false);
            default:
                return Usage();
        }
    }

    private int ShowTitles()
    {
        var configuration = SessionTitleConfiguration.Load(localAppDataDirectory);
        if (!configuration.IsConfigured)
        {
            console.WriteLine("titling=off");
            if (configuration.Rejection is { } rejection) console.WriteLine($"  reason={SafeText(rejection)}");
            console.WriteLine("  turn it on: agent-sync titles set http://<host>:11434");
            return 0;
        }

        console.WriteLine("titling=on");
        console.WriteLine($"  endpoint={SafeText(configuration.Options.Endpoint)}");
        console.WriteLine($"  model={SafeToken(configuration.Options.Model)}");
        console.WriteLine($"  language={SafeToken(configuration.Options.Language)}");
        WriteTitleOverrides();
        return 0;
    }

    private int SetTitles(string[] args)
    {
        var endpoint = args[2];
        string? model = null;
        string? language = null;
        for (var index = 3; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--model" when model is null && index + 1 < args.Length:
                    model = args[++index];
                    break;
                case "--language" when language is null && index + 1 < args.Length:
                    language = args[++index];
                    break;
                default:
                    return Usage();
            }
        }

        if (language is not null && language is not ("auto" or "ru" or "en")) return Usage();

        var saved = SessionTitleConfiguration.Save(
            new SessionTitleOptions(endpoint, model ?? SessionTitleOptions.DefaultModel, language ?? "auto"),
            localAppDataDirectory);
        if (!saved.IsConfigured)
        {
            // Refused when it is typed rather than stored and quietly ignored later.
            console.WriteError($"Endpoint refused: {SafeText(saved.Rejection)}");
            return 2;
        }

        console.WriteLine("titling=on");
        console.WriteLine($"  endpoint={SafeText(saved.Options.Endpoint)}");
        console.WriteLine($"  model={SafeToken(saved.Options.Model)}");
        console.WriteLine($"  language={SafeToken(saved.Options.Language)}");
        console.WriteLine($"  stored in {SafeText(SessionTitleConfiguration.PathFor(localAppDataDirectory))}");
        WriteTitleOverrides();
        return 0;
    }

    /// <summary>Asks the endpoint to name a sample session, which is the only honest check.</summary>
    private async Task<int> TestTitlesAsync(CancellationToken cancellationToken)
    {
        var configuration = SessionTitleConfiguration.Load(localAppDataDirectory);
        if (!configuration.IsConfigured)
        {
            console.WriteError(configuration.Rejection is { } rejection
                ? $"Titling is not configured: {SafeText(rejection)}"
                : "Titling is not configured.");
            return 2;
        }

        const string sample = "USER: the event log service was stopped and ollama could not start a runner\n\n" +
                              "ASSISTANT: starting the service brought it back";
        console.WriteLine($"Asking {SafeToken(configuration.Options.Model)} at " +
            $"{SafeText(configuration.Options.Endpoint)} to name a sample session\u2026");

        using var suggester = new OllamaSessionTitleSuggester(configuration.Options);
        var started = DateTimeOffset.UtcNow;
        var draft = await suggester.SuggestAsync(SessionDigest.Build(SampleConversation(sample)), cancellationToken)
            .ConfigureAwait(false);
        var seconds = (DateTimeOffset.UtcNow - started).TotalSeconds;

        if (draft is null)
        {
            console.WriteError($"titling=failed seconds={seconds.ToString("F1", CultureInfo.InvariantCulture)}");
            // Which half failed matters: a host that is down and a model that will not start need
            // different things done to them.
            if (suggester.LastFailure is { } reason) console.WriteError($"  reason={SafeText(reason)}");
            console.WriteError(
                await EndpointAnswersAsync(configuration.Options.Endpoint!, cancellationToken).ConfigureAwait(false)
                    ? "The host answered, but no usable title came back. The model may still be loading, " +
                      "or its runner may have failed to start - try again, and check `ollama ps` there."
                    : "The host did not answer at all. Check that it is running and reachable at that address.");
            return 1;
        }

        console.WriteLine($"titling=ok seconds={seconds.ToString("F1", CultureInfo.InvariantCulture)}");
        console.WriteLine($"  title={SafeText(draft.Title)}");
        console.WriteLine($"  description={SafeText(draft.Description)}");
        return 0;
    }

    /// <summary>A plain liveness question, asked only to explain a probe that came back empty.</summary>
    private static async Task<bool> EndpointAnswersAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client
                .GetAsync(endpoint.TrimEnd('/') + "/api/version", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                             or InvalidOperationException or UriFormatException)
        {
            return false;
        }
    }

    private static Core.Conversion.PortableConversation SampleConversation(string text) => new(
        Core.Conversion.ConversationAgent.Claude,
        "sample",
        "sample",
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        [new Core.Conversion.PortableTurn(Core.Conversion.ConversationRole.User, text)]);

    /// <summary>An environment variable beats the file, so say so rather than let it confuse.</summary>
    private void WriteTitleOverrides()
    {
        foreach (var name in new[]
                 {
                     SessionTitleConfiguration.EndpointVariable,
                     SessionTitleConfiguration.ModelVariable,
                     SessionTitleConfiguration.LanguageVariable
                 })
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                console.WriteLine($"  note: {name} is set and overrides the file");
        }
    }

    private static string SafeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var trimmed = value.Length <= 200 ? value : value[..200];
        return new string(trimmed.Select(character =>
            char.IsControl(character) ? '_' : character).ToArray());
    }

    private async Task<int> RunUpdateAsync(string[] args, CancellationToken cancellationToken)
    {
        if (selfUpdate is null) return Usage();

        var checkOnly = false;
        string? tag = null;
        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--check" when !checkOnly:
                    checkOnly = true;
                    break;
                case "--version" when tag is null && index + 1 < args.Length:
                    tag = args[++index];
                    break;
                default:
                    return Usage();
            }
        }

        if (tag is not null && !ReleaseVersion.TryParse(tag, out _)) return Usage();

        try
        {
            var report = await selfUpdate.UpdateAsync(new SelfUpdateRequest(checkOnly, tag), cancellationToken)
                .ConfigureAwait(false);
            switch (report.Status)
            {
                case SelfUpdateStatus.AlreadyCurrent:
                    console.WriteLine($"Already up to date: agent-sync {report.Installed} (latest release {SafeToken(report.Tag)}).");
                    break;
                case SelfUpdateStatus.UpdateAvailable when report.Release > report.Installed:
                    console.WriteLine($"Update available: {report.Installed} -> {report.Release} ({SafeToken(report.Tag)}).");
                    console.WriteLine("Run 'agent-sync update' to install it.");
                    break;
                case SelfUpdateStatus.UpdateAvailable:
                    // A pinned tag reaches this branch by being asked for, not by being newer,
                    // so calling it an available update would misread as a version bump.
                    console.WriteLine($"Pinned release {SafeToken(report.Tag)} would replace {report.Installed} with {report.Release}.");
                    console.WriteLine($"Run 'agent-sync update --version {SafeToken(report.Tag)}' to install it.");
                    break;
                default:
                    console.WriteLine($"Updated agent-sync {report.Installed} -> {report.Release} ({SafeToken(report.Tag)}).");
                    console.WriteLine("The replaced binary stays beside the new one until a later run removes it.");
                    break;
            }
            return 0;
        }
        catch (InvalidDataException exception)
        {
            // Every message on this path is authored in the update code and carries no path or
            // secret, so the reason serves the user better than the generic type token.
            console.WriteError($"Update failed: {exception.Message}");
            return 1;
        }
    }

    private int ReportVersion()
    {
        // The commit is what turns a bug report into a diff; the version alone cannot tell two
        // builds of the same release apart.
        console.WriteLine($"agent-sync {CliVersion.Current} (commit {SafeToken(CliBuildInfo.Commit)})");
        return 0;
    }

    private int Usage()
    {
        console.WriteError("Usage: agent-sync <init|join|sync|pull|push|status|doctor|conflicts|resolve|agent|update|titles> [options] [--manage] [--sessions] [--version]");
        console.WriteError("  titles                       show what session titling is configured with");
        console.WriteError("  titles set <endpoint> [--model <name>] [--language <auto|ru|en>]");
        console.WriteError("  titles off                   turn session titling off");
        console.WriteError("  titles test                  ask the endpoint to name a sample session");
        return 2;
    }

    private int Help()
    {
        console.WriteLine("Usage: agent-sync <init|join|sync|pull|push|status|doctor|conflicts|resolve|agent|update|titles> [options] [--manage] [--sessions] [--version]");
        console.WriteLine("  titles                       show what session titling is configured with");
        console.WriteLine("  titles set <endpoint> [--model <name>] [--language <auto|ru|en>]");
        console.WriteLine("  titles off                   turn session titling off");
        console.WriteLine("  titles test                  ask the endpoint to name a sample session");
        console.WriteLine("doctor [--compatibility-session <jsonl> --codex-exe <path>]");
        console.WriteLine("update [--check] [--version <tag>]  install the latest published release");
        console.WriteLine("--manage    copy and delete sessions across agents");
        console.WriteLine("--sessions  read session contents, search, export, delete");
        console.WriteLine("--version   print the installed version");
        return 0;
    }

    private int GateFailure(string name, string? diagnostic = null)
    {
        console.WriteError($"Gate failed: {SafeToken(name)}.");
        if (!string.IsNullOrWhiteSpace(diagnostic))
            console.WriteError($"diagnostic: {SafeToken(diagnostic)}");
        return 3;
    }

    private static bool FixedTimeEquals(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var difference = left.Length ^ right.Length;
        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var leftValue = index < left.Length ? left[index] : '\0';
            var rightValue = index < right.Length ? right[index] : '\0';
            difference |= leftValue ^ rightValue;
        }
        return difference == 0;
    }

    /// <summary>
    /// Records the last failure where a person can find it. Best effort by design: a diagnostic
    /// that throws would replace the failure being diagnosed.
    /// </summary>
    private string? WriteFailureReport(Exception exception)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(localAppDataDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : localAppDataDirectory;
            if (string.IsNullOrWhiteSpace(root)) return null;
            var directory = Path.Combine(root, "CodexHistorySync", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "last-failure.log");
            File.WriteAllText(path,
                $"agent-sync {CliVersion.Current} (commit {CliBuildInfo.Commit})" + Environment.NewLine +
                DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine + Environment.NewLine +
                exception.ToString() + Environment.NewLine);
            return path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return new string(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' ? character : '_').ToArray());
    }

    private ICliServices Services => services ?? throw new InvalidOperationException("CLI services are unavailable.");
}
