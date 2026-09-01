namespace CodexHistorySync.Core.Management;

internal interface ILocalSessionCatalogSource
{
    ManagedAgent Agent { get; }

    Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken);
}

internal sealed record SessionCatalogCandidate(
    string SessionId,
    string NativePath,
    string Title,
    DateTimeOffset LastModifiedAt,
    bool CanRead,
    ManagedTitleSource TitleSource = ManagedTitleSource.Official);
