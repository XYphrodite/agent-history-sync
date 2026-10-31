using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Antigravity;

/// <summary>
/// Discovers local Google Antigravity sessions with two-phase stability check, active lock detection,
/// process detection, and clean absence confirmation.
/// </summary>
public sealed class AntigravitySessionScanner
{
    private const string AntigravityProcessName = "antigravity";
    private const string AntigravityCliProcessName = "antigravity-cli";

    public static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task> waitForStability;
    private readonly Func<bool> isAntigravityRunning;
    private readonly Func<string, bool> isExclusiveLockHeld;
    private readonly TimeSpan activityWindow;
    private readonly Func<DateTimeOffset> now;

    public AntigravitySessionScanner() : this(TimeSpan.FromMilliseconds(50)) { }

    public AntigravitySessionScanner(TimeSpan stabilityDelay) : this(stabilityDelay, DefaultActivityWindow) { }

    public AntigravitySessionScanner(TimeSpan stabilityDelay, TimeSpan activityWindow)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        if (activityWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activityWindow));
        waitForStability = ct => Task.Delay(stabilityDelay, ct);
        isAntigravityRunning = IsAntigravityProcessRunning;
        isExclusiveLockHeld = IsFileExclusivelyLocked;
        this.activityWindow = activityWindow;
        now = () => DateTimeOffset.UtcNow;
    }

    internal AntigravitySessionScanner(
        Func<CancellationToken, Task> waitForStability,
        Func<bool>? isAntigravityRunning = null,
        TimeSpan? activityWindow = null,
        Func<DateTimeOffset>? now = null,
        Func<string, bool>? isExclusiveLockHeld = null)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
        this.isAntigravityRunning = isAntigravityRunning ?? IsAntigravityProcessRunning;
        this.activityWindow = activityWindow ?? DefaultActivityWindow;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.isExclusiveLockHeld = isExclusiveLockHeld ?? IsFileExclusivelyLocked;
    }

    public async Task<SessionScanResult> ScanDetailedAsync(AntigravityPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        if (!Directory.Exists(paths.Home))
        {
            uncertain.Add(ObjectKind.AntigravitySession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var convDirExists = Directory.Exists(paths.ConversationsDirectory);
        var dbFiles = convDirExists ? Directory.GetFiles(paths.ConversationsDirectory, "*.db") : Array.Empty<string>();
        var summariesDbExists = File.Exists(paths.ConversationSummariesDbPath);

        if (!summariesDbExists && dbFiles.Length == 0)
        {
            uncertain.Add(ObjectKind.AntigravitySession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        Dictionary<string, AntigravitySummaryRow> summariesById;
        try
        {
            summariesById = summariesDbExists
                ? AntigravityDatabase.ReadSummaries(paths.ConversationSummariesDbPath)
                    .Where(s => AntigravityPaths.IsSessionId(s.ConversationId))
                    .ToDictionary(s => s.ConversationId.ToLowerInvariant(), StringComparer.Ordinal)
                : new Dictionary<string, AntigravitySummaryRow>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.AntigravitySession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var allSessionIds = DiscoverSessionIds(paths, summariesById);
        if (allSessionIds.Count == 0 && summariesDbExists)
        {
            // Empty database with 0 rows confirmed: clean absence
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        Dictionary<string, string>? first;
        try
        {
            first = Fingerprints(paths, allSessionIds);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.AntigravitySession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        if (first.Count != 0)
            await waitForStability(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> second;
        try
        {
            second = Fingerprints(paths, allSessionIds);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.AntigravitySession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var running = isAntigravityRunning();
        var cutoff = now().ToUnixTimeMilliseconds() / 1000d - activityWindow.TotalSeconds;
        var complete = true;

        var byId = new Dictionary<LogicalObjectId, LocalObject>();

        foreach (var sessionId in allSessionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = AntigravitySessionPackage.ToLogicalId(sessionId);

            if (IsSessionLocked(paths, sessionId))
            {
                complete = false;
                continue;
            }

            summariesById.TryGetValue(sessionId, out var summary);
            var lastActiveUnix = summary is not null
                ? summary.UpdatedAt.ToUnixTimeSeconds()
                : (File.Exists(paths.ConversationDatabasePath(sessionId))
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(paths.ConversationDatabasePath(sessionId))).ToUnixTimeSeconds()
                    : 0);

            if (running && lastActiveUnix >= cutoff)
            {
                complete = false;
                continue;
            }

            if (!first.TryGetValue(logical, out var before) ||
                !second.TryGetValue(logical, out var after) ||
                !string.Equals(before, after, StringComparison.Ordinal))
            {
                complete = false;
                continue;
            }

            byte[] package;
            try
            {
                package = AntigravitySessionPackage.TryBuild(paths, sessionId)
                    ?? throw new InvalidDataException("Antigravity session disappeared while it was being read.");
            }
            catch (Exception exception) when (IsReadFailure(exception))
            {
                complete = false;
                continue;
            }

            var pkgHash = AntigravitySessionPackage.HashPackage(package);
            if (!string.Equals(pkgHash.Hex, after, StringComparison.Ordinal))
            {
                complete = false;
                continue;
            }

            var timestamp = summary?.UpdatedAt
                ?? (File.Exists(paths.ConversationDatabasePath(sessionId))
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(paths.ConversationDatabasePath(sessionId)))
                    : DateTimeOffset.UtcNow);

            var item = new LocalObject(
                new LogicalObjectId(logical),
                ObjectKind.AntigravitySession,
                paths.AnchorPath(sessionId),
                pkgHash,
                package.LongLength,
                timestamp);

            if (byId.TryAdd(item.Id, item)) objects.Add(item);
            else
            {
                duplicates.Add(item.Id);
                complete = false;
                if (byId.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (first.Count != second.Count || first.Keys.Any(id => !second.ContainsKey(id))) complete = false;
        if (!complete) uncertain.Add(ObjectKind.AntigravitySession);
        return new SessionScanResult(objects, uncertain, duplicates);
    }

    private static HashSet<string> DiscoverSessionIds(AntigravityPaths paths, Dictionary<string, AntigravitySummaryRow> summaries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in summaries.Keys)
        {
            if (AntigravityPaths.IsSessionId(key))
                ids.Add(key);
        }

        if (Directory.Exists(paths.ConversationsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(paths.ConversationsDirectory, "*.db"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (AntigravityPaths.IsSessionId(name))
                    ids.Add(name.ToLowerInvariant());
            }
        }

        return ids;
    }

    private static Dictionary<string, string> Fingerprints(AntigravityPaths paths, HashSet<string> sessionIds)
    {
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sessionId in sessionIds)
        {
            var package = AntigravitySessionPackage.TryBuild(paths, sessionId);
            if (package is not null)
            {
                fingerprints[AntigravitySessionPackage.ToLogicalId(sessionId)] =
                    AntigravitySessionPackage.HashPackage(package).Hex;
            }
        }

        return fingerprints;
    }

    private bool IsSessionLocked(AntigravityPaths paths, string sessionId)
    {
        var presencePath = Path.Combine(paths.Home, "presence", sessionId + ".lock");
        if (File.Exists(presencePath) && isExclusiveLockHeld(presencePath))
            return true;

        var walPath = Path.Combine(paths.ConversationsDirectory, sessionId + ".db-wal");
        if (File.Exists(walPath) && isExclusiveLockHeld(walPath))
            return true;

        var dbPath = paths.ConversationDatabasePath(sessionId);
        if (File.Exists(dbPath) && isExclusiveLockHeld(dbPath))
            return true;

        return false;
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or
            SqliteException or DecoderFallbackException or ArgumentException;

    private static bool IsAntigravityProcessRunning()
    {
        try
        {
            var p1 = Process.GetProcessesByName(AntigravityProcessName);
            try { if (p1.Length != 0) return true; }
            finally { foreach (var process in p1) process.Dispose(); }

            var p2 = Process.GetProcessesByName(AntigravityCliProcessName);
            try { return p2.Length != 0; }
            finally { foreach (var process in p2) process.Dispose(); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private static bool IsFileExclusivelyLocked(string path)
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }
}
