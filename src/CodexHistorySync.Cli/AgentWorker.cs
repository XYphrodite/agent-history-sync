using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Windows;

namespace CodexHistorySync.Cli;

public interface IAgentSyncOperations
{
    Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken);
    Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken);
}

public interface IAgentClock
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemAgentClock : IAgentClock
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public enum AgentLogKind
{
    Success,
    Failure
}

public sealed class AgentLogEntry
{
    private static readonly Regex SafeRevision = new("^[A-Za-z0-9_.:-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeErrorCode = new("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    public AgentLogEntry(AgentLogKind kind, Guid operationId, SyncMode mode, int uploaded, int downloaded,
        int deleted, int conflicts, int pending, string revision, string errorCode, long elapsedMilliseconds)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (uploaded < 0 || downloaded < 0 || deleted < 0 || conflicts < 0 || pending < 0 || elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(uploaded), "Agent diagnostic counts and timings cannot be negative.");
        Kind = kind;
        OperationId = operationId;
        Mode = mode;
        Uploaded = uploaded;
        Downloaded = downloaded;
        Deleted = deleted;
        Conflicts = conflicts;
        Pending = pending;
        Revision = SanitizeRevision(revision);
        ErrorCode = SanitizeErrorCode(errorCode);
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    public AgentLogKind Kind { get; }
    public Guid OperationId { get; }
    public SyncMode Mode { get; }
    public int Uploaded { get; }
    public int Downloaded { get; }
    public int Deleted { get; }
    public int Conflicts { get; }
    public int Pending { get; }
    public string Revision { get; }
    public string ErrorCode { get; }
    public long ElapsedMilliseconds { get; }

    private static string SanitizeRevision(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains("..", StringComparison.Ordinal) && SafeRevision.IsMatch(value)
            ? value : "redacted";

    private static string SanitizeErrorCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SafeErrorCode.IsMatch(value) ? value : "REDACTED";
}

public interface IAgentLogger
{
    Task WriteAsync(AgentLogEntry entry, CancellationToken cancellationToken);
}

public sealed class RotatingAgentLogger : IAgentLogger
{
    public const long DefaultMaximumBytes = 10L * 1024 * 1024;
    public const int DefaultRetainedFiles = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly long maximumBytes;
    private readonly int retainedFiles;
    private readonly SemaphoreSlim mutex = new(1, 1);

    public RotatingAgentLogger(string localAppData)
        : this(Path.Combine(Path.GetFullPath(localAppData ?? throw new ArgumentNullException(nameof(localAppData))),
            "CodexHistorySync", "logs"), DefaultMaximumBytes, DefaultRetainedFiles) { }

    public RotatingAgentLogger(string logDirectory, long maximumBytes, int retainedFiles)
    {
        directory = Path.GetFullPath(logDirectory ?? throw new ArgumentNullException(nameof(logDirectory)));
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (retainedFiles is < 1 or > DefaultRetainedFiles) throw new ArgumentOutOfRangeException(nameof(retainedFiles));
        this.maximumBytes = maximumBytes;
        this.retainedFiles = retainedFiles;
    }

    public async Task WriteAsync(AgentLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var current = LogPath(0);
            if (File.Exists(current) && new FileInfo(current).Length + payload.LongLength > maximumBytes)
                Rotate();
            await using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally { mutex.Release(); }
    }

    private void Rotate()
    {
        var oldest = LogPath(retainedFiles - 1);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = retainedFiles - 2; index >= 0; index--)
        {
            var source = LogPath(index);
            if (File.Exists(source)) File.Move(source, LogPath(index + 1), overwrite: true);
        }
    }

    private string LogPath(int index) => Path.Combine(directory, index == 0 ? "agent.log" : $"agent.{index}.log");
}

public sealed record AgentWorkerOptions(
    TimeSpan ActiveExportInterval,
    TimeSpan StoppedSyncInterval,
    TimeSpan QuiescenceInterval,
    TimeSpan InitialFailureBackoff,
    TimeSpan MaximumFailureBackoff,
    int RepeatedFailureThreshold,
    TimeSpan? QuiescencePollInterval = null)
{
    public static AgentWorkerOptions Default { get; } = new(
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), 3);
}

public sealed class AgentWorker
{
    private readonly ICodexProcessDetector detector;
    private readonly IAgentSyncOperations sync;
    private readonly IAgentClock clock;
    private readonly IAgentNotifier notifier;
    private readonly IAgentLogger logger;
    private readonly AgentWorkerOptions options;

