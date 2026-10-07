using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Hermes;

namespace CodexHistorySync.Core.Management;

internal sealed class HermesSessionCatalogSource(HermesPaths paths) : ILocalSessionCatalogSource
{
    private const int MaximumTitleLength = 80;

    public ManagedAgent Agent => ManagedAgent.Hermes;

    public Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Companions.Count == 0) return Task.FromResult(ScanHome(paths));

        // The same session id in several homes is one session (a copied state.db): the first
        // readable row in home order wins, the primary home first.
        var rows = new List<SessionCatalogCandidate>();
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var home in paths.AllHomes)
        {
            foreach (var row in ScanHome(home))
            {
                if (!byId.TryGetValue(row.SessionId, out var index))
                {
                    byId[row.SessionId] = rows.Count;
                    rows.Add(row);
                }
                else if (!rows[index].CanRead && row.CanRead) rows[index] = row;
            }
        }
        return Task.FromResult<IReadOnlyList<SessionCatalogCandidate>>(rows);
    }

    private static IReadOnlyList<SessionCatalogCandidate> ScanHome(HermesPaths paths)
    {
        HermesReadResult read;
        try
        {
            read = HermesSessionDatabase.ReadProfiles(paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          Microsoft.Data.Sqlite.SqliteException)
        {
            throw new IOException("Hermes sessions could not be read.", exception);
        }

        var rows = read.Snapshots.Select(snapshot =>
        {
            var title = Normalize(CellText(snapshot.Session, "title"));
            var official = title is not null;
            title ??= Preview(FirstUserText(snapshot.Messages));
            return new SessionCatalogCandidate(
                snapshot.SessionId,
                paths.AnchorPath(snapshot.Profile, snapshot.SessionId),
                title ?? snapshot.SessionId,
                Timestamp(snapshot.LastActiveUnix),
                true,
                official ? ManagedTitleSource.Official
                    : title is null ? ManagedTitleSource.SessionId : ManagedTitleSource.Fallback);
        }).ToArray();

        var duplicates = rows.GroupBy(row => row.SessionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return rows.Select(row => duplicates.Contains(row.SessionId) ? row with { CanRead = false } : row).ToArray();
    }

    private static string? FirstUserText(IReadOnlyList<IReadOnlyList<HermesCell>> messages)
    {
        foreach (var message in messages)
        {
            if (!string.Equals(CellText(message, "role"), "user", StringComparison.OrdinalIgnoreCase)) continue;
            var text = CellText(message, "content");
            if (!string.IsNullOrWhiteSpace(text) && !ConversationTechnicalText.IsWrapper(text)) return text;
        }

        return null;
    }

    private static string? CellText(IReadOnlyList<HermesCell> cells, string name)
    {
        var cell = cells.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return cell?.Type == "text" ? cell.Text : null;
    }

    private static string? Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var trimmed = title.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (trimmed.Length == 0 || ConversationTechnicalText.IsWrapper(trimmed)) return null;
        return trimmed.Length <= MaximumTitleLength ? trimmed : trimmed[..MaximumTitleLength];
    }

    private static string? Preview(string? text) => Normalize(text);

    private static DateTimeOffset Timestamp(double unixSeconds)
    {
        try
        {
            var millis = (long)Math.Round(unixSeconds * 1000d, MidpointRounding.AwayFromZero);
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }
}
