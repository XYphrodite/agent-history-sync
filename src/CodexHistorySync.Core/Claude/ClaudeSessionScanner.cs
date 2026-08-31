using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Claude;

public sealed class ClaudeSessionScanner
{
    private const string ClaudeProcessName = "claude";

    /// <summary>
    /// How recently a transcript must have been written to count as belonging to a live session.
    /// Claude publishes no active-session file, so liveness is inferred from a running process plus
    /// this recency window (design D3). It is deliberately wider than the stability delay: an idle
    /// but open session stops deferring once it goes quiet, and the two-observation stability read
    /// still rejects a transcript that grows while it is being scanned.
    /// </summary>
    public static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task> waitForStability;
    private readonly Func<bool> isClaudeRunning;
    private readonly TimeSpan activityWindow;
    private readonly Func<DateTimeOffset> now;

    public ClaudeSessionScanner() : this(TimeSpan.FromMilliseconds(50)) { }

    public ClaudeSessionScanner(TimeSpan stabilityDelay)
        : this(stabilityDelay, DefaultActivityWindow) { }

    public ClaudeSessionScanner(TimeSpan stabilityDelay, TimeSpan activityWindow)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        if (activityWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activityWindow));
        waitForStability = ct => Task.Delay(stabilityDelay, ct);
        isClaudeRunning = IsClaudeProcessRunning;
        this.activityWindow = activityWindow;
        now = () => DateTimeOffset.UtcNow;
    }

    internal ClaudeSessionScanner(
        Func<CancellationToken, Task> waitForStability,
        Func<bool>? isClaudeRunning = null,
        TimeSpan? activityWindow = null,
        Func<DateTimeOffset>? now = null)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
        this.isClaudeRunning = isClaudeRunning ?? IsClaudeProcessRunning;
        this.activityWindow = activityWindow ?? DefaultActivityWindow;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SessionScanResult> ScanDetailedAsync(ClaudePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var objectsById = new Dictionary<LogicalObjectId, LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        if (!Directory.Exists(paths.Projects))
        {
            uncertain.Add(ObjectKind.ClaudeSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        IEnumerable<string> transcripts;
        try
        {
            transcripts = Directory.EnumerateFiles(paths.Projects, "*.jsonl",
                    new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            uncertain.Add(ObjectKind.ClaudeSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        // Probed once per scan, not per file: enumerating processes is the expensive half of D3.
        var claudeRunning = isClaudeRunning();
        var activeSince = now() - activityWindow;

        var complete = true;
        var observed = new List<ObservedCandidate>();
        foreach (var transcriptPath in transcripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(transcriptPath, paths.Projects)) continue;
            // Sessions live exactly one level under projects/; anything deeper is not Claude's layout.
            var projectDirectory = Path.GetDirectoryName(transcriptPath);
            if (string.IsNullOrWhiteSpace(projectDirectory)) continue;
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(projectDirectory) ?? string.Empty),
                    Path.TrimEndingDirectorySeparator(paths.Projects)))
                continue;

            FileObservation observation;
            try
            {
                observation = ReadObservation(transcriptPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            observed.Add(new ObservedCandidate(transcriptPath, observation));
        }

        var candidates = new List<ObservedCandidate>();
        foreach (var live in SelectLiveCopies(observed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Defer instead of failing, exactly like a locked Grok session. Applied to the live
            // copy only, and after the choice: deferring it must not let a frozen older copy of
            // the same session publish in its place.
            if (claudeRunning && live.First.LastWriteTimeUtc >= activeSince.UtcDateTime) { complete = false; continue; }

            candidates.Add(live);
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
                // Unreachable while one session id yields one candidate, and kept as the guard
                // that keeps it that way: two objects under one id must never reach a publish.
                duplicates.Add(item.Id);
                uncertain.Add(ObjectKind.ClaudeSession);
                if (objectsById.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (!complete) uncertain.Add(ObjectKind.ClaudeSession);
        return new SessionScanResult(objects, uncertain, duplicates);
    }

    /// <summary>
    /// Reduces the transcripts sharing one session id to the one that is still being written.
    ///
    /// A Claude session whose working directory changes is copied into the project folder for
    /// the new directory and continued there; the copy left in the old folder stops at the
    /// moment of the move. Both files carry the same session id, so treating them as two objects
    /// would publish one session twice under one logical id — and refusing the scan over it
    /// stops every agent from synchronizing, not just Claude. The newest write is the session;
    /// size and path only break ties, so the choice is the same on every machine and every run.
    /// </summary>
    private static IEnumerable<ObservedCandidate> SelectLiveCopies(List<ObservedCandidate> observed) =>
        observed
            .GroupBy(candidate => Path.GetFileNameWithoutExtension(candidate.TranscriptPath),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate => candidate.First.LastWriteTimeUtc)
                .ThenByDescending(candidate => candidate.First.Length)
                .ThenBy(candidate => candidate.TranscriptPath, StringComparer.OrdinalIgnoreCase)
                .First());

    private static LocalObject? ReadStable(ObservedCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var second = ReadObservation(candidate.TranscriptPath);
            if (candidate.First != second) return null;

            var package = ClaudeSessionPackage.BuildFromFile(candidate.TranscriptPath);
            if (package.Length == 0) return null;
            var hash = ClaudeSessionPackage.HashPackage(package);
            var sessionId = Path.GetFileNameWithoutExtension(candidate.TranscriptPath);
            var logicalId = new LogicalObjectId(ClaudeSessionPackage.ToLogicalId(sessionId));

            return new LocalObject(
                logicalId,
                ObjectKind.ClaudeSession,
                Path.GetFullPath(candidate.TranscriptPath),
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

    private static FileObservation ReadObservation(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new FileObservation(info.Length, info.LastWriteTimeUtc);
    }

    private static bool IsClaudeProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(ClaudeProcessName);
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Fail closed: an unreadable process list defers recent transcripts rather than
            // publishing a file that a live session may still be appending to.
            return true;
        }
    }

    private readonly record struct FileObservation(long Length, DateTime LastWriteTimeUtc);
    private readonly record struct ObservedCandidate(string TranscriptPath, FileObservation First);
}
