using System.Globalization;
using System.Text;
using CodexHistorySync.Core.Management;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CodexHistorySync.Cli.Management;

public interface ISessionManagerInput
{
    ConsoleKeyInfo ReadKey(CancellationToken cancellationToken);
}

public sealed class SpectreSessionManagerInput(IAnsiConsole console) : ISessionManagerInput
{
    private readonly IAnsiConsole console = console ?? throw new ArgumentNullException(nameof(console));

    public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = console.Input.ReadKeyAsync(intercept: true, cancellationToken).GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        return key ?? throw new InvalidOperationException("Interactive console input is unavailable.");
    }
}

public sealed class SpectreSessionManagerView : ISessionManagerView
{
    private const string EnterDisplay = "\u001b[?1049h";
    private const string LeaveDisplay = "\u001b[?1049l";
    private const int LayoutRows = 8;
    private const int MinimumPanelWidth = 28;
    private static readonly Style FocusedBorder = new(Color.Cyan1);
    private static readonly Style UnfocusedBorder = new(Color.Grey);
    private static readonly Style FocusedSelection = new(Color.Black, Color.Cyan1, Decoration.Bold);
    private static readonly Style UnfocusedSelection = new(Color.White, Color.Grey);
    private static readonly Style ErrorStyle = new(Color.Red);
    private static readonly Style InformationStyle = new(Color.Yellow);

    private readonly IAnsiConsole console;
    private readonly ISessionManagerInput input;
    private LiveDisplayContext? liveContext;
    private SessionManagerState? latestState;
    private PendingMessage? pendingMessage;
    private PendingMessage? exitMessage;
    private bool searchEditing;
    private int displayActive;

