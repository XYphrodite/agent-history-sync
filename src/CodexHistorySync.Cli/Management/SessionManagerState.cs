using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public sealed class SessionManagerState
{
    public SessionManagerState(
        SessionCatalogSnapshot snapshot,
        ManagedAgent focusedAgent = ManagedAgent.Codex,
        int viewportRows = 10)
        : this(snapshot, focusedAgent, viewportRows, 0, 0, 0, 0)
    {
    }

    private SessionManagerState(
        SessionCatalogSnapshot snapshot,
        ManagedAgent focusedAgent,
        int viewportRows,
        int codexSelectedIndex,
        int grokSelectedIndex,
        int codexViewportOffset,
        int grokViewportOffset)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = new SessionCatalogSnapshot(
            Array.AsReadOnly(snapshot.Codex.ToArray()),
            Array.AsReadOnly(snapshot.Grok.ToArray()));
        FocusedAgent = focusedAgent;
        ViewportRows = Math.Max(1, viewportRows);
        CodexSelectedIndex = ClampSelection(codexSelectedIndex, Snapshot.Codex.Count);
        GrokSelectedIndex = ClampSelection(grokSelectedIndex, Snapshot.Grok.Count);
        CodexViewportOffset = ClampViewport(codexViewportOffset, CodexSelectedIndex, ViewportRows, Snapshot.Codex.Count);
        GrokViewportOffset = ClampViewport(grokViewportOffset, GrokSelectedIndex, ViewportRows, Snapshot.Grok.Count);
    }

    public SessionCatalogSnapshot Snapshot { get; }
    public ManagedAgent FocusedAgent { get; }
    public int ViewportRows { get; }
    public int CodexSelectedIndex { get; }
    public int GrokSelectedIndex { get; }
    public int CodexViewportOffset { get; }
    public int GrokViewportOffset { get; }

    public ManagedSession? SelectedSession => SelectedSessionFor(FocusedAgent);

    public int SelectedIndex(ManagedAgent agent) =>
        agent == ManagedAgent.Codex ? CodexSelectedIndex : GrokSelectedIndex;

    public int ViewportOffset(ManagedAgent agent) =>
        agent == ManagedAgent.Codex ? CodexViewportOffset : GrokViewportOffset;

    public SessionManagerState ApplyNavigation(SessionManagerCommand command)
    {
        var focusedAgent = FocusedAgent;
        var codexSelectedIndex = CodexSelectedIndex;
        var grokSelectedIndex = GrokSelectedIndex;

        switch (command)
        {
            case SessionManagerCommand.FocusLeft:
                focusedAgent = ManagedAgent.Codex;
                break;
            case SessionManagerCommand.FocusRight:
                focusedAgent = ManagedAgent.Grok;
                break;
            case SessionManagerCommand.MoveUp:
                if (focusedAgent == ManagedAgent.Codex) codexSelectedIndex--;
                else grokSelectedIndex--;
                break;
            case SessionManagerCommand.MoveDown:
                if (focusedAgent == ManagedAgent.Codex) codexSelectedIndex++;
                else grokSelectedIndex++;
                break;
        }

        return new SessionManagerState(Snapshot, focusedAgent, ViewportRows, codexSelectedIndex, grokSelectedIndex,
            CodexViewportOffset, GrokViewportOffset);
    }

    public SessionManagerState ReplaceSnapshot(SessionCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var codexSelected = SelectionForReplacement(ManagedAgent.Codex, snapshot.Codex, CodexSelectedIndex);
        var grokSelected = SelectionForReplacement(ManagedAgent.Grok, snapshot.Grok, GrokSelectedIndex);
        return new SessionManagerState(snapshot, FocusedAgent, ViewportRows, codexSelected, grokSelected,
            CodexViewportOffset, GrokViewportOffset);
    }

    public SessionManagerState SetViewportRows(int rows) =>
        new(Snapshot, FocusedAgent, rows, CodexSelectedIndex, GrokSelectedIndex,
            CodexViewportOffset, GrokViewportOffset);

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
        var sessions = agent == ManagedAgent.Codex ? Snapshot.Codex : Snapshot.Grok;
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
