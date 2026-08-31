using System.Globalization;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CodexHistorySync.Cli.Management;

public sealed class SpectreSessionViewerView : ISessionViewerView
{
    private const string EnterDisplay = "\u001b[?1049h\u001b[H";
    private const string LeaveDisplay = "\u001b[?1049l";
    // Footer, message line, panel borders — plus the four the brand header now takes.
    private const int ChromeRows = 12;
    private const int ListWidth = 52;
    // Panel borders take two columns and each table column carries one space of padding.
    private const int AgentColumn = 6;
    private const int UpdatedColumn = 11;
    private static readonly Style FocusedBorder = new(Color.Cyan1);
    private static readonly Style UnfocusedBorder = new(Color.Grey23);
    private static readonly Style FocusedSelection = new(Color.Cyan1, decoration: Decoration.Bold);
    private static readonly Style NormalText = new(Color.Grey82);
    private static readonly Style MutedText = new(Color.Grey58);
    private static readonly Style ActiveText = new(Color.Orange1);
    private static readonly Style UnreadableText = new(Color.Red);
    private static readonly Style RoleText = new(Color.Cyan1, decoration: Decoration.Bold);
    private static readonly Style MatchText = new(Color.Black, Color.Yellow);
    private static readonly Style ErrorStyle = new(Color.Red);
    private static readonly Style InformationStyle = new(Color.Orange1);

    private readonly IAnsiConsole console;
    private readonly ISessionManagerInput input;
    private LiveDisplayContext? liveContext;
    private SessionViewerState? latestState;
    private PendingMessage? pendingMessage;
    private PendingMessage? exitMessage;
    private bool searchEditing;
    private bool filterEditing;
    private int displayActive;

    public SpectreSessionViewerView(IAnsiConsole console, ISessionManagerInput input)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public int ContentRows => Math.Max(1, console.Profile.Height - ChromeRows);

    public int ContentWidth => Math.Max(ConversationDocument.MinimumWidth, console.Profile.Width - ListWidth - 8);