    public SpectreSessionManagerView(IAnsiConsole console, ISessionManagerInput input)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
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
                if (message is not null)
                    WriteMessage(message.Message, message.IsError);
            }
            finally
            {
                Volatile.Write(ref displayActive, 0);
            }
        }
    }

    public void Render(SessionManagerState state)
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

    private Rows BuildFrame(SessionManagerState state)
    {
        var showSearch = searchEditing || state.SearchQuery.Length > 0;
        var visibleRows = Math.Max(1, console.Profile.Height - LayoutRows - (showSearch ? 1 : 0));
        var displayState = state.SetViewportRows(visibleRows);
        var availableWidth = Math.Max(MinimumPanelWidth * 2 + 1, console.Profile.Width);
        var panelWidth = Math.Max(MinimumPanelWidth, (availableWidth - 1) / 2);

        var panels = new Columns(
        [
            BuildPanel(displayState, ManagedAgent.Codex, panelWidth),
            BuildPanel(displayState, ManagedAgent.Grok, panelWidth)
        ])
        {
            Expand = true
        };
        var message = pendingMessage is { } pending
            ? new Text(pending.Message, pending.IsError ? ErrorStyle : InformationStyle)
            : new Text(string.Empty);
        var search = new Text($"Search: {state.SearchQuery}", InformationStyle);
        var footer = new Text("↑↓ select  ←→ panel  C copy  Del delete  R refresh  / search  Esc clear  Q exit",
            new Style(Color.Grey));
        pendingMessage = null;
        return showSearch
            ? new Rows(panels, search, message, footer)
            : new Rows(panels, message, footer);
    }

    public SessionManagerCommand ReadCommand(CancellationToken cancellationToken)
    {
        while (true)
        {
            var key = input.ReadKey(cancellationToken);
            if (key.KeyChar == '/') return SessionManagerCommand.Search;
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: return SessionManagerCommand.MoveUp;
                case ConsoleKey.DownArrow: return SessionManagerCommand.MoveDown;
                case ConsoleKey.LeftArrow: return SessionManagerCommand.FocusLeft;
                case ConsoleKey.RightArrow: return SessionManagerCommand.FocusRight;
                case ConsoleKey.C: return SessionManagerCommand.Copy;
                case ConsoleKey.Delete: return SessionManagerCommand.Delete;
                case ConsoleKey.R: return SessionManagerCommand.Refresh;
                case ConsoleKey.Q:
                    return SessionManagerCommand.Exit;
                case ConsoleKey.Escape:
                    return latestState?.SearchQuery.Length > 0
                        ? SessionManagerCommand.ClearSearch
                        : SessionManagerCommand.Exit;
            }
        }
    }

    public string ReadSearchQuery(SessionManagerState state, CancellationToken cancellationToken)
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
        pendingMessage = new PendingMessage(
            $"Local only: delete '{session.Title}'? Sync may restore it. [y/N] ",
            false);
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
        var nextMessage = new PendingMessage(message ?? string.Empty, isError);
        if (liveContext is not null)
        {
            if (latestState is null)
                exitMessage = nextMessage;
            else
                pendingMessage = nextMessage;
            return;
        }

        WriteMessage(nextMessage.Message, nextMessage.IsError);
    }

    private static Panel BuildPanel(SessionManagerState state, ManagedAgent agent, int width)
    {
        var focused = state.FocusedAgent == agent;
        var sessions = agent == ManagedAgent.Codex ? state.Snapshot.Codex : state.Snapshot.Grok;
        var offset = state.ViewportOffset(agent);
        var selectedIndex = state.SelectedIndex(agent);
        var table = new Table
        {
            Border = TableBorder.None,
            Expand = true,
            ShowHeaders = true
        };
        var timestampWidth = 16;
        table.AddColumn(new TableColumn("Title")
        {
            Width = Math.Max(4, width - timestampWidth - 6),
            NoWrap = true
        });
        table.AddColumn(new TableColumn("Last modified")
        {
            Width = timestampWidth,
            NoWrap = true
        });

        var visible = sessions.Skip(offset).Take(state.ViewportRows).ToArray();
        if (visible.Length == 0)
        {
            var emptyMessage = state.SearchQuery.Length > 0 ? "No matching sessions" : "None";
            table.AddRow(new Text(emptyMessage, new Style(Color.Grey)), new Text("—", new Style(Color.Grey)));
        }
        else
        {
            for (var index = 0; index < visible.Length; index++)
            {
                var absoluteIndex = offset + index;
                var selected = absoluteIndex == selectedIndex;
                var session = visible[index];
                var style = selected
                    ? focused ? FocusedSelection : UnfocusedSelection
                    : Style.Plain;
                table.AddRow(
                    new Text(FormatTitle(session, width), style),
                    new Text(session.LastModifiedAt.ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), style));
            }
        }

        var name = agent == ManagedAgent.Codex ? "Codex" : "Grok";
        var header = new PanelHeader(focused ? $"[cyan1]{name}[/]" : $"[grey]{name}[/]");
        return new Panel(table)
        {
            Header = header,
            Border = BoxBorder.Rounded,
            BorderStyle = focused ? FocusedBorder : UnfocusedBorder,
            Width = width,
            Expand = false,
            Padding = new Padding(0, 0, 0, 0)
        };
    }

    private static string FormatTitle(ManagedSession session, int panelWidth)
    {
        var marker = (session.IsActive, session.CanRead) switch
        {
            (true, false) => "[AU]",
            (true, true) => "[A] ",
            (false, false) => "[U] ",
            _ => string.Empty
        };
        var maximum = Math.Max(4, panelWidth - 25);
        if (marker.Length >= maximum) return marker[..maximum];
        var availableTitle = maximum - marker.Length;
        var title = NormalizeWhitespace(session.Title);
        if (title.Length == 0) title = session.SessionId;
        if (title.Length > availableTitle)
            title = availableTitle == 1 ? "…" : string.Concat(title.AsSpan(0, availableTitle - 1), "…");
        return marker + title;
    }

    private static string NormalizeWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            builder.Append(character);
            pendingSpace = false;
        }
        return builder.ToString();
    }

    private void WriteMessage(string message, bool isError)
    {
        console.Write(new Text(message, isError ? ErrorStyle : InformationStyle));
        console.WriteLine();
    }

    private sealed record PendingMessage(string Message, bool IsError);
}
