using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Hermes;

public sealed class HermesSessionScanner
{
    private const string HermesProcessName = "hermes";

    /// <summary>
    /// Hermes publishes no per-session lock file. A session counts as live when a hermes process
    /// is running and its newest message (or start time) falls inside this window.
    /// </summary>
    public static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task> waitForStability;
    private readonly Func<bool> isHermesRunning;
    private readonly TimeSpan activityWindow;
    private readonly Func<DateTimeOffset> now;

    public HermesSessionScanner() : this(TimeSpan.FromMilliseconds(50)) { }

    public HermesSessionScanner(TimeSpan stabilityDelay) : this(stabilityDelay, DefaultActivityWindow) { }

    public HermesSessionScanner(TimeSpan stabilityDelay, TimeSpan activityWindow)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        if (activityWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activityWindow));
        waitForStability = ct => Task.Delay(stabilityDelay, ct);
        isHermesRunning = IsHermesProcessRunning;
        this.activityWindow = activityWindow;
        now = () => DateTimeOffset.UtcNow;
    }

    internal HermesSessionScanner(
        Func<CancellationToken, Task> waitForStability,
        Func<bool>? isHermesRunning = null,
        TimeSpan? activityWindow = null,
        Func<DateTimeOffset>? now = null)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
        this.isHermesRunning = isHermesRunning ?? IsHermesProcessRunning;
        this.activityWindow = activityWindow ?? DefaultActivityWindow;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SessionScanResult> ScanDetailedAsync(HermesPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        // A home with no state.db has not been initialized. That is not proof the user deleted
        // every session, so absence must not become a tombstone.
        if (paths.ListProfiles().Count == 0)
        {
            uncertain.Add(ObjectKind.HermesSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        Dictionary<string, string>? first;
        try
        {
            first = Fingerprints(paths);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.HermesSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        if (first.Count != 0)
            await waitForStability(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> second;
        try
        {
            second = Fingerprints(paths);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.HermesSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var running = isHermesRunning();
        var cutoff = now().ToUnixTimeMilliseconds() / 1000d - activityWindow.TotalSeconds;
        var complete = true;
        HermesReadResult read;
        try
        {
            read = HermesSessionDatabase.ReadProfiles(paths);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.HermesSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        if (!read.Complete) complete = false;
        var byId = new Dictionary<LogicalObjectId, LocalObject>();
        foreach (var snapshot in read.Snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = HermesSessionPackage.ToLogicalId(snapshot.Profile, snapshot.SessionId);
            if (!first.TryGetValue(logical, out var before) ||
                !second.TryGetValue(logical, out var after) ||
                !string.Equals(before, after, StringComparison.Ordinal))
            {
                complete = false;
                continue;
            }

            if (running && snapshot.LastActiveUnix >= cutoff)
            {
                complete = false;
                continue;
            }

            byte[] package;
            try
            {
                package = HermesSessionPackage.TryBuild(paths, snapshot.Profile, snapshot.SessionId)
                    ?? throw new InvalidDataException("Hermes session disappeared while it was being read.");
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                complete = false;
                continue;
            }

            if (!string.Equals(HermesSessionPackage.HashPackage(package).Hex, after, StringComparison.Ordinal))
            {
                complete = false;
                continue;
            }

            var item = new LocalObject(
                new LogicalObjectId(logical),
                ObjectKind.HermesSession,
                paths.AnchorPath(snapshot.Profile, snapshot.SessionId),
                HermesSessionPackage.HashPackage(package),
                package.LongLength,
                Timestamp(snapshot.LastActiveUnix));
            if (byId.TryAdd(item.Id, item)) objects.Add(item);
            else
            {
                duplicates.Add(item.Id);
                complete = false;
                if (byId.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (first.Count != second.Count || first.Keys.Any(id => !second.ContainsKey(id))) complete = false;
        if (!complete) uncertain.Add(ObjectKind.HermesSession);
        return new SessionScanResult(objects, uncertain, duplicates);
    }

    private static Dictionary<string, string> Fingerprints(HermesPaths paths)
    {
        var read = HermesSessionDatabase.ReadProfiles(paths);
        if (!read.Complete) throw new InvalidDataException("Hermes state database could not be read completely.");
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in read.Snapshots)
        {
            var package = HermesSessionPackage.TryBuild(paths, snapshot.Profile, snapshot.SessionId)
                ?? throw new InvalidDataException("Hermes session disappeared while it was being read.");
            fingerprints[HermesSessionPackage.ToLogicalId(snapshot.Profile, snapshot.SessionId)] =
                HermesSessionPackage.HashPackage(package).Hex;
        }

        return fingerprints;
    }

    private static DateTimeOffset Timestamp(double unixSeconds)
    {
        try
        {
            var millis = (long)Math.Round(unixSeconds * 1000d, MidpointRounding.AwayFromZero);
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            SqliteException or DecoderFallbackException or ArgumentException;

    private static bool IsHermesProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(HermesProcessName);
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }
}
