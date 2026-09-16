using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Muse;

namespace CodexHistorySync.Core.Management;

internal sealed class MuseSessionCatalogSource(MusePaths paths, ISessionCatalogIo io) : ILocalSessionCatalogSource
{
    public ManagedAgent Agent => ManagedAgent.Muse;

    public async Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(SessionCatalogReadLimiter limiter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        var candidates = EnumerateCandidates();
        var collected = new CandidateResult?[candidates.Count];

        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 8
        }, async (index, token) =>
        {
            var (candidate, sessionId) = candidates[index];
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(candidate, paths.Sessions, expectDirectory: true, out var nativePath))
                return;
            var metadata = await ReadMetadataAsync(nativePath, sessionId, limiter, token).ConfigureAwait(false);
            collected[index] = new CandidateResult(sessionId, nativePath, metadata);
        }).ConfigureAwait(false);

        var rows = collected.Where(r => r is not null).Select(r => r!).Select(r =>
        {
            var m = r.Metadata;
            return new SessionCatalogCandidate(
                r.SessionId,
                r.NativePath,
                DisplayTitle(m.Title, r.SessionId),
                m.LastModifiedAt ?? LastWriteTime(r.NativePath),
                m.CanRead,
                NormalizeTitle(m.Title) is null ? ManagedTitleSource.SessionId : m.TitleIsOfficial ? ManagedTitleSource.Official : ManagedTitleSource.Fallback);
        }).ToArray();

        var duplicates = rows.GroupBy(r => r.SessionId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Select(r => duplicates.Contains(r.SessionId) ? r with { CanRead = false } : r).ToArray();
    }

    private List<(string Candidate, string SessionId)> EnumerateCandidates()
    {
        try
        {
            var result = new List<(string, string)>();
            foreach (var file in io.EnumerateFiles(paths.Sessions, MusePaths.SessionFileName, SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(file);
                if (dir is null) continue;
                var dirName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!Guid.TryParse(dirName, out _)) continue;
                result.Add((dir, dirName));
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private async Task<Metadata> ReadMetadataAsync(string directory, string sessionId, SessionCatalogReadLimiter limiter, CancellationToken ct)
    {
        var sessionFile = Path.Combine(directory, MusePaths.SessionFileName);
        try
        {
            using var _ = await limiter.AcquireAsync(ct).ConfigureAwait(false);
            var text = await File.ReadAllTextAsync(sessionFile, Encoding.UTF8, ct).ConfigureAwait(false);
            // Try to extract first user message as title
            var title = ExtractTitle(text);
            var lastWrite = File.GetLastWriteTimeUtc(sessionFile);
            return new Metadata(title, title is not null, lastWrite != default ? new DateTimeOffset(lastWrite, TimeSpan.Zero) : null, true);
        }
        catch
        {
            return new Metadata(null, false, null, false);
        }
    }

    private static string? ExtractTitle(string content)
    {
        try
        {
            // Look for first user prompt in the jsonl
            foreach (var line in content.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("\"text\":\"") && line.Contains("Explore"))
                {
                    var idx = line.IndexOf("\"text\":\"", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var start = idx + 8;
                        var end = line.IndexOf("\"", start, StringComparison.Ordinal);
                        if (end > start)
                        {
                            var text = line[start..end];
                            if (!string.IsNullOrWhiteSpace(text))
                                return text.Length > 80 ? text[..80] : text;
                        }
                    }
                }
                // Also try to parse as json and look for payload
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    // Check for different payload structures
                    var root = doc.RootElement;
                    if (root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("record", out var record))
                    {
                        if (record.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
                            return prompt.GetString();
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string DisplayTitle(string? title, string sessionId) =>
        string.IsNullOrWhiteSpace(title) ? sessionId : title.Length > 80 ? title[..80] : title;

    private static string? NormalizeTitle(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim();

    private static DateTimeOffset LastWriteTime(string path)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero); }
        catch { return DateTimeOffset.UtcNow; }
    }

    private sealed record CandidateResult(string SessionId, string NativePath, Metadata Metadata);
    private sealed record Metadata(string? Title, bool TitleIsOfficial, DateTimeOffset? LastModifiedAt, bool CanRead);
}
