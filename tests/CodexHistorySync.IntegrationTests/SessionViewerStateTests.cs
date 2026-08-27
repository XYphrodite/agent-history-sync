using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.IntegrationTests;

public sealed class SessionViewerStateTests
{
    [Fact]
    public void Create_MergesEveryAgentIntoOneListNewestFirst()
    {
        var state = SessionViewerState.Create(Snapshot(
            codex: [Session(ManagedAgent.Codex, "codex-old", Minutes(10))],
            grok: [Session(ManagedAgent.Grok, "grok-newest", Minutes(30))],
            claude: [Session(ManagedAgent.Claude, "claude-middle", Minutes(20))]));

        Assert.Equal(
            ["grok-newest", "claude-middle", "codex-old"],
            state.Sessions.Select(session => session.SessionId));
        Assert.Equal("grok-newest", state.SelectedSession?.SessionId);
    }

    [Fact]
    public void MoveDown_MovesTheListSelectionWhileTheListHasFocus()
    {
        var state = SessionViewerState.Create(ThreeSessions());

        var moved = state.Apply(SessionViewerCommand.MoveDown).Apply(SessionViewerCommand.MoveDown);

        Assert.Equal(2, moved.SelectedIndex);
        Assert.Equal(SessionViewerFocus.List, moved.Focus);
    }

    [Fact]
    public void SelectionStopsAtBothEndsOfTheList()
    {
        var state = SessionViewerState.Create(ThreeSessions());

        Assert.Equal(0, state.Apply(SessionViewerCommand.MoveUp).SelectedIndex);
        Assert.Equal(2, state.Apply(SessionViewerCommand.End).Apply(SessionViewerCommand.MoveDown).SelectedIndex);
    }

    [Fact]
    public void FocusContent_IsRefusedUntilSomethingIsLoaded()
    {
        var loading = SessionViewerState.Create(ThreeSessions())
            .WithContent(new SessionContentState(SessionContentStatus.Loading));

        Assert.Equal(SessionViewerFocus.List, loading.Apply(SessionViewerCommand.FocusContent).Focus);
        Assert.Equal(SessionViewerFocus.Content, Loaded(40).Apply(SessionViewerCommand.FocusContent).Focus);
    }

    [Fact]
    public void MoveDown_ScrollsTheContentOnceItHasFocus()
    {
        var state = Loaded(40).Apply(SessionViewerCommand.FocusContent);

        var scrolled = state.Apply(SessionViewerCommand.MoveDown).Apply(SessionViewerCommand.MoveDown);

        Assert.Equal(2, scrolled.ContentOffset);
        Assert.Equal(0, scrolled.SelectedIndex);
    }

    [Fact]
    public void ContentScrollingStopsAtBothEnds()
    {
        var state = Loaded(40).Apply(SessionViewerCommand.FocusContent);

        Assert.Equal(0, state.Apply(SessionViewerCommand.PageUp).ContentOffset);
        var end = state.Apply(SessionViewerCommand.End);
        // The last page, not the last line: there is nothing below it to reveal.
        Assert.Equal(state.ContentLineCount - state.ViewportRows, end.ContentOffset);
        Assert.Equal(end.ContentOffset, end.Apply(SessionViewerCommand.PageDown).ContentOffset);
    }

    [Fact]
    public void WithContent_ResetsScrollAndReturnsFocusToTheListWhenTheReadFails()
    {
        var scrolled = Loaded(40).Apply(SessionViewerCommand.FocusContent).Apply(SessionViewerCommand.PageDown);

        var failed = scrolled.WithContent(new SessionContentState(SessionContentStatus.Failed, Message: "boom"));

        Assert.Equal(0, failed.ContentOffset);
        Assert.Equal(SessionViewerFocus.List, failed.Focus);
        Assert.Equal("boom", failed.Content.Message);
    }

    [Fact]
    public void WithSearchQuery_JumpsToTheFirstMatch()
    {
        var state = Loaded(40).WithSearchQuery("needle");

        Assert.NotEmpty(state.Matches);
        Assert.Equal(state.Matches[0], state.ContentOffset);
        Assert.Equal(0, state.MatchIndex);
    }