    public bool IsInputPending
    {
        get
        {
            try { return console.Input.IsKeyAvailable(); }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // A console that cannot be polled simply never defers a read.
                return false;
            }
        }
    }

    public async Task RunDisplayAsync(Func<CancellationToken, Task> interaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        if (Interlocked.CompareExchange(ref displayActive, 1, 0) != 0)
            throw new InvalidOperationException("A display session is already active.");

        try
        {
            console.Write(new ControlCode(EnterDisplay));
            await console.Live(new Text(string.Empty))
                .AutoClear(true)
                .Overflow(VerticalOverflow.Crop)
                .StartAsync(async context =>
                {
                    liveContext = context;
                    await interaction(cancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        finally
        {
            var message = exitMessage;
            liveContext = null;
            latestState = null;
            pendingMessage = null;
            exitMessage = null;
            try
            {
                console.Write(new ControlCode(LeaveDisplay));
                if (message is not null) WriteMessage(message.Message, message.IsError);
            }
            finally
            {
                Volatile.Write(ref displayActive, 0);
            }
        }
    }

    public void Render(SessionViewerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        latestState = state;
        var frame = BuildFrame(state);
        if (liveContext is { } context)
        {
            context.UpdateTarget(frame);
            context.Refresh();
            return;
        }

        console.Write(frame);
    }

    private Rows BuildFrame(SessionViewerState state)
    {
        var panels = new Columns([BuildList(state), BuildContent(state)]) { Expand = true };
        // The header the manager already carried. Without it the viewer was the one screen that
        // never said which build drew it — exactly what you want to know when it misbehaves.
        var brand = new Panel(new Rows(
            new Markup("[cyan1 bold]<>[/] [white bold]agent[/][grey58]-[/][orange1 bold]sync[/]"),
            new Markup(
                $"[grey58]version[/] [white]{Markup.Escape(CliBuildInfo.Version)}[/]  " +
                $"[grey58]commit[/] [cyan1]{Markup.Escape(CliBuildInfo.Commit)}[/]  " +
                $"[grey58]by[/] [orange1]{Markup.Escape(CliBuildInfo.Author)}[/]")))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = FocusedBorder,
            Padding = new Padding(1, 0, 1, 0),
            Width = 49,
            Expand = false
        };
        var message = pendingMessage is { } pending
            ? new Text(pending.Message, pending.IsError ? ErrorStyle : InformationStyle)
            : new Text(string.Empty);
        var search = new Markup(
            $"[cyan1 bold]>[/] [grey58]Find:[/] [grey82]{Markup.Escape(state.SearchQuery)}[/]" +
            (state.Matches.Count == 0
                ? state.SearchQuery.Length == 0 ? string.Empty : "  [red]no matches[/]"
                : $"  [grey58]{state.MatchIndex + 1}/{state.Matches.Count}[/]"));
        var filter = new Markup(
            $"[cyan1 bold]>[/] [grey58]Filter:[/] [grey82]{Markup.Escape(state.ListFilter)}[/]" +
            (state.ListFilter.Length == 0
                ? string.Empty
                : state.Sessions.Count == 0
                    ? "  [red]no sessions match[/]"
                    : $"  [grey58]{state.Sessions.Count}/{state.AllSessions.Count}[/]"));
        var footer = new Markup(
            "[cyan1]Up/Dn[/] [grey58]move[/]   [cyan1]Lt/Rt[/] [grey58]list/text[/]   " +
            "[cyan1]PgUp/PgDn Home/End[/] [grey58]scroll[/]   " +
            $"[cyan1]/[/] [grey58]{(state.Focus == SessionViewerFocus.Content ? "find" : "filter")}[/]   " +
            "[cyan1]N[/] [grey58]next[/]   [cyan1]E[/] [grey58]export[/]   [cyan1]Del[/] [grey58]delete[/]   " +
            "[cyan1]R[/] [grey58]refresh[/]   [cyan1]Q[/] [grey58]exit[/]");
        pendingMessage = null;
        var rows = new List<IRenderable> { brand, panels };
        if (filterEditing || state.ListFilter.Length > 0) rows.Add(filter);
        if (searchEditing || state.SearchQuery.Length > 0) rows.Add(search);
        rows.Add(message);
        rows.Add(footer);
        return new Rows(rows);
    }

    /// <summary>
    /// Rows are laid out by hand into fixed-width fields rather than by a table. A table sizes
    /// its columns with padding of its own, so a cell that looks like it fits still wraps, and a
    /// wrapped cell doubles the row height, halves how much of the list fits, and leaves the
    /// reading pane ending on a different row than the list.
    /// </summary>
    private Panel BuildList(SessionViewerState state)
    {
        var focused = state.Focus == SessionViewerFocus.List;
        var inner = ListWidth - 4;
        var titleWidth = Math.Max(8, inner - AgentColumn - UpdatedColumn - 2);
        var rows = new List<IRenderable>
        {
            new Text(Row("AGENT", "SESSION", "UPDATED", titleWidth), MutedText)
        };

        for (var index = state.ListOffset;
             index < state.Sessions.Count && index < state.ListOffset + state.ViewportRows;
             index++)
        {
            var session = state.Sessions[index];
            var selected = index == state.SelectedIndex;
            var style = selected && focused ? FocusedSelection
                : !session.CanRead ? UnreadableText
                : session.IsActive ? ActiveText
                : NormalText;
            rows.Add(new Text(
                Row(
                    AgentName(session.Agent),
                    FormatTitle(session, selected && focused, titleWidth),
                    session.LastModifiedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                    titleWidth),
                style));
        }

        while (rows.Count <= state.ViewportRows) rows.Add(new Text(string.Empty));

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader(focused ? "[cyan1 bold]* SESSIONS[/]" : "[grey58]  SESSIONS[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = focused ? FocusedBorder : UnfocusedBorder,
            Width = ListWidth,
            Expand = false,
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    private static string Row(string agent, string title, string updated, int titleWidth) =>
        Fit(agent, AgentColumn).PadRight(AgentColumn) + ' ' +
        Fit(title, titleWidth).PadRight(titleWidth) + ' ' +
        Fit(updated, UpdatedColumn);

    private Panel BuildContent(SessionViewerState state)
    {
        var focused = state.Focus == SessionViewerFocus.Content;
        var rows = new List<IRenderable>();
        var query = state.SearchQuery;

        switch (state.Content.Status)
        {
            case SessionContentStatus.Empty:
                rows.Add(new Text("Select a session.", MutedText));
                break;
            case SessionContentStatus.Loading:
                rows.Add(new Text("loading…", MutedText));
                break;
            case SessionContentStatus.Failed:
                rows.Add(new Text(state.Content.Message ?? "This session could not be read.", UnreadableText));
                break;
            case SessionContentStatus.Loaded:
                var lines = state.Content.Document!.Lines;
                for (var index = state.ContentOffset;
                     index < lines.Count && index < state.ContentOffset + state.ViewportRows;
                     index++)
                {
                    var line = lines[index];
                    var style = line.Kind == ConversationLineKind.RoleHeader ? RoleText
                        : query.Length > 0 && line.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ? MatchText
                        : NormalText;
                    rows.Add(new Text(line.Text, style));
                }
                if (lines.Count == 0) rows.Add(new Text("This session has no conversation text.", MutedText));
                break;
        }

        // One taller than the viewport, matching the list`s column header, so both panels end
        // on the same row instead of one trailing the other.
        while (rows.Count < state.ViewportRows + 1) rows.Add(new Text(string.Empty));

        var header = state.SelectedSession is { } session
            ? $"{(focused ? "[cyan1 bold]* " : "[grey58]  ")}{Markup.Escape(Shorten(session.Title, ContentWidth - 4))}[/]"
            : focused ? "[cyan1 bold]* CONTENT[/]" : "[grey58]  CONTENT[/]";

        return new Panel(new Rows(rows))
        {
            Header = new PanelHeader(header),
            Border = BoxBorder.Rounded,
            BorderStyle = focused ? FocusedBorder : UnfocusedBorder,
            Expand = true,
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    public SessionViewerCommand ReadCommand(CancellationToken cancellationToken)
    {
        while (true)
        {
            var key = input.ReadKey(cancellationToken);
            // One key, two jobs, chosen by where you are: filtering a list of forty sessions and
            // finding a word inside one of them are the same gesture aimed at different panes.
            if (key.KeyChar == '/')
                return latestState?.Focus == SessionViewerFocus.Content
                    ? SessionViewerCommand.Search
                    : SessionViewerCommand.FilterList;
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: return SessionViewerCommand.MoveUp;
                case ConsoleKey.DownArrow: return SessionViewerCommand.MoveDown;
                case ConsoleKey.LeftArrow: return SessionViewerCommand.FocusList;
                case ConsoleKey.RightArrow: return SessionViewerCommand.FocusContent;
                case ConsoleKey.PageUp: return SessionViewerCommand.PageUp;
                case ConsoleKey.PageDown: return SessionViewerCommand.PageDown;
                case ConsoleKey.Home: return SessionViewerCommand.Home;
                case ConsoleKey.End: return SessionViewerCommand.End;
                case ConsoleKey.N: return SessionViewerCommand.NextMatch;
                case ConsoleKey.E: return SessionViewerCommand.Export;
                case ConsoleKey.Delete: return SessionViewerCommand.Delete;
                case ConsoleKey.R: return SessionViewerCommand.Refresh;
                case ConsoleKey.Q: return SessionViewerCommand.Exit;
                case ConsoleKey.Escape:
                    // Escape clears what is narrowing the view before it offers to leave.
                    if (latestState?.SearchQuery.Length > 0) return SessionViewerCommand.Search;
                    return latestState?.ListFilter.Length > 0
                        ? SessionViewerCommand.FilterList
                        : SessionViewerCommand.Exit;
            }
        }
    }

    public string ReadSearchQuery(SessionViewerState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var query = state.SearchQuery;
        searchEditing = true;
        try
        {
            Render(state.WithSearchQuery(query));
            while (true)
            {
                var key = input.ReadKey(cancellationToken);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        return query;
                    case ConsoleKey.Escape:
                        query = string.Empty;
                        Render(state.WithSearchQuery(query));
                        return query;
                    case ConsoleKey.Backspace:
                        if (query.Length > 0) query = query[..^1];
                        break;
                    default:
                        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) query += key.KeyChar;
                        else continue;
                        break;
                }
                Render(state.WithSearchQuery(query));
            }
        }
        finally
        {
            searchEditing = false;
        }
    }

    public string ReadListFilter(SessionViewerState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var query = state.ListFilter;
        filterEditing = true;
        try
        {
            Render(state.WithListFilter(query));
            while (true)
            {
                var key = input.ReadKey(cancellationToken);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        return query;
                    case ConsoleKey.Escape:
                        query = string.Empty;
                        Render(state.WithListFilter(query));
                        return query;
                    case ConsoleKey.Backspace:
                        if (query.Length > 0) query = query[..^1];
                        break;
                    default:
                        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar)) query += key.KeyChar;
                        else continue;
                        break;
                }
                Render(state.WithListFilter(query));
            }
        }
        finally
        {
            filterEditing = false;
        }
    }

    public bool ConfirmLocalDelete(ManagedSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = latestState ?? throw new InvalidOperationException("A session state must be rendered before confirmation.");
        pendingMessage = new PendingMessage($"Local only: delete '{session.Title}'? Sync may restore it. [y/N] ", false);
        Render(state);
        try
        {
            return input.ReadKey(cancellationToken).Key == ConsoleKey.Y;
        }
        finally
        {
            pendingMessage = null;
            Render(state);
        }
    }

    public void ShowMessage(string message, bool isError)
    {
        var next = new PendingMessage(message ?? string.Empty, isError);
        if (liveContext is not null)
        {
            if (latestState is null) exitMessage = next;
            else pendingMessage = next;
            return;
        }

        WriteMessage(next.Message, next.IsError);
    }

    private void WriteMessage(string message, bool isError)
    {
        console.Write(new Text(message, isError ? ErrorStyle : InformationStyle));
        console.WriteLine();
    }

    private static string FormatTitle(ManagedSession session, bool focusedSelection, int width)
    {
        var marker = (session.IsActive, session.CanRead) switch
        {
            (true, false) => "*!",
            (true, true) => "*",
            (false, false) => "!",
            _ => string.Empty
        };
        var prefix = (focusedSelection ? ">" : " ") + marker + " ";
        var title = string.IsNullOrWhiteSpace(session.Title) ? session.SessionId : session.Title;
        return prefix + Shorten(title, Math.Max(1, width - prefix.Length));
    }

    private static string Fit(string value, int width) =>
        value.Length <= width ? value : value[..width];

    private static string Shorten(string value, int maximum)
    {
        var single = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (maximum <= 1) return single.Length == 0 ? string.Empty : "…";
        return single.Length <= maximum ? single : string.Concat(single.AsSpan(0, maximum - 1), "…");
    }

    internal static string AgentName(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => "codex",
        ManagedAgent.Grok => "grok",
        ManagedAgent.Claude => "claude",
        _ => agent.ToString().ToLowerInvariant()
    };

    private sealed record PendingMessage(string Message, bool IsError);
}
