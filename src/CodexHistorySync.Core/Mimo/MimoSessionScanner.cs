using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Mimo;

public sealed class MimoSessionScanner
{
    private const string MimoProcessName = "mimo";
    private const string MimoCodeProcessName = "mimocode";

    public static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task> waitForStability;
    private readonly Func<bool> isMimoRunning;
    private readonly TimeSpan activityWindow;
    private readonly Func<DateTimeOffset> now;

    public MimoSessionScanner() : this(TimeSpan.FromMilliseconds(50)) { }

    public MimoSessionScanner(TimeSpan stabilityDelay) : this(stabilityDelay, DefaultActivityWindow) { }

    public MimoSessionScanner(TimeSpan stabilityDelay, TimeSpan activityWindow)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        if (activityWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activityWindow));
        waitForStability = ct => Task.Delay(stabilityDelay, ct);
        isMimoRunning = IsMimoProcessRunning;
        this.activityWindow = activityWindow;
        now = () => DateTimeOffset.UtcNow;
    }

    internal MimoSessionScanner(
        Func<CancellationToken, Task> waitForStability,
        Func<bool>? isMimoRunning = null,
        TimeSpan? activityWindow = null,
        Func<DateTimeOffset>? now = null)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
        this.isMimoRunning = isMimoRunning ?? IsMimoProcessRunning;
        this.activityWindow = activityWindow ?? DefaultActivityWindow;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SessionScanResult> ScanDetailedAsync(MimoPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        // No database files at all – absence is not evidence of deletion
        if (paths.ListDatabasePaths().Count == 0 && !File.Exists(paths.PrimaryDatabasePath))
        {
            // Check if home exists; if not, treat as not installed (uncertain)
            if (!Directory.Exists(paths.Home))
            {
                uncertain.Add(ObjectKind.MimoSession);
                return new SessionScanResult(objects, uncertain, duplicates);
            }
            // Home exists but no DB – same as empty (not tombstone)
            uncertain.Add(ObjectKind.MimoSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        Dictionary<string, string>? first;
        try
        {
            first = Fingerprints(paths);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.MimoSession);
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
            uncertain.Add(ObjectKind.MimoSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var running = isMimoRunning();
        var cutoff = now().ToUnixTimeMilliseconds() / 1000d - activityWindow.TotalSeconds;
        var complete = true;
        var ignored = new HashSet<LogicalObjectId>();
        MimoReadResult read;
        try
        {
            read = MimoSessionDatabase.ReadAll(paths);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.MimoSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        if (!read.Complete) complete = false;
        var byId = new Dictionary<LogicalObjectId, LocalObject>();
        foreach (var snapshot in read.Snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = MimoSessionPackage.ToLogicalId(snapshot.SessionId);
            if (snapshot.IsSubagent)
            {
                // A child session for an internal agent run is machine-local noise the manager
                // already hides. Ignore it rather than drop it so its absence never reads as a
                // deletion of a session another machine still holds.
                ignored.Add(new LogicalObjectId(logical));
                continue;
            }

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
                package = MimoSessionPackage.TryBuild(paths, snapshot.SessionId)
                    ?? throw new InvalidDataException("MiMo session disappeared while it was being read.");
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                complete = false;
                continue;
            }

            if (!string.Equals(MimoSessionPackage.HashPackage(package).Hex, after, StringComparison.Ordinal))
            {
                complete = false;
                continue;
            }

            var item = new LocalObject(
                new LogicalObjectId(logical),
                ObjectKind.MimoSession,
                paths.AnchorPath(snapshot.SessionId),
                MimoSessionPackage.HashPackage(package),
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
        if (!complete) uncertain.Add(ObjectKind.MimoSession);
        return new SessionScanResult(objects, uncertain, duplicates) { IgnoredIds = ignored };
    }

    private static Dictionary<string, string> Fingerprints(MimoPaths paths)
    {
        var read = MimoSessionDatabase.ReadAll(paths);
        if (!read.Complete) throw new InvalidDataException("MiMo state database could not be read completely.");
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in read.Snapshots)
        {
            if (snapshot.IsSubagent) continue;
            var package = MimoSessionPackage.TryBuild(paths, snapshot.SessionId)
                ?? throw new InvalidDataException("MiMo session disappeared while it was being read.");
            fingerprints[MimoSessionPackage.ToLogicalId(snapshot.SessionId)] =
                MimoSessionPackage.HashPackage(package).Hex;
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

    private static bool IsMimoProcessRunning()
    {
        try
        {
            var p1 = Process.GetProcessesByName(MimoProcessName);
            try { if (p1.Length != 0) return true; }
            finally { foreach (var process in p1) process.Dispose(); }
            var p2 = Process.GetProcessesByName(MimoCodeProcessName);
            try { return p2.Length != 0; }
            finally { foreach (var process in p2) process.Dispose(); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }
}
