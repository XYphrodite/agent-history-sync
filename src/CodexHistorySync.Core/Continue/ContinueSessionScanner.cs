using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Continue;

public sealed class ContinueSessionScanner
{
    /// <summary>
    /// How recently a session must have been written to count as still in use.
    ///
    /// Claude infers liveness from a running process plus write recency (its D3). Continue has no
    /// process of its own — it runs inside the VS Code extension host, and the only candidate
    /// signal, Code.exe, is running whenever the editor is open and would defer everything
    /// forever. So recency stands alone here (design C4). The two-observation stability read is
    /// what actually prevents publishing a half-written file; this window only avoids the churn
    /// of publishing a session that is about to change again.
    /// </summary>
    public static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task> waitForStability;
    private readonly TimeSpan activityWindow;
    private readonly Func<DateTimeOffset> now;

    public ContinueSessionScanner() : this(TimeSpan.FromMilliseconds(50)) { }

    public ContinueSessionScanner(TimeSpan stabilityDelay)
        : this(stabilityDelay, DefaultActivityWindow) { }

    public ContinueSessionScanner(TimeSpan stabilityDelay, TimeSpan activityWindow)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        if (activityWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activityWindow));
        waitForStability = ct => Task.Delay(stabilityDelay, ct);
        this.activityWindow = activityWindow;
        now = () => DateTimeOffset.UtcNow;
    }

    internal ContinueSessionScanner(
        Func<CancellationToken, Task> waitForStability,
        TimeSpan? activityWindow = null,
        Func<DateTimeOffset>? now = null)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
        this.activityWindow = activityWindow ?? DefaultActivityWindow;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SessionScanResult> ScanDetailedAsync(ContinuePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var objectsById = new Dictionary<LogicalObjectId, LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        if (!Directory.Exists(paths.Sessions))
        {
            uncertain.Add(ObjectKind.ContinueSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        IEnumerable<string> sessionFiles;
        try
        {
            // One level only: sessions/ is flat, and anything nested is not Continue's layout.
            sessionFiles = Directory.EnumerateFiles(paths.Sessions, "*.json",
                    new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(path => !ContinuePaths.IsIndexFile(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            uncertain.Add(ObjectKind.ContinueSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var complete = true;

        // Read once per scan rather than once per session: every candidate needs the same file,
        // and re-reading it per session would also let it change underneath the scan.
        string? indexContent = null;
        try
        {
            if (File.Exists(paths.IndexFilePath))
                indexContent = File.ReadAllText(paths.IndexFilePath, new UTF8Encoding(false, true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            // A missing entry is synthesized, so an unreadable index costs fidelity, not the scan.
            complete = false;
        }

        var activeSince = now() - activityWindow;
        var candidates = new List<ObservedCandidate>();
        foreach (var sessionPath in sessionFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(sessionPath, paths.Sessions)) continue;

            FileObservation observation;
            try
            {
                observation = ReadObservation(sessionPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            // Defer instead of failing, exactly like a locked Grok session.
            if (observation.LastWriteTimeUtc >= activeSince.UtcDateTime) { complete = false; continue; }

            candidates.Add(new ObservedCandidate(sessionPath, observation));
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
            results[index] = await Task.Run(() => ReadStable(candidates[index], indexContent, ct), ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        for (var index = 0; index < candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = results[index];
            if (item is null) { complete = false; continue; }
            if (objectsById.TryAdd(item.Id, item)) objects.Add(item);
            else
            {
                // Sessions are flat and named by their id, so this cannot happen without a
                // case-only filename collision. Kept as the guard that keeps it impossible.
                duplicates.Add(item.Id);
                uncertain.Add(ObjectKind.ContinueSession);
                if (objectsById.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (!complete) uncertain.Add(ObjectKind.ContinueSession);
        return new SessionScanResult(objects, uncertain, duplicates);
    }

    private static LocalObject? ReadStable(ObservedCandidate candidate, string? indexContent, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var second = ReadObservation(candidate.SessionPath);
            if (candidate.First != second) return null;

            var package = ContinueSessionPackage.BuildFromFile(candidate.SessionPath, indexContent);
            if (package.Length == 0) return null;
            var hash = ContinueSessionPackage.HashPackage(package);
            var sessionId = Path.GetFileNameWithoutExtension(candidate.SessionPath);
            var logicalId = new LogicalObjectId(ContinueSessionPackage.ToLogicalId(sessionId));

            return new LocalObject(
                logicalId,
                ObjectKind.ContinueSession,
                Path.GetFullPath(candidate.SessionPath),
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

    private readonly record struct FileObservation(long Length, DateTime LastWriteTimeUtc);
    private readonly record struct ObservedCandidate(string SessionPath, FileObservation First);
}
