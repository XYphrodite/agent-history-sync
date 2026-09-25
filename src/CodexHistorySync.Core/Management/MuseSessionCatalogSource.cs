using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Muse;

namespace CodexHistorySync.Core.Management;

internal sealed class MuseSessionCatalogSource(MusePaths paths, ISessionCatalogIo io) : ILocalSessionCatalogSource
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private readonly ConcurrentDictionary<string, CachedTitle> cache = new(StringComparer.Ordinal);
    public ManagedAgent Agent => ManagedAgent.Muse;

    public async Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(SessionCatalogReadLimiter limiter, CancellationToken cancellationToken)
    {
        if (paths.IsReadOnly)
        {
            var rows = new List<SessionCatalogCandidate>();
            foreach (var disk in paths.OfflineDisks) rows.AddRange(await disk.ScanAsync(cancellationToken).ConfigureAwait(false));
            return rows;
        }
        var directories = MuseSessionDiscovery.MainDirectories(paths.Sessions, cancellationToken);
        var indexed = MuseSessionIndex.Read(paths.Home, cancellationToken);
        var candidates = await ReadDirectoriesAsync(directories, paths.Sessions, indexed, limiter, cancellationToken).ConfigureAwait(false);
        var live = directories.ToHashSet(StringComparer.Ordinal);
        foreach (var key in cache.Keys)
            if (!live.Contains(key)) cache.TryRemove(key, out _);
        return candidates;
    }

    internal async Task<IReadOnlyList<SessionCatalogCandidate>> ReadDirectoriesAsync(
        IReadOnlyList<string> directories, string root, IReadOnlyDictionary<string, MuseIndexedTitle> indexed,
        SessionCatalogReadLimiter limiter, CancellationToken ct)
    {
        var rows = new SessionCatalogCandidate?[directories.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, directories.Count), new ParallelOptions
        {
            CancellationToken = ct, MaxDegreeOfParallelism = 8
        }, async (index, token) =>
        {
            var directory = directories[index];
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(directory, root, true, out var nativePath)) return;
            var id = Path.GetFileName(nativePath);
            var file = Path.Combine(nativePath, MusePaths.SessionFileName);
            try
            {
                var info = new FileInfo(file);
                info.Refresh();
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
                var stamp = (info.Length, info.LastWriteTimeUtc);
                var title = indexed.GetValueOrDefault(id);
                if (string.IsNullOrWhiteSpace(title?.Title))
                {
                    if (cache.TryGetValue(nativePath, out var cached) && cached.Stamp == stamp) title = cached.Title;
                    else
                    {
                        var prefix = await limiter.RunAsync(t => io.ReadPrefixAsync(file, MaximumMetadataBytes, t), token).ConfigureAwait(false);
                        title = ExtractTitle(prefix.Text);
                        cache[nativePath] = new CachedTitle(stamp, title);
                    }
                }
                var normalized = NormalizeTitle(title?.Title);
                rows[index] = new SessionCatalogCandidate(id, nativePath, normalized ?? id,
                    new DateTimeOffset(info.LastWriteTimeUtc), info.Length > 0,
                    normalized is null ? ManagedTitleSource.SessionId : title!.Official ? ManagedTitleSource.Official : ManagedTitleSource.Fallback);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
            {
                rows[index] = new SessionCatalogCandidate(id, nativePath, id, DateTimeOffset.MinValue, false, ManagedTitleSource.SessionId);
            }
        }).ConfigureAwait(false);
        var result = rows.OfType<SessionCatalogCandidate>().ToArray();
        var duplicates = result.GroupBy(r => r.SessionId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result.Select(r => duplicates.Contains(r.SessionId) ? r with { CanRead = false } : r).ToArray();
    }

    private static MuseIndexedTitle? ExtractTitle(string text)
    {
        MuseIndexedTitle? fallback = null;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line.TrimStart('\uFEFF'));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                {
                    if (String(payload, "new_name") is { } name) return new MuseIndexedTitle(name, true);
                    if (payload.TryGetProperty("record", out var record) && String(record, "prompt") is { } prompt)
                        fallback ??= new MuseIndexedTitle(prompt, false);
                }
                if (String(root, "role") == "user" && String(root, "text") is { } message)
                    fallback ??= new MuseIndexedTitle(message, false);
                if (Conversion.MuseConversationReader.ExtractTurn(root) is { Role: Conversion.ConversationRole.User } turn)
                    fallback ??= new MuseIndexedTitle(turn.Text, false);
            }
            catch (JsonException) { }
        }
        return fallback;
    }

    private static string? String(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var normalized = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length > 80 ? normalized[..80] : normalized;
    }

    private sealed record CachedTitle((long Length, DateTime Modified) Stamp, MuseIndexedTitle? Title);
}
