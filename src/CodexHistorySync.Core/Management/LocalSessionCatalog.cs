using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Management;

public sealed class LocalSessionCatalog : ILocalSessionCatalog
{
    private const int MaximumConcurrentReads = 8;

    private readonly ILocalSessionCatalogSource? codexSource;
    private readonly ILocalSessionCatalogSource? grokSource;
    private readonly ILocalSessionCatalogSource? claudeSource;
    private readonly IManagedSessionActiveState activeState;

    public LocalSessionCatalog(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState,
        ClaudePaths? claudePaths = null)
        : this(
            codexPaths is null
                ? null
                : new CodexSessionCatalogSource(codexPaths, new SystemSessionCatalogIo()),
            grokPaths is null
                ? null
                : new GrokSessionCatalogSource(grokPaths, new SystemSessionCatalogIo()),
            activeState,
            claudePaths is null
                ? null
                : new ClaudeSessionCatalogSource(claudePaths, new SystemSessionCatalogIo()))
    {
    }

    internal LocalSessionCatalog(
        ILocalSessionCatalogSource? codexSource,
        ILocalSessionCatalogSource? grokSource,
        IManagedSessionActiveState activeState,
        ILocalSessionCatalogSource? claudeSource = null)
    {
        this.codexSource = codexSource;
        this.grokSource = grokSource;
        this.claudeSource = claudeSource;
        this.activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
    }

    public async Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        using var limiter = new SessionCatalogReadLimiter(MaximumConcurrentReads);
        var codexTask = ScanAgentAsync(
            codexSource, ManagedAgent.Codex, limiter, cancellationToken);
        var grokTask = ScanAgentAsync(
            grokSource, ManagedAgent.Grok, limiter, cancellationToken);
        var claudeTask = ScanAgentAsync(
            claudeSource, ManagedAgent.Claude, limiter, cancellationToken);

        await Task.WhenAll(codexTask, grokTask, claudeTask).ConfigureAwait(false);
        return new SessionCatalogSnapshot(
            Order(codexTask.Result),
            Order(grokTask.Result),
            Order(claudeTask.Result))
        {
            ConfiguredAgents = ManagedAgents.All.Where(IsConfigured).ToArray()
        };
    }

    private bool IsConfigured(ManagedAgent agent) => SourceFor(agent) is not null;

    private ILocalSessionCatalogSource? SourceFor(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => codexSource,
        ManagedAgent.Grok => grokSource,
        ManagedAgent.Claude => claudeSource,
        _ => null
    };

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
            () => ReadActiveIdsAsync(agent, cancellationToken),
            cancellationToken);

        await Task.WhenAll(sourceTask, activityTask).ConfigureAwait(false);
        var activity = activityTask.Result;
        return sourceTask.Result.Select(candidate => new ManagedSession(
            agent,
            candidate.SessionId,
            candidate.NativePath,
            candidate.Title,
            candidate.LastModifiedAt,
            activity.Unknown || activity.SessionIds.Contains(candidate.SessionId),
            candidate.CanRead)).ToArray();
    }

    private async Task<ActiveIds> ReadActiveIdsAsync(
        ManagedAgent agent,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = await activeState.GetActiveSessionIdsAsync(agent, cancellationToken).ConfigureAwait(false);
            return new ActiveIds(
                new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase),
                Unknown: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ActiveIds(new HashSet<string>(StringComparer.OrdinalIgnoreCase), Unknown: true);
        }
    }

    private readonly record struct ActiveIds(IReadOnlySet<string> SessionIds, bool Unknown);

    private static IReadOnlyList<ManagedSession> Order(IEnumerable<ManagedSession> sessions) =>
        sessions.OrderByDescending(session => session.LastModifiedAt)
            .ThenBy(session => session.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
