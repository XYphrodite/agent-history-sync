using System.Diagnostics;
using System.Security.Cryptography;
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

        Dictionary<string, AntigravitySummaryRow> summariesPhase1;
        try
        {
            summariesPhase1 = ReadSummariesById(paths);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.AntigravitySession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var idsPhase1 = DiscoverSessionIds(paths, summariesPhase1);
        if (idsPhase1.Count == 0 && summariesDbExists)
        {
            // Empty database with 0 rows confirmed: clean absence
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var first = new Dictionary<string, (ContentHash Hash, long Length, SessionStamp Stamp0, SessionStamp Stamp1, AntigravitySummaryRow? Summary)>(StringComparer.Ordinal);
        var lockedPhase1 = false;
        try
        {
            foreach (var sessionId in idsPhase1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSessionLocked(paths, sessionId))
                {
                    lockedPhase1 = true;
                    continue;
                }

                summariesPhase1.TryGetValue(sessionId, out var summaryPhase1);
                var stamp0 = CaptureSessionStamp(paths, sessionId);
                var measured = AntigravitySessionPackage.TryHashAndMeasure(
                    paths, sessionId, usePreloadedSummary: true, preloadedSummary: summaryPhase1);
                if (measured is not null)
                {
                    var stamp1 = CaptureSessionStamp(paths, sessionId);
                    first[AntigravitySessionPackage.ToLogicalId(sessionId)] =
                        (measured.Value.Hash, measured.Value.Length, stamp0, stamp1, summaryPhase1);
                }
            }
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.AntigravitySession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        if (first.Count != 0 || lockedPhase1)
            await waitForStability(cancellationToken).ConfigureAwait(false);

        Dictionary<string, AntigravitySummaryRow> summariesPhase2;
        try
        {
            summariesPhase2 = ReadSummariesById(paths);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            uncertain.Add(ObjectKind.AntigravitySession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var idsPhase2 = DiscoverSessionIds(paths, summariesPhase2);
        var running = isAntigravityRunning();
        var cutoff = now().ToUnixTimeMilliseconds() / 1000d - activityWindow.TotalSeconds;
        var complete = !lockedPhase1;

        var byId = new Dictionary<LogicalObjectId, LocalObject>();
        var stableLogicalIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sessionId in idsPhase2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = AntigravitySessionPackage.ToLogicalId(sessionId);

            if (IsSessionLocked(paths, sessionId))
            {
                complete = false;
                continue;
            }

            summariesPhase2.TryGetValue(sessionId, out var summary);
            var convDbPath = paths.ConversationDatabasePath(sessionId);
            var convDbExists = File.Exists(convDbPath);
            var lastActiveUnix = summary is not null
                ? summary.UpdatedAt.ToUnixTimeSeconds()
                : (convDbExists
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(convDbPath)).ToUnixTimeSeconds()
                    : 0);

            if (running && lastActiveUnix >= cutoff)
            {
                complete = false;
                continue;
            }

            if (!first.TryGetValue(logical, out var before))
            {
                complete = false;
                continue;
            }

            (ContentHash Hash, long Length)? after;
            var stamp2 = CaptureSessionStamp(paths, sessionId);
            if (before.Stamp0.CanShortCircuit &&
                before.Stamp0.Equals(before.Stamp1) &&
                before.Stamp1.Equals(stamp2) &&
                SummariesEqual(before.Summary, summary))
            {
                after = (before.Hash, before.Length);
            }
            else
            {
                after = AntigravitySessionPackage.TryHashAndMeasure(
                    paths, sessionId, usePreloadedSummary: true, preloadedSummary: summary);
            }

            if (after is null ||
                !string.Equals(before.Hash.Hex, after.Value.Hash.Hex, StringComparison.Ordinal) ||
                before.Length != after.Value.Length)
            {
                complete = false;
                continue;
            }

            stableLogicalIds.Add(logical);
            var timestamp = summary?.UpdatedAt
                ?? (convDbExists
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(convDbPath))
                    : DateTimeOffset.UtcNow);

            var item = new LocalObject(
                new LogicalObjectId(logical),
                ObjectKind.AntigravitySession,
                paths.AnchorPath(sessionId),
                after.Value.Hash,
                after.Value.Length,
                timestamp);

            if (byId.TryAdd(item.Id, item)) objects.Add(item);
            else
            {
                duplicates.Add(item.Id);
                complete = false;
                if (byId.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (first.Keys.Any(id => !stableLogicalIds.Contains(id))) complete = false;
        if (!complete) uncertain.Add(ObjectKind.AntigravitySession);
        return new SessionScanResult(objects, uncertain, duplicates);
    }

    private static bool SummariesEqual(AntigravitySummaryRow? a, AntigravitySummaryRow? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.ConversationId, b.ConversationId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.ParentConversationId, b.ParentConversationId, StringComparison.Ordinal) &&
               a.NestingDepth == b.NestingDepth &&
               string.Equals(a.AgentName, b.AgentName, StringComparison.Ordinal) &&
               string.Equals(a.Title, b.Title, StringComparison.Ordinal) &&
               string.Equals(a.Status, b.Status, StringComparison.Ordinal) &&
               a.CreatedAt.ToUnixTimeSeconds() == b.CreatedAt.ToUnixTimeSeconds() &&
               a.UpdatedAt.ToUnixTimeSeconds() == b.UpdatedAt.ToUnixTimeSeconds() &&
               (a.WorkspaceUris ?? Array.Empty<string>()).SequenceEqual(b.WorkspaceUris ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    private static SessionStamp CaptureSessionStamp(AntigravityPaths paths, string sessionId)
    {
        try
        {
            var convDb = paths.ConversationDatabasePath(sessionId);
            if (File.Exists(convDb + "-wal") || File.Exists(convDb + "-journal"))
                return SessionStamp.Fallback;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> scratch = stackalloc byte[100];

            void AppendInt64(long v)
            {
                Span<byte> buf = stackalloc byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, v);
                hasher.AppendData(buf);
            }

            if (File.Exists(convDb))
            {
                var info = new FileInfo(convDb);
                AppendInt64(1);
                AppendInt64(info.Length);
                AppendInt64(info.LastWriteTimeUtc.Ticks);
                using var stream = new FileStream(convDb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var read = stream.ReadAtLeast(scratch, scratch.Length, throwOnEndOfStream: false);
                hasher.AppendData(scratch[..read]);
            }
            else
            {
                AppendInt64(0);
            }

            var annotPath = paths.AnnotationPath(sessionId);
            if (File.Exists(annotPath))
            {
                var info = new FileInfo(annotPath);
                AppendInt64(1);
                AppendInt64(info.Length);
                AppendInt64(info.LastWriteTimeUtc.Ticks);
            }
            else
            {
                AppendInt64(0);
            }

            var brainDir = paths.BrainSessionDirectory(sessionId);
            if (Directory.Exists(brainDir))
            {
                var files = Directory.GetFiles(brainDir, "*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                AppendInt64(files.Length);
                foreach (var file in files)
                {
                    hasher.AppendData(Encoding.UTF8.GetBytes(file));
                    var info = new FileInfo(file);
                    AppendInt64(info.Length);
                    AppendInt64(info.LastWriteTimeUtc.Ticks);
                }
            }
            else
            {
                AppendInt64(-1);
            }

            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            hasher.GetHashAndReset(digest);
            return new SessionStamp(true, Convert.ToHexStringLower(digest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return SessionStamp.Fallback;
        }
    }

    private readonly record struct SessionStamp(bool CanShortCircuit, string Digest)
    {
        public static SessionStamp Fallback => new(false, string.Empty);
    }

    private static Dictionary<string, AntigravitySummaryRow> ReadSummariesById(AntigravityPaths paths)
    {
        if (!File.Exists(paths.ConversationSummariesDbPath))
            return new Dictionary<string, AntigravitySummaryRow>(StringComparer.Ordinal);

        var dict = new Dictionary<string, AntigravitySummaryRow>(StringComparer.Ordinal);
        foreach (var row in AntigravityDatabase.ReadSummaries(paths.ConversationSummariesDbPath))
        {
            if (AntigravityPaths.IsSessionId(row.ConversationId))
            {
                dict[row.ConversationId.ToLowerInvariant()] = row;
            }
        }
        return dict;
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
