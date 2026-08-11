using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Grok;

public sealed class GrokSessionScanner
{
    private readonly Func<CancellationToken, Task> waitForStability;

    public GrokSessionScanner() : this(TimeSpan.FromMilliseconds(50)) { }

    public GrokSessionScanner(TimeSpan stabilityDelay)
    {
        if (stabilityDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        waitForStability = ct => Task.Delay(stabilityDelay, ct);
    }

    internal GrokSessionScanner(Func<CancellationToken, Task> waitForStability)
    {
        this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
    }

    public async Task<SessionScanResult> ScanDetailedAsync(GrokPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var objectsById = new Dictionary<LogicalObjectId, LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        if (!Directory.Exists(paths.Sessions))
        {
            uncertain.Add(ObjectKind.GrokSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var activeIds = LoadActiveSessionIds(paths.Home);

        IEnumerable<string> chatFiles;
        try
        {
            chatFiles = Directory.EnumerateFiles(paths.Sessions, "chat_history.jsonl",
                    new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            uncertain.Add(ObjectKind.GrokSession);
            return new SessionScanResult(objects, uncertain, duplicates);
        }

        var complete = true;
        var candidates = new List<ObservedCandidate>();
        foreach (var chatPath in chatFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(chatPath, paths.Sessions)) continue;
            var sessionDir = Path.GetDirectoryName(chatPath);
            if (string.IsNullOrWhiteSpace(sessionDir)) continue;
            var sessionId = Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDir));
            // Skip sessions currently open in a live Grok CLI process.
            if (activeIds.Contains(sessionId)) { complete = false; continue; }

            try
            {
                candidates.Add(new ObservedCandidate(sessionDir, chatPath, ReadObservation(chatPath)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }

        if (candidates.Count != 0)
            await waitForStability(cancellationToken).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = ReadStable(candidate, cancellationToken);
            if (item is null) { complete = false; continue; }
            if (objectsById.TryAdd(item.Id, item)) objects.Add(item);
            else
            {
                duplicates.Add(item.Id);
                uncertain.Add(ObjectKind.GrokSession);
                if (objectsById.Remove(item.Id, out var prior)) objects.Remove(prior);
            }
        }

        if (!complete) uncertain.Add(ObjectKind.GrokSession);
        return new SessionScanResult(objects, uncertain, duplicates);
    }

    private static LocalObject? ReadStable(
        ObservedCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var second = ReadObservation(candidate.ChatPath);
            if (candidate.First != second) return null;

            var package = GrokSessionPackage.BuildFromDirectory(candidate.SessionDirectory);
            if (package.Length == 0) return null;
            var hash = GrokSessionPackage.HashPackage(package);
            var sessionId = Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate.SessionDirectory));
            var logicalId = new LogicalObjectId(GrokSessionPackage.ToLogicalId(sessionId));

            return new LocalObject(
                logicalId,
                ObjectKind.GrokSession,
                Path.GetFullPath(candidate.ChatPath),
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

    private static HashSet<string> LoadActiveSessionIds(string grokHome)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(grokHome, "active_sessions.json");
        if (!File.Exists(path)) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("session_id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fail open: still scan; stage-time hash check will defer races.
        }
        return result;
    }

    private readonly record struct FileObservation(long Length, DateTime LastWriteTimeUtc);
    private readonly record struct ObservedCandidate(
        string SessionDirectory,
        string ChatPath,
        FileObservation First);
}
