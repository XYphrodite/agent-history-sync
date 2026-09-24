using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Kimi;
using CodexHistorySync.Core.Muse;

namespace CodexHistorySync.Core.Management;

public sealed class LocalSessionCatalog : ILocalSessionCatalog
{
    private const int MaximumConcurrentReads = 8;

    private readonly ILocalSessionCatalogSource? codexSource;
    private readonly ILocalSessionCatalogSource? grokSource;
    private readonly ILocalSessionCatalogSource? claudeSource;
    private readonly ILocalSessionCatalogSource? continueSource;
    private readonly ILocalSessionCatalogSource? kimiSource;
    private readonly ILocalSessionCatalogSource? museSource;
    private readonly IManagedSessionActiveState activeState;

    public LocalSessionCatalog(
        CodexPaths? codexPaths,
        GrokPaths? grokPaths,
        IManagedSessionActiveState activeState,
        ClaudePaths? claudePaths = null,
        ContinuePaths? continuePaths = null,
        KimiPaths? kimiPaths = null,
        MusePaths? musePaths = null)
        : this(
            codexPaths is null || !Directory.Exists(codexPaths.Home)
                ? null
                : new CodexSessionCatalogSource(codexPaths, new SystemSessionCatalogIo()),
            grokPaths is null
                ? null
                : new GrokSessionCatalogSource(grokPaths, new SystemSessionCatalogIo()),
            activeState,
            claudePaths is null
                ? null
                : new ClaudeSessionCatalogSource(claudePaths, new SystemSessionCatalogIo()),
            continuePaths is null
                ? null
                : new ContinueSessionCatalogSource(continuePaths, new SystemSessionCatalogIo()),
            kimiPaths is null
                ? null
                : new KimiSessionCatalogSource(kimiPaths, new SystemSessionCatalogIo()),
            musePaths is null
                ? null
                : new MuseSessionCatalogSource(musePaths, new SystemSessionCatalogIo()))
    {
    }

    internal LocalSessionCatalog(
        ILocalSessionCatalogSource? codexSource,
        ILocalSessionCatalogSource? grokSource,
        IManagedSessionActiveState activeState,
        ILocalSessionCatalogSource? claudeSource = null,
        ILocalSessionCatalogSource? continueSource = null,
        ILocalSessionCatalogSource? kimiSource = null,
        ILocalSessionCatalogSource? museSource = null)
    {
        this.codexSource = codexSource;
        this.grokSource = grokSource;
        this.claudeSource = claudeSource;
        this.continueSource = continueSource;
        this.kimiSource = kimiSource;
        this.museSource = museSource;
        this.activeState = activeState ?? throw new ArgumentNullException(nameof(activeState));
    }

    public async Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        SessionCatalogSnapshot? result = null;
        await foreach (var snapshot in ScanIncrementallyAsync(cancellationToken).ConfigureAwait(false)) result = snapshot;
        return result!;
    }

    public async IAsyncEnumerable<SessionCatalogSnapshot> ScanIncrementallyAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var limiter = new SessionCatalogReadLimiter(MaximumConcurrentReads);
        var configured = ManagedAgents.All.Where(IsConfigured).ToArray();
        var pending = configured.ToDictionary(agent => agent,
            agent => ScanAgentAsync(SourceFor(agent), agent, limiter, lifetime.Token));
        var rows = new Dictionary<ManagedAgent, IReadOnlyList<ManagedSession>>();
        var unavailable = new List<ManagedAgent>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Snapshot();
            while (pending.Count > 0)
            {
                await Task.WhenAny(pending.Values).ConfigureAwait(false);
                foreach (var (agent, task) in pending.Where(pair => pair.Value.IsCompleted).ToArray())
                {
                    try { rows[agent] = Order(await task.ConfigureAwait(false)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    {
                        unavailable.Add(agent);
                    }
                    pending.Remove(agent);
                }
                cancellationToken.ThrowIfCancellationRequested();
                yield return Snapshot();
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(pending.Values).ConfigureAwait(false); }
            catch (Exception) { /* Observe outstanding tasks when a consumer cancels or leaves early. */ }
        }

        SessionCatalogSnapshot Snapshot() => new(
            rows.GetValueOrDefault(ManagedAgent.Codex) ?? [], rows.GetValueOrDefault(ManagedAgent.Grok) ?? [],
            rows.GetValueOrDefault(ManagedAgent.Claude) ?? [], rows.GetValueOrDefault(ManagedAgent.Continue) ?? [],
            rows.GetValueOrDefault(ManagedAgent.Kimi) ?? [], rows.GetValueOrDefault(ManagedAgent.Muse) ?? [])
        {
            ConfiguredAgents = configured,
            PendingAgents = configured.Where(pending.ContainsKey).ToArray(),
            UnavailableAgents = unavailable.ToArray()
        };
    }

    private bool IsConfigured(ManagedAgent agent) => SourceFor(agent) is not null;

    private ILocalSessionCatalogSource? SourceFor(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => codexSource,
        ManagedAgent.Grok => grokSource,
        ManagedAgent.Claude => claudeSource,
        ManagedAgent.Continue => continueSource,
        ManagedAgent.Kimi => kimiSource,
        ManagedAgent.Muse => museSource,
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
            candidate.CanRead,
            candidate.TitleSource)).ToArray();
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
