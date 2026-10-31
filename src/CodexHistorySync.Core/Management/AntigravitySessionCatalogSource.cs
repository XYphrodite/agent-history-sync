using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Management;

internal sealed class AntigravitySessionCatalogSource(AntigravityPaths paths) : ILocalSessionCatalogSource
{
    private const int MaximumTitleLength = 80;

    public ManagedAgent Agent => ManagedAgent.Antigravity;

    public Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<AntigravitySummaryRow> summaries;
        try
        {
            summaries = AntigravityDatabase.ReadSummaries(paths.ConversationSummariesDbPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
                                          Microsoft.Data.Sqlite.SqliteException)
        {
            throw new IOException("Antigravity sessions could not be read.", exception);
        }

        var rows = summaries
            .Where(row => string.IsNullOrEmpty(row.ParentConversationId) && row.NestingDepth == 0 && AntigravityPaths.IsSessionId(row.ConversationId))
            .Select(row =>
            {
                var title = Normalize(row.Title);
                var official = title is not null;
                var canRead = File.Exists(paths.ConversationDatabasePath(row.ConversationId));
                var timestamp = row.UpdatedAt > DateTimeOffset.MinValue ? row.UpdatedAt : row.CreatedAt;

                return new SessionCatalogCandidate(
                    row.ConversationId,
                    paths.AnchorPath(row.ConversationId),
                    title ?? row.ConversationId,
                    timestamp,
                    canRead,
                    official ? ManagedTitleSource.Official : ManagedTitleSource.SessionId);
            })
            .ToArray();

        var duplicates = rows.GroupBy(row => row.SessionId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        return Task.FromResult<IReadOnlyList<SessionCatalogCandidate>>(
            rows.Select(row => duplicates.Contains(row.SessionId) ? row with { CanRead = false } : row).ToArray());
    }

    private static string? Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var trimmed = title.Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (trimmed.Length == 0 || ConversationTechnicalText.IsWrapper(trimmed)) return null;
        return trimmed.Length <= MaximumTitleLength ? trimmed : trimmed[..MaximumTitleLength];
    }
}
