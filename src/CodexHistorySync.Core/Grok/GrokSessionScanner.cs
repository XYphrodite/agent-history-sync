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
        foreach (var chatPath in chatFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(chatPath, paths.Sessions)) continue;
            var sessionDir = Path.GetDirectoryName(chatPath);
            if (string.IsNullOrWhiteSpace(sessionDir)) continue;

            var item = await ReadStableAsync(sessionDir, cancellationToken).ConfigureAwait(false);
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

    private async Task<LocalObject?> ReadStableAsync(string sessionDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var chatPath = GrokSessionPackage.ChatHistoryPath(sessionDirectory);
            var first = ReadObservation(chatPath);
            await waitForStability(cancellationToken).ConfigureAwait(false);
            var second = ReadObservation(chatPath);
            if (first != second) return null;

            var package = GrokSessionPackage.BuildFromDirectory(sessionDirectory);
            if (package.Length == 0) return null;
            var hash = GrokSessionPackage.HashPackage(package);
            var sessionId = Path.GetFileName(Path.TrimEndingDirectorySeparator(sessionDirectory));
            var logicalId = new LogicalObjectId(GrokSessionPackage.ToLogicalId(sessionId));

            return new LocalObject(
                logicalId,
                ObjectKind.GrokSession,
                Path.GetFullPath(chatPath),
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
}
