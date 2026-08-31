using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.IntegrationTests;

public sealed class SessionManagerStateTests
{
    [Fact]
    public void Move_navigation_is_clamped_to_the_focused_panel_bounds()
    {
        var state = new SessionManagerState(Snapshot(
            codex: [Session(ManagedAgent.Codex, "codex-1"), Session(ManagedAgent.Codex, "codex-2")],
            grok: [Session(ManagedAgent.Grok, "grok-1")]));

        state = state.ApplyNavigation(SessionManagerCommand.MoveUp);
        state = state.ApplyNavigation(SessionManagerCommand.MoveDown);
        state = state.ApplyNavigation(SessionManagerCommand.MoveDown);

        Assert.Equal(1, state.SelectedIndex(ManagedAgent.Codex));
        Assert.Equal("codex-2", state.SelectedSession!.SessionId);

        state = state.ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.MoveDown);

        Assert.Equal(ManagedAgent.Grok, state.FocusedAgent);
        Assert.Equal(0, state.SelectedIndex(ManagedAgent.Grok));
    }

    [Fact]
    public void Focus_navigation_keeps_each_panel_selection_independent()
    {
        var state = new SessionManagerState(Snapshot(
            codex: [Session(ManagedAgent.Codex, "codex-1"), Session(ManagedAgent.Codex, "codex-2")],
            grok: [Session(ManagedAgent.Grok, "grok-1"), Session(ManagedAgent.Grok, "grok-2")]));

        state = state.ApplyNavigation(SessionManagerCommand.MoveDown)
            .ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.MoveDown)
            .ApplyNavigation(SessionManagerCommand.FocusLeft);

        Assert.Equal("codex-2", state.SelectedSession!.SessionId);
        Assert.Equal(1, state.SelectedIndex(ManagedAgent.Grok));
    }

    [Fact]
    public void Snapshot_replacement_preserves_selected_sessions_by_agent_and_id()
    {
        var state = new SessionManagerState(Snapshot(
            codex: [Session(ManagedAgent.Codex, "codex-1"), Session(ManagedAgent.Codex, "codex-2")],
            grok: [Session(ManagedAgent.Grok, "grok-1"), Session(ManagedAgent.Grok, "grok-2")]))
            .ApplyNavigation(SessionManagerCommand.MoveDown)
            .ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.MoveDown);

        state = state.ReplaceSnapshot(Snapshot(
            codex: [Session(ManagedAgent.Codex, "codex-new"), Session(ManagedAgent.Codex, "codex-2")],
            grok: [Session(ManagedAgent.Grok, "grok-new"), Session(ManagedAgent.Grok, "grok-2"), Session(ManagedAgent.Grok, "grok-other")]));

        Assert.Equal("grok-2", state.SelectedSession!.SessionId);
        Assert.Equal(1, state.SelectedIndex(ManagedAgent.Codex));
        Assert.Equal(1, state.SelectedIndex(ManagedAgent.Grok));
    }

    [Fact]
    public void Snapshot_replacement_falls_back_to_the_last_available_index_after_deletion()
    {
        var state = new SessionManagerState(Snapshot(codex:
            [Session(ManagedAgent.Codex, "first"), Session(ManagedAgent.Codex, "second"), Session(ManagedAgent.Codex, "third")],
            grok: []))
            .ApplyNavigation(SessionManagerCommand.MoveDown)
            .ApplyNavigation(SessionManagerCommand.MoveDown);

        state = state.ReplaceSnapshot(Snapshot(
            codex: [Session(ManagedAgent.Codex, "first")],
            grok: []));

        Assert.Equal(0, state.SelectedIndex(ManagedAgent.Codex));
        Assert.Equal("first", state.SelectedSession!.SessionId);
    }

    [Fact]
    public void Viewport_scrolls_to_keep_the_selected_session_visible()
    {
        var state = new SessionManagerState(Snapshot(
            codex: Enumerable.Range(1, 5).Select(index => Session(ManagedAgent.Codex, $"codex-{index}")).ToArray(),
            grok: []))
            .SetViewportRows(2);

        for (var index = 0; index < 4; index++) state = state.ApplyNavigation(SessionManagerCommand.MoveDown);

        Assert.Equal(4, state.SelectedIndex(ManagedAgent.Codex));
        Assert.Equal(3, state.ViewportOffset(ManagedAgent.Codex));

        state = state.ApplyNavigation(SessionManagerCommand.MoveUp)
            .ApplyNavigation(SessionManagerCommand.MoveUp);

        Assert.Equal(2, state.SelectedIndex(ManagedAgent.Codex));
        Assert.Equal(2, state.ViewportOffset(ManagedAgent.Codex));
    }

    [Fact]
    public void Empty_lists_and_every_supported_viewport_height_keep_indexes_nonnegative()
    {
        var empty = new SessionManagerState(Snapshot([], []));

        foreach (var rows in Enumerable.Range(1, 100))
        {
            var state = empty.SetViewportRows(rows)
                .ApplyNavigation(SessionManagerCommand.MoveDown)
                .ApplyNavigation(SessionManagerCommand.MoveUp)
                .ReplaceSnapshot(Snapshot([], []));

            Assert.Equal(0, state.SelectedIndex(ManagedAgent.Codex));
            Assert.Equal(0, state.SelectedIndex(ManagedAgent.Grok));
            Assert.Equal(0, state.ViewportOffset(ManagedAgent.Codex));
            Assert.Equal(0, state.ViewportOffset(ManagedAgent.Grok));
            Assert.Null(state.SelectedSession);
        }
    }

    [Fact]
    public void State_owns_a_snapshot_that_cannot_be_mutated_by_the_catalog_caller()
    {
        var codex = new List<ManagedSession> { Session(ManagedAgent.Codex, "stable") };
        var state = new SessionManagerState(Snapshot(codex, []));

        codex.Clear();

        Assert.Equal("stable", state.SelectedSession!.SessionId);
        Assert.Equal(0, state.SelectedIndex(ManagedAgent.Codex));
        Assert.Equal(0, state.ViewportOffset(ManagedAgent.Codex));
    }

    [Fact]
    public void Exposed_snapshot_panels_cannot_mutate_state()
    {
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "stable")],
            [Session(ManagedAgent.Grok, "stable-grok")]));
        var exposedCodex = Assert.IsAssignableFrom<IList<ManagedSession>>(state.Snapshot.Codex);
        var exposedGrok = Assert.IsAssignableFrom<IList<ManagedSession>>(state.Snapshot.Grok);

        Assert.Throws<NotSupportedException>(() => exposedCodex[0] = Session(ManagedAgent.Codex, "replaced"));
        Assert.Throws<NotSupportedException>(() => exposedGrok[0] = Session(ManagedAgent.Grok, "replaced-grok"));

        Assert.Equal("stable", state.SelectedSession!.SessionId);
        Assert.Equal("stable-grok", state.ApplyNavigation(SessionManagerCommand.FocusRight).SelectedSession!.SessionId);
    }

    [Fact]
    public void Search_filters_both_panels_by_title_only_ignoring_case_and_resets_selection()
    {
        var state = new SessionManagerState(Snapshot(
            [
                Session(ManagedAgent.Codex, "codex-beta", "Beta task"),
                Session(ManagedAgent.Codex, "codex-alpha", "Alpha task"),
                Session(ManagedAgent.Codex, "alpha-only-in-id", "Different title")
            ],
            [
                Session(ManagedAgent.Grok, "grok-beta", "Another task"),
                Session(ManagedAgent.Grok, "grok-alpha", "ALPHA notes")
            ]))
            .ApplyNavigation(SessionManagerCommand.MoveDown)
            .ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.MoveDown);

        state = state.WithSearchQuery("alpha");

        Assert.Equal("alpha", state.SearchQuery);
        Assert.Equal(["codex-alpha"], state.Snapshot.Codex.Select(session => session.SessionId));
        Assert.Equal(["grok-alpha"], state.Snapshot.Grok.Select(session => session.SessionId));
        Assert.Equal(0, state.SelectedIndex(ManagedAgent.Codex));
        Assert.Equal(0, state.SelectedIndex(ManagedAgent.Grok));
        Assert.Equal(0, state.ViewportOffset(ManagedAgent.Codex));
        Assert.Equal(0, state.ViewportOffset(ManagedAgent.Grok));
        Assert.Equal("grok-alpha", state.SelectedSession!.SessionId);
    }

    [Fact]
    public void Clearing_search_restores_the_owned_snapshot_and_refresh_keeps_an_active_filter()
    {
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "one", "Needle one"), Session(ManagedAgent.Codex, "two", "Other")],
            [Session(ManagedAgent.Grok, "three", "Needle three")]))
            .WithSearchQuery("needle");

        state = state.ReplaceSnapshot(Snapshot(
            [Session(ManagedAgent.Codex, "four", "Needle four"), Session(ManagedAgent.Codex, "five", "Other")],
            [Session(ManagedAgent.Grok, "six", "Needle six")]));

        Assert.Equal(["four"], state.Snapshot.Codex.Select(session => session.SessionId));
        Assert.Equal(["six"], state.Snapshot.Grok.Select(session => session.SessionId));

        state = state.WithSearchQuery(string.Empty);

        Assert.Equal(string.Empty, state.SearchQuery);
        Assert.Equal(["four", "five"], state.Snapshot.Codex.Select(session => session.SessionId));
        Assert.Equal(["six"], state.Snapshot.Grok.Select(session => session.SessionId));
    }

    [Fact]
    public void VisibleAgents_OmitsAnAgentWithoutAResolvableHome()
    {
        var twoAgents = new SessionManagerState(Snapshot([Session(ManagedAgent.Codex, "a")], []));
        var threeAgents = new SessionManagerState(ThreeAgentSnapshot(
            [Session(ManagedAgent.Codex, "a")], [], [Session(ManagedAgent.Claude, "c")]));

        Assert.Equal([ManagedAgent.Codex, ManagedAgent.Grok], twoAgents.VisibleAgents);
        Assert.Equal([ManagedAgent.Codex, ManagedAgent.Grok, ManagedAgent.Claude], threeAgents.VisibleAgents);
    }

    [Fact]
    public void ConfiguredButEmptyAgentKeepsItsPanel()
    {
        // Zero sessions is not the same as "not installed": the panel must stay.
        var state = new SessionManagerState(ThreeAgentSnapshot([Session(ManagedAgent.Codex, "a")], [], []));

        Assert.Equal([ManagedAgent.Codex, ManagedAgent.Grok, ManagedAgent.Claude], state.VisibleAgents);
    }

    [Fact]
    public void ContinueTakesItsOwnPanelAndItsOwnSelection()
    {
        var state = new SessionManagerState(FourAgentSnapshot(
            [Session(ManagedAgent.Codex, "a")], [Session(ManagedAgent.Grok, "b")],
            [Session(ManagedAgent.Claude, "c")], [Session(ManagedAgent.Continue, "d")]));

        var last = state
            .ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.FocusRight);

        Assert.Equal(ManagedAgents.All, state.VisibleAgents);
        Assert.Equal(ManagedAgent.Continue, last.FocusedAgent);
        Assert.Equal("d", last.SelectedSession?.SessionId);
        Assert.Equal(ManagedAgent.Claude,
            last.ApplyNavigation(SessionManagerCommand.FocusLeft).FocusedAgent);
    }

    [Fact]
    public void FocusRightStepsThroughVisiblePanelsAndStopsAtTheLast()
    {
        var state = new SessionManagerState(ThreeAgentSnapshot(
            [Session(ManagedAgent.Codex, "a")], [Session(ManagedAgent.Grok, "b")], [Session(ManagedAgent.Claude, "c")]));

        var grok = state.ApplyNavigation(SessionManagerCommand.FocusRight);
        var claude = grok.ApplyNavigation(SessionManagerCommand.FocusRight);
        var stillClaude = claude.ApplyNavigation(SessionManagerCommand.FocusRight);

        Assert.Equal(ManagedAgent.Grok, grok.FocusedAgent);
        Assert.Equal(ManagedAgent.Claude, claude.FocusedAgent);
        Assert.Equal(ManagedAgent.Claude, stillClaude.FocusedAgent);
        Assert.Equal("c", stillClaude.SelectedSession?.SessionId);
    }

    [Fact]
    public void FocusLeftSkipsBackThroughVisiblePanelsOnly()
    {
        // Without a Claude home, right from Grok must not land on an invisible panel.
        var state = new SessionManagerState(Snapshot([Session(ManagedAgent.Codex, "a")], [Session(ManagedAgent.Grok, "b")]))
            .ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.FocusRight);

        Assert.Equal(ManagedAgent.Grok, state.FocusedAgent);
        Assert.Equal(ManagedAgent.Codex, state.ApplyNavigation(SessionManagerCommand.FocusLeft).FocusedAgent);
    }

    [Fact]
    public void SelectionIsTrackedPerAgentAndSurvivesFocusChanges()
    {
        var state = new SessionManagerState(ThreeAgentSnapshot(
            [Session(ManagedAgent.Codex, "a1"), Session(ManagedAgent.Codex, "a2")],
            [Session(ManagedAgent.Grok, "b1")],
            [Session(ManagedAgent.Claude, "c1"), Session(ManagedAgent.Claude, "c2")]));

        var moved = state
            .ApplyNavigation(SessionManagerCommand.MoveDown)
            .ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.FocusRight)
            .ApplyNavigation(SessionManagerCommand.MoveDown);

        Assert.Equal(1, moved.SelectedIndex(ManagedAgent.Codex));
        Assert.Equal(0, moved.SelectedIndex(ManagedAgent.Grok));
        Assert.Equal(1, moved.SelectedIndex(ManagedAgent.Claude));
        Assert.Equal("c2", moved.SelectedSession?.SessionId);
    }

    [Fact]
    public void SearchFiltersEveryPanelIncludingClaude()
    {
        var state = new SessionManagerState(ThreeAgentSnapshot(
            [Session(ManagedAgent.Codex, "a", "keep me"), Session(ManagedAgent.Codex, "a2", "drop")],
            [Session(ManagedAgent.Grok, "b", "drop")],
            [Session(ManagedAgent.Claude, "c", "keep me too")]));

        var filtered = state.WithSearchQuery("keep");

        Assert.Equal(["a"], filtered.Snapshot.Codex.Select(session => session.SessionId));
        Assert.Empty(filtered.Snapshot.Grok);
        Assert.Equal(["c"], filtered.Snapshot.Claude.Select(session => session.SessionId));
    }

    private static SessionCatalogSnapshot Snapshot(IReadOnlyList<ManagedSession> codex, IReadOnlyList<ManagedSession> grok) =>
        new(codex, grok);

    private static SessionCatalogSnapshot ThreeAgentSnapshot(
        IReadOnlyList<ManagedSession> codex,
        IReadOnlyList<ManagedSession> grok,
        IReadOnlyList<ManagedSession> claude) =>
        // Named agents rather than "all of them": these tests are about what a three-agent machine
        // shows, and every agent added later would otherwise silently grow them a panel.
        new(codex, grok, claude)
        {
            ConfiguredAgents = [ManagedAgent.Codex, ManagedAgent.Grok, ManagedAgent.Claude]
        };

    private static SessionCatalogSnapshot FourAgentSnapshot(
        IReadOnlyList<ManagedSession> codex,
        IReadOnlyList<ManagedSession> grok,
        IReadOnlyList<ManagedSession> claude,
        IReadOnlyList<ManagedSession> continueSessions) =>
        new(codex, grok, claude, continueSessions) { ConfiguredAgents = ManagedAgents.All };

    private static ManagedSession Session(ManagedAgent agent, string id, string? title = null) =>
        new(agent, id, $"C:\\injected\\{id}", title ?? id, DateTimeOffset.UnixEpoch, false, true);
}