    [Fact]
    public void NextMatch_StepsThroughEveryMatchAndWrapsAround()
    {
        var state = Loaded(40).WithSearchQuery("needle");
        var total = state.Matches.Count;
        Assert.True(total >= 2, "the fixture must contain more than one match");

        var stepped = state;
        for (var step = 1; step < total; step++)
        {
            stepped = stepped.Apply(SessionViewerCommand.NextMatch);
            Assert.Equal(step, stepped.MatchIndex);
            Assert.Equal(state.Matches[step], stepped.ContentOffset);
        }

        var wrapped = stepped.Apply(SessionViewerCommand.NextMatch);
        Assert.Equal(0, wrapped.MatchIndex);
        Assert.Equal(state.Matches[0], wrapped.ContentOffset);
    }

    [Fact]
    public void NextMatch_DoesNothingWithoutMatches()
    {
        var state = Loaded(40).WithSearchQuery("absent-from-the-fixture");

        Assert.Empty(state.Matches);
        Assert.Equal(state.ContentOffset, state.Apply(SessionViewerCommand.NextMatch).ContentOffset);
    }

    [Fact]
    public void ReplaceSnapshot_KeepsTheSelectedSessionAndItsContent()
    {
        var state = Loaded(40).Apply(SessionViewerCommand.MoveDown);
        var selected = state.SelectedSession!;

        // The list is rescanned with an extra newer session, which shifts every index.
        var replaced = state.ReplaceSnapshot(Snapshot(
            codex: [Session(ManagedAgent.Codex, "brand-new", Minutes(99)), .. ThreeSessions().Codex],
            grok: [], claude: []));

        Assert.Equal(selected.SessionId, replaced.SelectedSession?.SessionId);
        Assert.Equal(SessionContentStatus.Loaded, replaced.Content.Status);
    }

    [Fact]
    public void ReplaceSnapshot_DropsTheContentWhenTheSelectedSessionIsGone()
    {
        var state = Loaded(40);

        var replaced = state.ReplaceSnapshot(Snapshot(
            codex: [Session(ManagedAgent.Codex, "unrelated", Minutes(5))], grok: [], claude: []));

        Assert.Equal("unrelated", replaced.SelectedSession?.SessionId);
        Assert.Equal(SessionContentStatus.Empty, replaced.Content.Status);
        Assert.Equal(SessionViewerFocus.List, replaced.Focus);
    }

    [Fact]
    public void Create_SurvivesAnEmptyCatalog()
    {
        var state = SessionViewerState.Create(Snapshot([], [], []));

        Assert.Empty(state.Sessions);
        Assert.Null(state.SelectedSession);
        Assert.Equal(0, state.Apply(SessionViewerCommand.MoveDown).SelectedIndex);
    }

    private static SessionViewerState Loaded(int width)
    {
        var conversation = new PortableConversation(
            ConversationAgent.Codex, "source", "title", @"C:\Repos\Demo",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [
                new PortableTurn(ConversationRole.User, "first needle here"),
                new PortableTurn(ConversationRole.Assistant, "filler line one\nfiller line two"),
                new PortableTurn(ConversationRole.User, "second needle here"),
                new PortableTurn(ConversationRole.Assistant, "closing words")
            ]);
        return SessionViewerState.Create(ThreeSessions(), viewportRows: 4)
            .WithContent(new SessionContentState(
                SessionContentStatus.Loaded, ConversationDocument.Build(conversation, width)));
    }

    private static SessionCatalogSnapshot ThreeSessions() => Snapshot(
        codex:
        [
            Session(ManagedAgent.Codex, "one", Minutes(30)),
            Session(ManagedAgent.Codex, "two", Minutes(20)),
            Session(ManagedAgent.Codex, "three", Minutes(10))
        ],
        grok: [], claude: []);

    private static SessionCatalogSnapshot Snapshot(
        IReadOnlyList<ManagedSession> codex,
        IReadOnlyList<ManagedSession> grok,
        IReadOnlyList<ManagedSession> claude) =>
        new(codex, grok, claude) { ConfiguredAgents = ManagedAgents.All };

    private static DateTimeOffset Minutes(int value) => DateTimeOffset.UnixEpoch.AddMinutes(value);

    private static ManagedSession Session(ManagedAgent agent, string id, DateTimeOffset modified) =>
        new(agent, id, $@"C:\native\{id}", id, modified, IsActive: false, CanRead: true);
}