    public AgentWorker(ICodexProcessDetector detector, IAgentSyncOperations sync, IAgentClock clock,
        IAgentNotifier notifier, IAgentLogger logger, AgentWorkerOptions? options = null)
    {
        this.detector = detector ?? throw new ArgumentNullException(nameof(detector));
        this.sync = sync ?? throw new ArgumentNullException(nameof(sync));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.options = options ?? AgentWorkerOptions.Default;
        ValidateOptions(this.options);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var repeatedFailureNotified = false;
        var deferredImportNotified = false;
        var conflictNotified = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var active = false;
            var mode = SyncMode.Bidirectional;
            var operationId = Guid.NewGuid();
            var started = Stopwatch.GetTimestamp();
            try
            {
                active = detector.IsRunning();
                mode = active ? SyncMode.Push : SyncMode.Bidirectional;
                var result = await sync.SynchronizeAsync(mode, cancellationToken).ConfigureAwait(false);
                var pending = 0;
                var conflicts = result.Conflicts;
                if (active)
                {
                    var status = await sync.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    pending = status.Pending;
                    conflicts = Math.Max(conflicts, status.Conflicts);
                    if (pending > 0 && !deferredImportNotified)
                    {
                        await TryNotifyAsync(new AgentNotification(AgentNotificationKind.PendingRestart, pending), cancellationToken)
                            .ConfigureAwait(false);
                        deferredImportNotified = true;
                    }
                }
                else
                {
                    deferredImportNotified = false;
                }

                if (conflicts > 0 && !conflictNotified)
                {
                    await TryNotifyAsync(new AgentNotification(AgentNotificationKind.UnresolvedConflict, conflicts), cancellationToken)
                        .ConfigureAwait(false);
                    conflictNotified = true;
                }
                else if (conflicts == 0)
                {
                    conflictNotified = false;
                }

                await TryLogAsync(new AgentLogEntry(AgentLogKind.Success, operationId, mode,
                    result.Uploaded, result.Downloaded, result.Deleted, conflicts, pending,
                    result.RemoteRevision, "NONE", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds), cancellationToken)
                    .ConfigureAwait(false);

                if (failures > 0)
                    await TryNotifyAsync(new AgentNotification(AgentNotificationKind.Recovered, failures), cancellationToken)
                        .ConfigureAwait(false);
                failures = 0;
                repeatedFailureNotified = false;

                if (active)
                {
                    if (await WaitForExitOrIntervalAsync(cancellationToken).ConfigureAwait(false))
                        await WaitForQuiescenceAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await clock.DelayAsync(options.StoppedSyncInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (CodexBecameActiveException)
            {
                try
                {
                    if (!deferredImportNotified)
                    {
                        await TryNotifyAsync(new AgentNotification(AgentNotificationKind.PendingRestart, 1), cancellationToken)
                            .ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        deferredImportNotified = true;
                    }
                    await detector.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    await WaitForQuiescenceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    failures++;
                    await TryLogAsync(new AgentLogEntry(AgentLogKind.Failure, operationId, mode,
                        0, 0, 0, 0, 0, string.Empty, ErrorCode(exception),
                        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (failures >= options.RepeatedFailureThreshold && !repeatedFailureNotified)
                    {
                        await TryNotifyAsync(new AgentNotification(AgentNotificationKind.RepeatedFailure, failures), cancellationToken)
                            .ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        repeatedFailureNotified = true;
                    }
                    await clock.DelayAsync(Backoff(failures), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> WaitForExitOrIntervalAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exit = detector.WaitForExitAsync(linked.Token);
        var interval = clock.DelayAsync(options.ActiveExportInterval, linked.Token);
        var completed = await Task.WhenAny(exit, interval).ConfigureAwait(false);
        linked.Cancel();
        try { await Task.WhenAll(exit, interval).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        return ReferenceEquals(completed, exit);
    }

    private TimeSpan Backoff(int failures)
    {
        var multiplier = Math.Pow(2, Math.Min(failures - 1, 30));
        var ticks = Math.Min(options.MaximumFailureBackoff.Ticks,
            options.InitialFailureBackoff.Ticks * multiplier);
        return TimeSpan.FromTicks((long)ticks);
    }

    private async Task WaitForQuiescenceAsync(CancellationToken cancellationToken)
    {
        var poll = options.QuiescencePollInterval ?? TimeSpan.FromMilliseconds(250);
        var quiet = TimeSpan.Zero;
        while (quiet < options.QuiescenceInterval)
        {
            var delay = poll < options.QuiescenceInterval - quiet ? poll : options.QuiescenceInterval - quiet;
            await clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            if (detector.IsRunning())
            {
                await detector.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                quiet = TimeSpan.Zero;
            }
            else
            {
                quiet += delay;
            }
        }
    }

    private async Task TryNotifyAsync(AgentNotification notification, CancellationToken cancellationToken)
    {
        try { await notifier.NotifyAsync(notification, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
    }

    private async Task TryLogAsync(AgentLogEntry entry, CancellationToken cancellationToken)
    {
        try { await logger.WriteAsync(entry, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
    }

    private static string ErrorCode(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "ACCESS_DENIED",
        IOException => "IO_ERROR",
        System.Security.Cryptography.CryptographicException => "AUTHENTICATION_ERROR",
        TimeoutException => "TIMEOUT",
        _ => "OPERATION_ERROR"
    };

    private static void ValidateOptions(AgentWorkerOptions options)
    {
        if (options.ActiveExportInterval <= TimeSpan.Zero || options.StoppedSyncInterval <= TimeSpan.Zero ||
            options.QuiescenceInterval <= TimeSpan.Zero || options.InitialFailureBackoff <= TimeSpan.Zero ||
            options.MaximumFailureBackoff < options.InitialFailureBackoff || options.RepeatedFailureThreshold < 1 ||
            options.QuiescencePollInterval is { } poll && poll <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
}

public sealed class CliAgentSyncOperations(ICliServices services) : IAgentSyncOperations
{
    public Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken) =>
        services.SynchronizeAsync(mode, cancellationToken);

    public Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken) =>
        services.GetStatusAsync(cancellationToken);
}
