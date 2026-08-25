using System.Globalization;
using System.Security.Cryptography;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

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

public sealed class CliApplication
{
    private readonly ICliServices? services;
    private readonly ICliConsole console;
    private readonly IAgentCliOperations? agentOperations;
    private readonly ISessionManagerRunner? managerRunner;

    public CliApplication(
        ICliServices services,
        ICliConsole console,
        IAgentCliOperations? agentOperations = null,
        ISessionManagerRunner? managerRunner = null)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.agentOperations = agentOperations;
        this.managerRunner = managerRunner;
    }

    internal CliApplication(ICliConsole console, ISessionManagerRunner managerRunner)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.managerRunner = managerRunner ?? throw new ArgumentNullException(nameof(managerRunner));
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            if (args is ["--manage"])
            {
                if (managerRunner is null) return Usage();
                await managerRunner.RunAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            return args.Length == 0 ? Usage() : args[0] switch
            {
                "--help" or "-h" when args.Length == 1 => Help(),
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

    private int Usage()
    {
        console.WriteError("Usage: agent-sync <init|join|sync|pull|push|status|doctor|conflicts|resolve|agent> [options] [--manage]");
        return 2;
    }

    private int Help()
    {
        console.WriteLine("Usage: agent-sync <init|join|sync|pull|push|status|doctor|conflicts|resolve|agent> [options] [--manage]");
        console.WriteLine("doctor [--compatibility-session <jsonl> --codex-exe <path>]");
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

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return new string(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' ? character : '_').ToArray());
    }

    private ICliServices Services => services ?? throw new InvalidOperationException("CLI services are unavailable.");
}
