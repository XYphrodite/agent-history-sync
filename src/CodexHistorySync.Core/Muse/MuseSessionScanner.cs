using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Muse;

public sealed class MuseSessionScanner
{
    private readonly Func<CancellationToken, Task> waitForStability;
    private readonly Func<bool> isMuseRunning;
    private readonly TimeSpan activityWindow;
    private readonly Func<DateTimeOffset> now;

    public static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromSeconds(30);

    public MuseSessionScanner() : this(TimeSpan.FromMilliseconds(50)) { }

    public MuseSessionScanner(TimeSpan stabilityDelay) : this(stabilityDelay, DefaultActivityWindow) { }

    public MuseSessionScanner(TimeSpan stabilityDelay, TimeSpan activityWindow)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        if (activityWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(activityWindow));
        waitForStability = ct => Task.Delay(stabilityDelay, ct);
        isMuseRunning = IsMuseProcessRunning;
        this.activityWindow = activityWindow;
        now = () => DateTimeOffset.UtcNow;
    }

    internal MuseSessionScanner(Func<CancellationToken, Task> waitForStability, Func<bool>? isMuseRunning = null, TimeSpan? activityWindow = null, Func<DateTimeOffset>? now = null)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
        this.isMuseRunning = isMuseRunning ?? IsMuseProcessRunning;
        this.activityWindow = activityWindow ?? DefaultActivityWindow;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<SessionScanResult> ScanDetailedAsync(MusePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var objectsById = new Dictionary<LogicalObjectId, LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        if (!Directory.Exists(paths.Sessions))
        {
            uncertain.Add(ObjectKind.MuseSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        List<string> sessionDirectories;
        try
        {
            sessionDirectories = EnumerateSessionDirectories(paths.Sessions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            uncertain.Add(ObjectKind.MuseSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var museRunning = isMuseRunning();
        var activeSince = now() - activityWindow;
        var complete = true;
        var candidates = new List<ObservedCandidate>();

        foreach (var sessionDirectory in sessionDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageObservation? observation;
            try
            {
                observation = Observe(sessionDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            if (observation is null) { complete = false; continue; }
            if (museRunning && observation.LastWriteTimeUtc >= activeSince.UtcDateTime) { complete = false; continue; }

            candidates.Add(new ObservedCandidate(sessionDirectory, observation));
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

        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = results[i];
            if (item is null) { complete = false; continue; }
            if (objectsById.TryAdd(item.Id, item)) objects.Add(item);
            else
            {
                duplicates.Add(item.Id);
                uncertain.Add(ObjectKind.MuseSession);
                if (objectsById.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (!complete) uncertain.Add(ObjectKind.MuseSession);
        return new SessionScanResult(objects, uncertain, duplicates);
    }

    private static LocalObject? ReadStable(ObservedCandidate candidate, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var second = Observe(candidate.SessionDirectory);
            if (second is null || candidate.First.Key != second.Key) return null;

            var package = MuseSessionPackage.BuildFromDirectory(candidate.SessionDirectory);
            var hash = MuseSessionPackage.HashPackage(package);
            var sessionId = Path.GetFileName(candidate.SessionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var logicalId = new LogicalObjectId(MuseSessionPackage.ToLogicalId(sessionId));
            var sessionFile = Path.Combine(candidate.SessionDirectory, MusePaths.SessionFileName);
            return new LocalObject(logicalId, ObjectKind.MuseSession, Path.GetFullPath(sessionFile), hash, package.LongLength, new DateTimeOffset(second.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    private static PackageObservation? Observe(string sessionDirectory)
    {
        var files = MuseSessionPackage.ListSynchronizableFiles(sessionDirectory);
        if (files is null) return null;
        var key = new StringBuilder();
        var lastWrite = DateTime.MinValue;
        foreach (var rel in files)
        {
            var info = new FileInfo(Path.Combine(sessionDirectory, rel.Replace('/', Path.DirectorySeparatorChar)));
            info.Refresh();
            key.Append(rel).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            if (info.LastWriteTimeUtc > lastWrite) lastWrite = info.LastWriteTimeUtc;
        }
        return new PackageObservation(key.ToString(), lastWrite);
    }

    private static List<string> EnumerateSessionDirectories(string sessionsRoot)
    {
        var result = new List<string>();
        // Muse sessions are stored as .../sessions/YYYY/MM/DD/<uuid>/session.jsonl or .../sessions/<uuid>/session.jsonl
        // Enumerate all session.jsonl files recursively
        foreach (var file in Directory.EnumerateFiles(sessionsRoot, MusePaths.SessionFileName, SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                var dir = Path.GetDirectoryName(file);
                if (dir is null) continue;
                var dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!Guid.TryParse(dirName, out _)) continue;
                // Ensure the file is at depth that matches Muse's layout (parent is uuid, file is session.jsonl)
                if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(file), MusePaths.SessionFileName)) continue;
                result.Add(dir);
            }
            catch { continue; }
        }
        return result;
    }

    private static bool IsMuseProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("muse").Length > 0 ||
                   Process.GetProcessesByName("Muse").Length > 0;
        }
        catch { return false; }
    }

    private sealed record ObservedCandidate(string SessionDirectory, PackageObservation First);
    private sealed record PackageObservation(string Key, DateTime LastWriteTimeUtc);
}
