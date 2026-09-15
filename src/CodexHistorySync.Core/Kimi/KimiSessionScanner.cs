using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Kimi;

public sealed class KimiSessionScanner
{
    private const string KimiProcessName = "kimi";

    /// <summary>
    /// How recently a session file must have been written to count as belonging to a live session.
    /// Kimi Code publishes no active-session file, so liveness is inferred from a running process
    /// plus this recency window, exactly like Claude (design D3): an idle but open session stops
    /// deferring once it goes quiet, and the two-observation stability read still rejects a session
    /// that grows while it is being scanned.
    /// </summary>
    public static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task> waitForStability;
    private readonly Func<bool> isKimiRunning;
    private readonly TimeSpan activityWindow;
    private readonly Func<DateTimeOffset> now;

    public KimiSessionScanner() : this(TimeSpan.FromMilliseconds(50)) { }

    public KimiSessionScanner(TimeSpan stabilityDelay)
        : this(stabilityDelay, DefaultActivityWindow) { }

    public KimiSessionScanner(TimeSpan stabilityDelay, TimeSpan activityWindow)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        if (activityWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activityWindow));
        waitForStability = ct => Task.Delay(stabilityDelay, ct);
        isKimiRunning = IsKimiProcessRunning;
        this.activityWindow = activityWindow;
        now = () => DateTimeOffset.UtcNow;
    }

    internal KimiSessionScanner(
        Func<CancellationToken, Task> waitForStability,
        Func<bool>? isKimiRunning = null,
        TimeSpan? activityWindow = null,
        Func<DateTimeOffset>? now = null)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
        this.isKimiRunning = isKimiRunning ?? IsKimiProcessRunning;
        this.activityWindow = activityWindow ?? DefaultActivityWindow;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SessionScanResult> ScanDetailedAsync(KimiPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var objectsById = new Dictionary<LogicalObjectId, LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        if (!Directory.Exists(paths.Sessions))
        {
            uncertain.Add(ObjectKind.KimiSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        List<(string Directory, string WorkDirKey)> sessionDirectories;
        try
        {
            sessionDirectories = EnumerateSessionDirectories(paths.Sessions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            uncertain.Add(ObjectKind.KimiSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        // Probed once per scan, not per session: enumerating processes is the expensive half.
        var kimiRunning = isKimiRunning();
        var activeSince = now() - activityWindow;

        var complete = true;
        var candidates = new List<ObservedCandidate>();
        foreach (var (sessionDirectory, workDirKey) in sessionDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(sessionDirectory, paths.Sessions)) continue;

            PackageObservation? observation;
            try
            {
                observation = Observe(sessionDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            // Defer instead of failing, exactly like a live Grok or Claude session: a directory
            // with nothing synchronizable yet (no state.json or no wire) or one being written
            // right now is retried on a later run.
            if (observation is null) { complete = false; continue; }
            if (kimiRunning && observation.LastWriteTimeUtc >= activeSince.UtcDateTime) { complete = false; continue; }

            candidates.Add(new ObservedCandidate(sessionDirectory, workDirKey, observation));
        }

        if (candidates.Count != 0)
            await waitForStability(cancellationToken).ConfigureAwait(false);

        var results = new LocalObject?[candidates.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
        }, async (index, ct) =>
        {
            results[index] = await Task.Run(() => ReadStable(candidates[index], ct), ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = results[index];
            if (item is null) { complete = false; continue; }
            if (objectsById.TryAdd(item.Id, item)) objects.Add(item);
            else
            {
                duplicates.Add(item.Id);
                uncertain.Add(ObjectKind.KimiSession);
                if (objectsById.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (!complete) uncertain.Add(ObjectKind.KimiSession);
        return new SessionScanResult(objects, uncertain, duplicates);
    }

    private static LocalObject? ReadStable(ObservedCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var second = Observe(candidate.SessionDirectory);
            if (second is null || candidate.First.Key != second.Key) return null;

            var package = KimiSessionPackage.BuildFromDirectory(candidate.SessionDirectory, candidate.WorkDirKey);
            var hash = KimiSessionPackage.HashPackage(package);
            var sessionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate.SessionDirectory));
            var sessionId = sessionName[KimiPaths.SessionIdPrefix.Length..];
            var logicalId = new LogicalObjectId(KimiSessionPackage.ToLogicalId(sessionId));

            return new LocalObject(
                logicalId,
                ObjectKind.KimiSession,
                Path.GetFullPath(KimiSessionPackage.StateFilePath(candidate.SessionDirectory)),
                hash,
                package.LongLength,
                new DateTimeOffset(second.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or JsonException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Captures the size and write time of every synchronizable file. Returns null when the
    /// directory holds nothing synchronizable yet, so the session is deferred rather than published
    /// empty. The key is the observation itself: any change to any file between the two reads
    /// rejects the candidate.
    /// </summary>
    private static PackageObservation? Observe(string sessionDirectory)
    {
        var files = KimiSessionPackage.ListSynchronizableFiles(sessionDirectory);
        if (files is null) return null;

        var key = new StringBuilder();
        var lastWrite = DateTime.MinValue;
        foreach (var relativePath in files)
        {
            var info = new FileInfo(Path.Combine(sessionDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            info.Refresh();
            key.Append(relativePath).Append(':').Append(info.Length).Append(':')
                .Append(info.LastWriteTimeUtc.Ticks).Append(';');
            if (info.LastWriteTimeUtc > lastWrite) lastWrite = info.LastWriteTimeUtc;
        }

        return new PackageObservation(key.ToString(), lastWrite);
    }

    private static List<(string Directory, string WorkDirKey)> EnumerateSessionDirectories(string sessionsRoot)
    {
        var result = new List<(string Directory, string WorkDirKey)>();
        foreach (var workDirDirectory in Directory.EnumerateDirectories(sessionsRoot))
        {
            if (File.GetAttributes(workDirDirectory).HasFlag(FileAttributes.ReparsePoint)) continue;
            var workDirKey = Path.GetFileName(Path.TrimEndingDirectorySeparator(workDirDirectory));
            if (!workDirKey.StartsWith("wd_", StringComparison.Ordinal)) continue;
            foreach (var sessionDirectory in Directory.EnumerateDirectories(workDirDirectory))
            {
                if (File.GetAttributes(sessionDirectory).HasFlag(FileAttributes.ReparsePoint)) continue;
                var sessionName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDirectory));
                if (!sessionName.StartsWith(KimiPaths.SessionIdPrefix, StringComparison.Ordinal)) continue;
                result.Add((sessionDirectory, workDirKey));
            }
        }

        return result
            .OrderBy(entry => entry.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsKimiProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(KimiProcessName);
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Fail closed: an unreadable process list defers recent sessions rather than
            // publishing files that a live session may still be appending to.
            return true;
        }
    }

    private sealed record PackageObservation(string Key, DateTime LastWriteTimeUtc);

    private readonly record struct ObservedCandidate(
        string SessionDirectory,
        string WorkDirKey,
        PackageObservation First);
}
