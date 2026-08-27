using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public enum SessionViewerFocus { List, Content }

/// <summary>How the selected session's content is doing right now.</summary>
public enum SessionContentStatus { Empty, Loading, Loaded, Failed }

public sealed record SessionContentState(
    SessionContentStatus Status,
    ConversationDocument? Document = null,
    string? Message = null);

public sealed class SessionViewerState
{
    public const int DefaultViewportRows = 20;

    private SessionViewerState(
        IReadOnlyList<ManagedSession> sessions,
        int selectedIndex,
        int listOffset,
        SessionViewerFocus focus,
        int viewportRows,
        SessionContentState content,
        int contentOffset,
        string searchQuery,
        int matchIndex)
    {
        Sessions = sessions;
        ViewportRows = Math.Max(1, viewportRows);
        SelectedIndex = ClampSelection(selectedIndex, sessions.Count);
        ListOffset = ClampViewport(listOffset, SelectedIndex, ViewportRows, sessions.Count);
        Focus = focus;
        Content = content;
        SearchQuery = searchQuery ?? string.Empty;
        Matches = content.Document?.FindMatches(SearchQuery) ?? [];
        MatchIndex = Matches.Count == 0 ? 0 : Math.Clamp(matchIndex, 0, Matches.Count - 1);
        ContentOffset = ClampViewport(contentOffset, contentOffset, ViewportRows, ContentLineCount);
    }

    public static SessionViewerState Create(SessionCatalogSnapshot snapshot, int viewportRows = DefaultViewportRows)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SessionViewerState(Flatten(snapshot), 0, 0, SessionViewerFocus.List, viewportRows,
            new SessionContentState(SessionContentStatus.Empty), 0, string.Empty, 0);
    }

    public IReadOnlyList<ManagedSession> Sessions { get; }
    public int SelectedIndex { get; }
    public int ListOffset { get; }
    public SessionViewerFocus Focus { get; }
    public int ViewportRows { get; }
    public SessionContentState Content { get; }
    public int ContentOffset { get; }
    public string SearchQuery { get; }
    public IReadOnlyList<int> Matches { get; }
    public int MatchIndex { get; }

    public ManagedSession? SelectedSession =>
        SelectedIndex >= 0 && SelectedIndex < Sessions.Count ? Sessions[SelectedIndex] : null;

    public int ContentLineCount => Content.Document?.Lines.Count ?? 0;

    public SessionViewerState Apply(SessionViewerCommand command) => command switch
    {
        SessionViewerCommand.MoveUp => Move(-1),
        SessionViewerCommand.MoveDown => Move(1),
        SessionViewerCommand.PageUp => Move(-ViewportRows),
        SessionViewerCommand.PageDown => Move(ViewportRows),
        SessionViewerCommand.Home => JumpToStart(),
        SessionViewerCommand.End => JumpToEnd(),
        SessionViewerCommand.FocusList => With(focus: SessionViewerFocus.List),
        SessionViewerCommand.FocusContent => Content.Status == SessionContentStatus.Loaded
            ? With(focus: SessionViewerFocus.Content)
            : this,
        SessionViewerCommand.NextMatch => StepMatch(),
        _ => this
    };

    /// <summary>Selecting a different session invalidates whatever was on the right.</summary>
    public SessionViewerState WithContent(SessionContentState content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var focus = content.Status == SessionContentStatus.Loaded ? Focus : SessionViewerFocus.List;
        return new SessionViewerState(Sessions, SelectedIndex, ListOffset, focus, ViewportRows,
            content, 0, SearchQuery, 0);
    }

    public SessionViewerState WithSearchQuery(string? query)
    {
        var state = new SessionViewerState(Sessions, SelectedIndex, ListOffset, Focus, ViewportRows,
            Content, ContentOffset, query ?? string.Empty, 0);
        return state.Matches.Count == 0 ? state : state.ScrollTo(state.Matches[0]);
    }

    public SessionViewerState SetViewportRows(int rows) =>
        new(Sessions, SelectedIndex, ListOffset, Focus, rows, Content, ContentOffset, SearchQuery, MatchIndex);

    /// <summary>Keeps the selected session by identity when it survives the rescan.</summary>
    public SessionViewerState ReplaceSnapshot(SessionCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sessions = Flatten(snapshot);
        var selected = SelectedSession;
        var index = selected is null
            ? SelectedIndex
            : sessions.ToList().FindIndex(session =>
                session.Agent == selected.Agent &&
                string.Equals(session.SessionId, selected.SessionId, StringComparison.OrdinalIgnoreCase));
        var survived = index >= 0;
        return new SessionViewerState(
            sessions,
            survived ? index : Math.Min(SelectedIndex, Math.Max(0, sessions.Count - 1)),
            ListOffset,
            survived ? Focus : SessionViewerFocus.List,
            ViewportRows,
            survived ? Content : new SessionContentState(SessionContentStatus.Empty),
            survived ? ContentOffset : 0,
            SearchQuery,
            survived ? MatchIndex : 0);
    }

    private SessionViewerState Move(int delta) => Focus == SessionViewerFocus.List
        ? new SessionViewerState(Sessions, SelectedIndex + delta, ListOffset, Focus, ViewportRows,
            Content, ContentOffset, SearchQuery, MatchIndex)
        : ScrollBy(delta);

    private SessionViewerState JumpToStart() => Focus == SessionViewerFocus.List
        ? new SessionViewerState(Sessions, 0, 0, Focus, ViewportRows, Content, ContentOffset, SearchQuery, MatchIndex)
        : ScrollTo(0);

    private SessionViewerState JumpToEnd() => Focus == SessionViewerFocus.List
        ? new SessionViewerState(Sessions, Sessions.Count - 1, ListOffset, Focus, ViewportRows,
            Content, ContentOffset, SearchQuery, MatchIndex)
        : ScrollTo(MaximumContentOffset);

    private SessionViewerState ScrollBy(int delta) => ScrollTo(ContentOffset + delta);

    /// <summary>The last page, not the last line: scrolling past the end shows nothing new.</summary>
    private int MaximumContentOffset => Math.Max(0, ContentLineCount - ViewportRows);

    private SessionViewerState ScrollTo(int line) =>
        new(Sessions, SelectedIndex, ListOffset, Focus, ViewportRows, Content,
            Math.Clamp(line, 0, MaximumContentOffset), SearchQuery, MatchIndex);

    /// <summary>Steps to the next match and wraps, so repeated presses tour every hit.</summary>
    private SessionViewerState StepMatch()
    {
        if (Matches.Count == 0) return this;
        var next = (MatchIndex + 1) % Matches.Count;
        return new SessionViewerState(Sessions, SelectedIndex, ListOffset, Focus, ViewportRows,
            Content, Matches[next], SearchQuery, next);
    }

    private SessionViewerState With(SessionViewerFocus focus) =>
        new(Sessions, SelectedIndex, ListOffset, focus, ViewportRows, Content, ContentOffset, SearchQuery, MatchIndex);

    /// <summary>One list across agents, newest first; the agent is a column, not an axis (design D1).</summary>
    private static IReadOnlyList<ManagedSession> Flatten(SessionCatalogSnapshot snapshot) =>
        snapshot.ConfiguredAgents
            .SelectMany(snapshot.For)
            .OrderByDescending(session => session.LastModifiedAt)
            .ThenBy(session => session.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
