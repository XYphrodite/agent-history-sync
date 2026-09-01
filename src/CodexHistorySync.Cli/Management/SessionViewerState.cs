using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public enum SessionViewerFocus { List, Content }

/// <summary>How the selected session's content is doing right now.</summary>
public enum SessionContentStatus { Empty, Loading, Loaded, Failed }

public sealed record SessionContentState(
    SessionContentStatus Status,
    ConversationDocument? Document = null,
    string? Message = null,
    // True when the open session has an annotation made from a conversation it has since outgrown.
    bool AnnotationIsStale = false);

public sealed class SessionViewerState
{
    public const int DefaultViewportRows = 20;

    private SessionViewerState(
        IReadOnlyList<ManagedSession> allSessions,
        string listFilter,
        int selectedIndex,
        int listOffset,
        SessionViewerFocus focus,
        int viewportRows,
        SessionContentState content,
        int contentOffset,
        string searchQuery,
        int matchIndex)
    {
        AllSessions = allSessions;
        ListFilter = listFilter ?? string.Empty;
        Sessions = Filter(allSessions, ListFilter);
        ViewportRows = Math.Max(1, viewportRows);
        SelectedIndex = ClampSelection(selectedIndex, Sessions.Count);
        ListOffset = ClampViewport(listOffset, SelectedIndex, ViewportRows, Sessions.Count);
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
        return new SessionViewerState(Flatten(snapshot), string.Empty, 0, 0, SessionViewerFocus.List, viewportRows,
            new SessionContentState(SessionContentStatus.Empty), 0, string.Empty, 0);
    }

    /// <summary>Every session the catalog returned, before the title filter narrows it.</summary>
    public IReadOnlyList<ManagedSession> AllSessions { get; }

    /// <summary>The rows the list shows: <see cref="AllSessions"/> narrowed by <see cref="ListFilter"/>.</summary>
    public IReadOnlyList<ManagedSession> Sessions { get; }

    /// <summary>Title filter for the list. Empty means every session is shown.</summary>
    public string ListFilter { get; }

    public int SelectedIndex { get; }
    public int ListOffset { get; }
    public SessionViewerFocus Focus { get; }
    public int ViewportRows { get; }
    public SessionContentState Content { get; }
    public int ContentOffset { get; }

    /// <summary>Find-within-the-open-session query, which is a different thing from the filter.</summary>
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
        return new SessionViewerState(AllSessions, ListFilter, SelectedIndex, ListOffset, focus, ViewportRows,
            content, 0, SearchQuery, 0);
    }

    public SessionViewerState WithSearchQuery(string? query)
    {
        var state = new SessionViewerState(AllSessions, ListFilter, SelectedIndex, ListOffset, Focus, ViewportRows,
            Content, ContentOffset, query ?? string.Empty, 0);
        return state.Matches.Count == 0 ? state : state.ScrollTo(state.Matches[0]);
    }

    /// <summary>
    /// Narrows the list to sessions whose title matches. The selected session is kept when it
    /// survives the filter, so typing and then clearing the query lands back where it started
    /// rather than at the top of a list of forty.
    /// </summary>
    public SessionViewerState WithListFilter(string? query)
    {
        var selected = SelectedSession;
        var filtered = Filter(AllSessions, query ?? string.Empty);
        var index = selected is null ? 0 : IndexOf(filtered, selected);
        return new SessionViewerState(AllSessions, query ?? string.Empty, Math.Max(0, index), ListOffset, Focus,
            ViewportRows, Content, ContentOffset, SearchQuery, MatchIndex);
    }

    public SessionViewerState SetViewportRows(int rows) =>
        new(AllSessions, ListFilter, SelectedIndex, ListOffset, Focus, rows, Content, ContentOffset, SearchQuery, MatchIndex);

    /// <summary>Keeps the selected session by identity when it survives the rescan.</summary>
    public SessionViewerState ReplaceSnapshot(SessionCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var all = Flatten(snapshot);
        var selected = SelectedSession;
        var index = selected is null ? SelectedIndex : IndexOf(Filter(all, ListFilter), selected);
        var survived = index >= 0;
        return new SessionViewerState(
            all,
            ListFilter,
            survived ? index : Math.Min(SelectedIndex, Math.Max(0, Filter(all, ListFilter).Count - 1)),
            ListOffset,
            survived ? Focus : SessionViewerFocus.List,
            ViewportRows,
            survived ? Content : new SessionContentState(SessionContentStatus.Empty),
            survived ? ContentOffset : 0,
            SearchQuery,
            survived ? MatchIndex : 0);
    }

    private SessionViewerState Move(int delta) => Focus == SessionViewerFocus.List
        ? new SessionViewerState(AllSessions, ListFilter, SelectedIndex + delta, ListOffset, Focus, ViewportRows,
            Content, ContentOffset, SearchQuery, MatchIndex)
        : ScrollBy(delta);

    private SessionViewerState JumpToStart() => Focus == SessionViewerFocus.List
        ? new SessionViewerState(AllSessions, ListFilter, 0, 0, Focus, ViewportRows, Content, ContentOffset,
            SearchQuery, MatchIndex)
        : ScrollTo(0);

    private SessionViewerState JumpToEnd() => Focus == SessionViewerFocus.List
        ? new SessionViewerState(AllSessions, ListFilter, Sessions.Count - 1, ListOffset, Focus, ViewportRows,
            Content, ContentOffset, SearchQuery, MatchIndex)
        : ScrollTo(MaximumContentOffset);

    private SessionViewerState ScrollBy(int delta) => ScrollTo(ContentOffset + delta);

    /// <summary>The last page, not the last line: scrolling past the end shows nothing new.</summary>
    private int MaximumContentOffset => Math.Max(0, ContentLineCount - ViewportRows);

    private SessionViewerState ScrollTo(int line) =>
        new(AllSessions, ListFilter, SelectedIndex, ListOffset, Focus, ViewportRows, Content,
            Math.Clamp(line, 0, MaximumContentOffset), SearchQuery, MatchIndex);

    /// <summary>Steps to the next match and wraps, so repeated presses tour every hit.</summary>
    private SessionViewerState StepMatch()
    {
        if (Matches.Count == 0) return this;
        var next = (MatchIndex + 1) % Matches.Count;
        return new SessionViewerState(AllSessions, ListFilter, SelectedIndex, ListOffset, Focus, ViewportRows,
            Content, Matches[next], SearchQuery, next);
    }

    private SessionViewerState With(SessionViewerFocus focus) =>
        new(AllSessions, ListFilter, SelectedIndex, ListOffset, focus, ViewportRows, Content, ContentOffset,
            SearchQuery, MatchIndex);

    private static IReadOnlyList<ManagedSession> Filter(IReadOnlyList<ManagedSession> sessions, string query) =>
        query.Length == 0
            ? sessions
            : sessions.Where(session => session.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

    private static int IndexOf(IReadOnlyList<ManagedSession> sessions, ManagedSession session)
    {
        for (var index = 0; index < sessions.Count; index++)
            if (sessions[index].Agent == session.Agent &&
                string.Equals(sessions[index].SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }

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
