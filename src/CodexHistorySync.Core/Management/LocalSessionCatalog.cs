using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Management;

public sealed class LocalSessionCatalog : ILocalSessionCatalog
{
    private const int MaximumConcurrentReads = 8;

    private readonly ILocalSessionCatalogSource? codexSource;
    private readonly ILocalSessionCatalogSource? grokSource;
    private readonly IManagedSessionActiveState activeState;

    public LocalSessionCatalog(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState)
        : this(
            codexPaths is null
                ? null
                : new CodexSessionCatalogSource(codexPaths, new SystemSessionCatalogIo()),
            grokPaths is null
                ? null
                : new GrokSessionCatalogSource(grokPaths, new SystemSessionCatalogIo()),
            activeState)
    {
    }

    internal LocalSessionCatalog(
        ILocalSessionCatalogSource? codexSource,
        ILocalSessionCatalogSource? grokSource,
        IManagedSessionActiveState activeState)
    {
        this.codexSource = codexSource;
        this.grokSource = grokSource;
        this.activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
    }

    public async Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        using var limiter = new SessionCatalogReadLimiter(MaximumConcurrentReads);
        var codexTask = ScanAgentAsync(
            codexSource, ManagedAgent.Codex, limiter, cancellationToken);
        var grokTask = ScanAgentAsync(
            grokSource, ManagedAgent.Grok, limiter, cancellationToken);

        await Task.WhenAll(codexTask, grokTask).ConfigureAwait(false);
        return new SessionCatalogSnapshot(
            Order(codexTask.Result),
            Order(grokTask.Result));
    }

    private async Task<IReadOnlyList<ManagedSession>> ScanAgentAsync(
        ILocalSessionCatalogSource? source,
        ManagedAgent agent,
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        if (source is null) return [];

        var sourceTask = Task.Run(
            () => source.ScanAsync(limiter, cancellationToken),
            cancellationToken);
        var activityTask = Task.Run(
            () => IsAgentActiveAsync(agent, cancellationToken),
            cancellationToken);

        await Task.WhenAll(sourceTask, activityTask).ConfigureAwait(false);
        var isActive = activityTask.Result;
        return sourceTask.Result.Select(candidate => new ManagedSession(
            agent,
            candidate.SessionId,
            candidate.NativePath,
            candidate.Title,
            candidate.LastModifiedAt,
            isActive,
            candidate.CanRead)).ToArray();
    }

    private async Task<bool> IsAgentActiveAsync(
        ManagedAgent agent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await activeState.IsAgentActiveAsync(agent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return true;
        }
    }

    private static IReadOnlyList<ManagedSession> Order(IEnumerable<ManagedSession> sessions) =>
        sessions.OrderByDescending(session => session.LastModifiedAt)
            .ThenBy(session => session.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
