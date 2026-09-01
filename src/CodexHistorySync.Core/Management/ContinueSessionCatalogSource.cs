using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexHistorySync.Core.Continue;

namespace CodexHistorySync.Core.Management;

/// <summary>
/// Lists Continue sessions for the manager and viewer.
///
/// Titles come from the shared index when it lists the session: Continue writes the same title to
/// both places, and reading one small file beats opening every session. A session the index has
/// lost still appears, titled from its own file, because the alternative is a session the user can
/// see in neither place.
/// </summary>
internal sealed class ContinueSessionCatalogSource(ContinuePaths paths, ISessionCatalogIo io) : ILocalSessionCatalogSource
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumIndexBytes = 1024 * 1024;
    private const int MaximumTitleLength = 80;

    public ManagedAgent Agent => ManagedAgent.Continue;

    public async Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        var candidates = EnumerateCandidates();
        var titles = await ReadIndexTitlesAsync(limiter, cancellationToken).ConfigureAwait(false);
        var collected = new SessionCatalogCandidate?[candidates.Count];

        await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = 8
        }, async (index, token) =>
        {
            var (candidate, sessionId) = candidates[index];
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(candidate, paths.Sessions, expectDirectory: false,
                    out var nativePath))
                return;

            var metadata = await ReadMetadataAsync(nativePath, sessionId, limiter, token).ConfigureAwait(false);
            var title = titles.TryGetValue(sessionId, out var listed) && !string.IsNullOrWhiteSpace(listed)
                ? Shorten(listed)
                : metadata.Title;
            collected[index] = new SessionCatalogCandidate(
                sessionId,
                nativePath,
                string.IsNullOrWhiteSpace(title) ? sessionId : title,
                io.LastWriteTime(nativePath),
                metadata.CanRead,
                // Continue writes one name into the session file and the shared index alike, so a
                // title here is always the official one and there is no preview to stand in.
                string.IsNullOrWhiteSpace(title) ? ManagedTitleSource.SessionId : ManagedTitleSource.Official);
        }).ConfigureAwait(false);

        return collected.Where(row => row is not null).Select(row => row!).ToArray();
    }

    private List<(string Candidate, string SessionId)> EnumerateCandidates()
    {
        try
        {
            return io.EnumerateFiles(paths.Sessions, "*.json")
                .Where(candidate => !ContinuePaths.IsIndexFile(candidate))
                .Select(candidate => (Candidate: candidate, SessionId: Path.GetFileNameWithoutExtension(candidate)))
                .Where(candidate => IsSafeSessionId(candidate.SessionId))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private async Task<Dictionary<string, string?>> ReadIndexTitlesAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var indexPath = paths.IndexFilePath;
        if (!io.FileExists(indexPath)) return titles;

        try
        {
            var read = await limiter.RunAsync(
                token => io.ReadPrefixAsync(indexPath, MaximumIndexBytes, token), cancellationToken).ConfigureAwait(false);
            // A truncated index cannot be parsed as an array, and titles are a convenience: the
            // per-session fallback covers it rather than the listing failing.
            if (!read.IsComplete) return titles;

            foreach (var entry in ContinueSessionIndex.Parse(read.Text))
            {
                var sessionId = ContinueSessionIndex.SessionIdOf(entry);
                if (sessionId is null) continue;
                titles[sessionId] = entry["title"] is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text
                    : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                             or JsonException or DecoderFallbackException or ArgumentException)
        {
            // A broken index costs titles, not the listing.
        }

        return titles;
    }

    private async Task<(string? Title, bool CanRead)> ReadMetadataAsync(
        string nativePath,
        string sessionId,
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        try
        {
            // Bounded like every other catalog read: a long conversation must not be paged in to
            // put one row on a list.
            var read = await limiter.RunAsync(
                token => io.ReadPrefixAsync(nativePath, MaximumMetadataBytes, token), cancellationToken)
                .ConfigureAwait(false);
            var title = ReadTitle(read.Text);
            // A session larger than the prefix cannot be parsed here, so readability is decided by
            // what the opening of the file always shows: a JSON object naming this session.
            var canRead = read.Text.TrimStart().StartsWith('{') &&
                          read.Text.Contains("\"sessionId\"", StringComparison.Ordinal);
            // The id is not a title: reporting it as one would hide that nothing named this session.
            return (title, canRead);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or DecoderFallbackException or ArgumentException)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Pulls the title out of the prefix without parsing the document, which for a long session is
    /// mostly not in the prefix to begin with.
    /// </summary>
    private static string? ReadTitle(string prefix)
    {
        const string Marker = "\"title\"";
        var index = prefix.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0) return null;
        var colon = prefix.IndexOf(':', index + Marker.Length);
        if (colon < 0) return null;

        var cursor = colon + 1;
        while (cursor < prefix.Length && char.IsWhiteSpace(prefix[cursor])) cursor++;
        if (cursor >= prefix.Length || prefix[cursor] != '"') return null;

        var builder = new StringBuilder();
        for (cursor++; cursor < prefix.Length; cursor++)
        {
            var character = prefix[cursor];
            if (character == '"') return Shorten(builder.ToString());
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }

            if (++cursor >= prefix.Length) return null;
            switch (prefix[cursor])
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'u' when cursor + 4 < prefix.Length &&
                              ushort.TryParse(prefix.AsSpan(cursor + 1, 4), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture, out var code):
                    builder.Append((char)code);
                    cursor += 4;
                    break;
                default: builder.Append(prefix[cursor]); break;
            }
        }

        return null;
    }

    private static string Shorten(string title)
    {
        var single = title.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaximumTitleLength ? single : single[..MaximumTitleLength];
    }

    private static bool IsSafeSessionId(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        sessionId.Length <= 128 &&
        sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
