using System.Globalization;
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
        return console.Input.ReadKey(intercept: true)
               ?? throw new InvalidOperationException("Interactive console input is unavailable.");
    }
}

public sealed class SpectreSessionManagerView : ISessionManagerView
{
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
    private PendingMessage? pendingMessage;

    public SpectreSessionManagerView(IAnsiConsole console, ISessionManagerInput input)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        this.input = input ?? throw new ArgumentNullException(nameof(input));
    }

    public void Render(SessionManagerState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var visibleRows = Math.Max(1, console.Profile.Height - LayoutRows);
        var displayState = state.SetViewportRows(visibleRows);
        var availableWidth = Math.Max(MinimumPanelWidth * 2 + 1, console.Profile.Width);
        var panelWidth = Math.Max(MinimumPanelWidth, (availableWidth - 1) / 2);

        console.Write(new ControlCode("\u001b[2J\u001b[H"));
        console.Write(new Columns(
        [
            BuildPanel(displayState, ManagedAgent.Codex, panelWidth),
            BuildPanel(displayState, ManagedAgent.Grok, panelWidth)
        ])
        {
            Expand = true
        });
        console.WriteLine();
        if (pendingMessage is { } message)
        {
            WriteMessage(message.Message, message.IsError);
            pendingMessage = null;
        }
        console.Write(new Text("↑/↓ select  ←/→ panel  C copy  Del delete  R refresh  Q/Esc exit",
            new Style(Color.Grey)));
        console.WriteLine();
    }

    public SessionManagerCommand ReadCommand(CancellationToken cancellationToken)
    {
        while (true)
        {
            var key = input.ReadKey(cancellationToken).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow: return SessionManagerCommand.MoveUp;
                case ConsoleKey.DownArrow: return SessionManagerCommand.MoveDown;
                case ConsoleKey.LeftArrow: return SessionManagerCommand.FocusLeft;
                case ConsoleKey.RightArrow: return SessionManagerCommand.FocusRight;
                case ConsoleKey.C: return SessionManagerCommand.Copy;
                case ConsoleKey.Delete: return SessionManagerCommand.Delete;
                case ConsoleKey.R: return SessionManagerCommand.Refresh;
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    return SessionManagerCommand.Exit;
            }
        }
    }

    public bool ConfirmLocalDelete(ManagedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        console.Write(new Text(
            $"Local only: delete '{session.Title}'? Sync may restore it. [y/N] ",
            InformationStyle));
        var key = input.ReadKey(CancellationToken.None).Key;
        console.WriteLine();
        return key == ConsoleKey.Y;
    }

    public void ShowMessage(string message, bool isError)
    {
        pendingMessage = new PendingMessage(message ?? string.Empty, isError);
        WriteMessage(pendingMessage.Message, pendingMessage.IsError);
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
            table.AddRow(new Text("None", new Style(Color.Grey)), new Text("—", new Style(Color.Grey)));
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
        var title = session.Title ?? string.Empty;
        if (title.Length > availableTitle)
            title = availableTitle == 1 ? "…" : string.Concat(title.AsSpan(0, availableTitle - 1), "…");
        return marker + title;
    }

    private void WriteMessage(string message, bool isError)
    {
        console.Write(new Text(message, isError ? ErrorStyle : InformationStyle));
        console.WriteLine();
    }

    private sealed record PendingMessage(string Message, bool IsError);
}
