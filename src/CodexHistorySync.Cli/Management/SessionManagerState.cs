using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public sealed class SessionManagerState
{
    private readonly IReadOnlyDictionary<ManagedAgent, int> selectedIndexes;
    private readonly IReadOnlyDictionary<ManagedAgent, int> viewportOffsets;

    public SessionManagerState(
        SessionCatalogSnapshot snapshot,
        ManagedAgent focusedAgent = ManagedAgent.Codex,
        int viewportRows = 10)
        : this(snapshot, focusedAgent, viewportRows, string.Empty, Zeroes(), Zeroes())
    {
    }

    private SessionManagerState(
        SessionCatalogSnapshot sourceSnapshot,
        ManagedAgent focusedAgent,
        int viewportRows,
        string searchQuery,
        IReadOnlyDictionary<ManagedAgent, int> selectedIndexes,
        IReadOnlyDictionary<ManagedAgent, int> viewportOffsets)
    {
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        SourceSnapshot = Copy(sourceSnapshot);
        SearchQuery = searchQuery ?? string.Empty;
        Snapshot = FilterSnapshot(SourceSnapshot, SearchQuery);
        VisibleAgents = Snapshot.ConfiguredAgents.Count == 0
            ? [ManagedAgent.Codex]
            : Snapshot.ConfiguredAgents;
        // Focus can only rest on a panel that exists; otherwise navigation and actions would
        // address a column the user cannot see.
        FocusedAgent = VisibleAgents.Contains(focusedAgent) ? focusedAgent : VisibleAgents[0];
        ViewportRows = Math.Max(1, viewportRows);

        var selected = new Dictionary<ManagedAgent, int>();
        var offsets = new Dictionary<ManagedAgent, int>();
        foreach (var agent in ManagedAgents.All)
        {
            var count = Snapshot.For(agent).Count;
            var index = ClampSelection(Lookup(selectedIndexes, agent), count);
            selected[agent] = index;
            offsets[agent] = ClampViewport(Lookup(viewportOffsets, agent), index, ViewportRows, count);
        }
        this.selectedIndexes = selected;
        this.viewportOffsets = offsets;
    }

    public SessionCatalogSnapshot Snapshot { get; }
    public string SearchQuery { get; }
    public ManagedAgent FocusedAgent { get; }
    public IReadOnlyList<ManagedAgent> VisibleAgents { get; }
    public int ViewportRows { get; }

    public int CodexSelectedIndex => SelectedIndex(ManagedAgent.Codex);
    public int GrokSelectedIndex => SelectedIndex(ManagedAgent.Grok);
    public int CodexViewportOffset => ViewportOffset(ManagedAgent.Codex);
    public int GrokViewportOffset => ViewportOffset(ManagedAgent.Grok);

    public ManagedSession? SelectedSession => SelectedSessionFor(FocusedAgent);

    public int SelectedIndex(ManagedAgent agent) => Lookup(selectedIndexes, agent);

    public int ViewportOffset(ManagedAgent agent) => Lookup(viewportOffsets, agent);

    public SessionManagerState ApplyNavigation(SessionManagerCommand command)
    {
        var focusedAgent = FocusedAgent;
        var selected = selectedIndexes.ToDictionary(pair => pair.Key, pair => pair.Value);

        switch (command)
        {
            // With three panels, left and right step through the visible ones instead of
            // jumping to a fixed agent.
            case SessionManagerCommand.FocusLeft:
                focusedAgent = Neighbour(-1);
                break;
            case SessionManagerCommand.FocusRight:
                focusedAgent = Neighbour(1);
                break;
            case SessionManagerCommand.MoveUp:
                selected[focusedAgent] = Lookup(selected, focusedAgent) - 1;
                break;
            case SessionManagerCommand.MoveDown:
                selected[focusedAgent] = Lookup(selected, focusedAgent) + 1;
                break;
        }

        return new SessionManagerState(SourceSnapshot, focusedAgent, ViewportRows, SearchQuery, selected, viewportOffsets);
    }

    public SessionManagerState ReplaceSnapshot(SessionCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var filtered = FilterSnapshot(snapshot, SearchQuery);
        var selected = ManagedAgents.All.ToDictionary(
            agent => agent,
            agent => SelectionForReplacement(agent, filtered.For(agent), SelectedIndex(agent)));
        return new SessionManagerState(snapshot, FocusedAgent, ViewportRows, SearchQuery, selected, viewportOffsets);
    }

    public SessionManagerState WithSearchQuery(string? query) =>
        new(SourceSnapshot, FocusedAgent, ViewportRows, query ?? string.Empty, Zeroes(), Zeroes());

    public SessionManagerState SetViewportRows(int rows) =>
        new(SourceSnapshot, FocusedAgent, rows, SearchQuery, selectedIndexes, viewportOffsets);

    private SessionCatalogSnapshot SourceSnapshot { get; }

    private ManagedAgent Neighbour(int step)
    {
        var current = VisibleAgents.ToList().IndexOf(FocusedAgent);
        if (current < 0) return VisibleAgents[0];
        return VisibleAgents[Math.Clamp(current + step, 0, VisibleAgents.Count - 1)];
    }

    private static Dictionary<ManagedAgent, int> Zeroes() =>
        ManagedAgents.All.ToDictionary(agent => agent, _ => 0);

    private static int Lookup(IReadOnlyDictionary<ManagedAgent, int> values, ManagedAgent agent) =>
        values.TryGetValue(agent, out var value) ? value : 0;

    private static SessionCatalogSnapshot Copy(SessionCatalogSnapshot snapshot) => new(
        Array.AsReadOnly(snapshot.Codex.ToArray()),
        Array.AsReadOnly(snapshot.Grok.ToArray()),
        Array.AsReadOnly(snapshot.Claude.ToArray()),
        Array.AsReadOnly(snapshot.Continue.ToArray()))
    {
        ConfiguredAgents = Array.AsReadOnly(snapshot.ConfiguredAgents.ToArray())
    };

    private static SessionCatalogSnapshot FilterSnapshot(SessionCatalogSnapshot snapshot, string query)
    {
        if (query.Length == 0) return Copy(snapshot);

        return new SessionCatalogSnapshot(
            Match(snapshot.Codex, query),
            Match(snapshot.Grok, query),
            Match(snapshot.Claude, query),
            Match(snapshot.Continue, query))
        {
            ConfiguredAgents = Array.AsReadOnly(snapshot.ConfiguredAgents.ToArray())
        };
    }

    private static IReadOnlyList<ManagedSession> Match(IEnumerable<ManagedSession> sessions, string query) =>
        Array.AsReadOnly(sessions
            .Where(session => session.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());

    private int SelectionForReplacement(ManagedAgent agent, IReadOnlyList<ManagedSession> replacement, int fallback)
    {
        var selected = SelectedSessionFor(agent);
        if (selected is null) return fallback;

        for (var index = 0; index < replacement.Count; index++)
        {
            var candidate = replacement[index];
            if (candidate.Agent == selected.Agent && candidate.SessionId == selected.SessionId) return index;
        }

        return fallback;
    }

    private ManagedSession? SelectedSessionFor(ManagedAgent agent)
    {
        var sessions = Snapshot.For(agent);
        var index = SelectedIndex(agent);
        return index >= 0 && index < sessions.Count ? sessions[index] : null;
    }

    private static int ClampSelection(int index, int count) =>
        count == 0 ? 0 : Math.Clamp(index, 0, count - 1);

    private static int ClampViewport(int offset, int selectedIndex, int rows, int count)
    {
        if (count == 0) return 0;

        var maximumOffset = Math.Max(0, count - rows);
        var clampedOffset = Math.Clamp(offset, 0, maximumOffset);
        if (selectedIndex < clampedOffset) return selectedIndex;
        if (selectedIndex >= clampedOffset + rows) return Math.Min(selectedIndex - rows + 1, maximumOffset);
        return clampedOffset;
    }
}
