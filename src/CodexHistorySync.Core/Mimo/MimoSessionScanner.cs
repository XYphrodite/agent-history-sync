using System.Diagnostics;
using System.Security.Cryptography;
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

        var stamp0 = CaptureDbStamp(paths);
        ScanPass pass1;
        try
        {
            pass1 = RunScanPass(paths, cancellationToken);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.MimoSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }
        var stamp1 = CaptureDbStamp(paths);

        if (pass1.Fingerprints.Count != 0)
            await waitForStability(cancellationToken).ConfigureAwait(false);

        var stamp2 = CaptureDbStamp(paths);
        ScanPass pass2;
        if (stamp0.CanShortCircuit && stamp0.Equals(stamp1) && stamp1.Equals(stamp2))
        {
            pass2 = pass1;
        }
        else
        {
            try
            {
                pass2 = RunScanPass(paths, cancellationToken);
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                uncertain.Add(ObjectKind.MimoSession);
                return new SessionScanResult(objects, uncertain, duplicates);
            }
        }

        var running = isMimoRunning();
        var cutoff = now().ToUnixTimeMilliseconds() / 1000d - activityWindow.TotalSeconds;
        var complete = true;
        var ignored = new HashSet<LogicalObjectId>();
        var byId = new Dictionary<LogicalObjectId, LocalObject>();
        foreach (var row in pass2.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = MimoSessionPackage.ToLogicalId(row.SessionId);
            if (row.IsSubagent)
            {
                // A child session for an internal agent run is machine-local noise the manager
                // already hides. Ignore it rather than drop it so its absence never reads as a
                // deletion of a session another machine still holds.
                ignored.Add(new LogicalObjectId(logical));
                continue;
            }

            if (!pass1.Fingerprints.TryGetValue(logical, out var before) ||
                !pass2.Fingerprints.TryGetValue(logical, out var after) ||
                !string.Equals(before, after, StringComparison.Ordinal))
            {
                complete = false;
                continue;
            }

            if (running && row.LastActiveUnix >= cutoff)
            {
                complete = false;
                continue;
            }

            if (!string.Equals(row.Hash.Hex, after, StringComparison.Ordinal))
            {
                complete = false;
                continue;
            }

            var item = new LocalObject(
                new LogicalObjectId(logical),
                ObjectKind.MimoSession,
                paths.AnchorPath(row.SessionId),
                row.Hash,
                row.Length,
                Timestamp(row.LastActiveUnix));
            if (byId.TryAdd(item.Id, item)) objects.Add(item);
            else
            {
                duplicates.Add(item.Id);
                complete = false;
                if (byId.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (pass1.Fingerprints.Count != pass2.Fingerprints.Count ||
            pass1.Fingerprints.Keys.Any(id => !pass2.Fingerprints.ContainsKey(id)))
        {
            complete = false;
        }
        if (!complete) uncertain.Add(ObjectKind.MimoSession);
        return new SessionScanResult(objects, uncertain, duplicates) { IgnoredIds = ignored };
    }

    private static ScanPass RunScanPass(MimoPaths paths, CancellationToken cancellationToken)
    {
        var rows = new List<MimoScannedSession>();
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var complete = MimoSessionDatabase.ScanSessions(paths, row =>
        {
            rows.Add(row);
            if (!row.IsSubagent)
            {
                fingerprints[MimoSessionPackage.ToLogicalId(row.SessionId)] = row.Hash.Hex;
            }
        }, cancellationToken);

        if (!complete)
            throw new InvalidDataException("MiMo state database could not be read completely.");

        return new ScanPass(rows, fingerprints);
    }

    private static DbStamp CaptureDbStamp(MimoPaths paths)
    {
        const long maxWalBytesForShortCircuit = 64L * 1024 * 1024;
        try
        {
            var dbs = paths.ListDatabasePaths();
            if (dbs.Count == 0 && File.Exists(paths.PrimaryDatabasePath))
                dbs = [paths.PrimaryDatabasePath];

            var entries = new DbFileStamp[dbs.Count];
            Span<byte> header = stackalloc byte[100];
            for (var i = 0; i < dbs.Count; i++)
            {
                var db = dbs[i];
                if (!File.Exists(db))
                    return DbStamp.Fallback;
                if (File.Exists(db + "-journal"))
                    return DbStamp.Fallback;

                var dbInfo = new FileInfo(db);
                string headerHash;
                using (var dbStream = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var read = dbStream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                    headerHash = Convert.ToHexStringLower(SHA256.HashData(header[..read]));
                }

                var walPath = db + "-wal";
                var walExists = File.Exists(walPath);
                long walLength = 0;
                var walHash = string.Empty;
                if (walExists)
                {
                    var walInfo = new FileInfo(walPath);
                    walLength = walInfo.Length;
                    if (walLength > maxWalBytesForShortCircuit)
                        return DbStamp.Fallback;
                    using var walStream = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    walHash = Convert.ToHexStringLower(SHA256.HashData(walStream));
                }

                entries[i] = new DbFileStamp(
                    db,
                    dbInfo.Length,
                    dbInfo.LastWriteTimeUtc.Ticks,
                    headerHash,
                    walExists,
                    walLength,
                    walHash);
            }

            return new DbStamp(true, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DbStamp.Fallback;
        }
    }

    private sealed record ScanPass(
        List<MimoScannedSession> Rows,
        Dictionary<string, string> Fingerprints);

    private readonly record struct DbFileStamp(
        string Path,
        long Length,
        long LastWriteTicks,
        string HeaderHash,
        bool WalExists,
        long WalLength,
        string WalHash);

    private readonly struct DbStamp(bool canShortCircuit, DbFileStamp[] entries) : IEquatable<DbStamp>
    {
        public static DbStamp Fallback => new(false, []);
        public bool CanShortCircuit { get; } = canShortCircuit;
        private DbFileStamp[] Entries { get; } = entries;

        public bool Equals(DbStamp other)
        {
            if (!CanShortCircuit || !other.CanShortCircuit) return false;
            return Entries.AsSpan().SequenceEqual(other.Entries);
        }

        public override bool Equals(object? obj) => obj is DbStamp other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(CanShortCircuit, Entries?.Length ?? 0);
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
