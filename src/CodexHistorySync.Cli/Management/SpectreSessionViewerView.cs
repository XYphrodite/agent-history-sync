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
    private const int ChromeRows = 8;
    private const int ListWidth = 46;
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
    private int displayActive;

    public SpectreSessionViewerView(IAnsiConsole console, ISessionManagerInput input)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public int ContentRows => Math.Max(1, console.Profile.Height - ChromeRows);

    public int ContentWidth => Math.Max(ConversationDocument.MinimumWidth, console.Profile.Width - ListWidth - 8);

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
        var message = pendingMessage is { } pending
            ? new Text(pending.Message, pending.IsError ? ErrorStyle : InformationStyle)
            : new Text(string.Empty);
        var search = new Markup(
            $"[cyan1 bold]>[/] [grey58]Find:[/] [grey82]{Markup.Escape(state.SearchQuery)}[/]" +
            (state.Matches.Count == 0
                ? state.SearchQuery.Length == 0 ? string.Empty : "  [red]no matches[/]"
                : $"  [grey58]{state.MatchIndex + 1}/{state.Matches.Count}[/]"));
        var footer = new Markup(
            "[cyan1]Up/Dn[/] [grey58]move[/]   [cyan1]Lt/Rt[/] [grey58]list/text[/]   " +
            "[cyan1]PgUp/PgDn Home/End[/] [grey58]scroll[/]   [cyan1]/[/] [grey58]find[/]   " +
            "[cyan1]N[/] [grey58]next[/]   [cyan1]E[/] [grey58]export[/]   [cyan1]Del[/] [grey58]delete[/]   " +
            "[cyan1]R[/] [grey58]refresh[/]   [cyan1]Q[/] [grey58]exit[/]");
        pendingMessage = null;
        return searchEditing || state.SearchQuery.Length > 0
            ? new Rows(panels, search, message, footer)
            : new Rows(panels, message, footer);
    }

    private Panel BuildList(SessionViewerState state)
    {
        var focused = state.Focus == SessionViewerFocus.List;
        var table = new Table { Border = TableBorder.None, Expand = true, ShowHeaders = true };
        table.AddColumn(new TableColumn(new Text("AGENT", MutedText)) { Width = 7, NoWrap = true });
        table.AddColumn(new TableColumn(new Text("SESSION", MutedText)) { NoWrap = true });
        table.AddColumn(new TableColumn(new Text("UPDATED", MutedText)) { Width = 11, NoWrap = true });

        for (var index = state.ListOffset; index < state.Sessions.Count && index < state.ListOffset + state.ViewportRows; index++)
        {
            var session = state.Sessions[index];
            var selected = index == state.SelectedIndex;
            var style = selected && focused ? FocusedSelection
                : !session.CanRead ? UnreadableText
                : session.IsActive ? ActiveText
                : NormalText;
            table.AddRow(
                new Text(AgentName(session.Agent), selected && focused ? FocusedSelection : MutedText),
                new Text(FormatTitle(session, selected && focused), style),
                new Text(session.LastModifiedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                    selected && focused ? FocusedSelection : MutedText));
        }

        return new Panel(table)
        {
            Header = new PanelHeader(focused ? "[cyan1 bold]* SESSIONS[/]" : "[grey58]  SESSIONS[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = focused ? FocusedBorder : UnfocusedBorder,
            Width = ListWidth,
            Expand = false,
            Padding = new Padding(0, 0, 0, 0)
        };
    }

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
            if (key.KeyChar == '/') return SessionViewerCommand.Search;
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
                    return latestState?.SearchQuery.Length > 0
                        ? SessionViewerCommand.Search
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

    private static string FormatTitle(ManagedSession session, bool focusedSelection)
    {
        var marker = (session.IsActive, session.CanRead) switch
        {
            (true, false) => "*! ",
            (true, true) => "* ",
            (false, false) => "! ",
            _ => string.Empty
        };
        var title = string.IsNullOrWhiteSpace(session.Title) ? session.SessionId : session.Title.Trim();
        return (focusedSelection ? "> " : "  ") + marker + Shorten(title, 22);
    }

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
