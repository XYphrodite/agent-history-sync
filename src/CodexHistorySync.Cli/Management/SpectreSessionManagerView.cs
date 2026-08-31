using System.Globalization;
using System.Reflection;
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
    private const string EnterDisplay = "\u001b[?1049h\u001b[H";
    private const string LeaveDisplay = "\u001b[?1049l";
    private const int LayoutRows = 12;
    private const int MinimumPanelWidth = 28;
    /// <summary>Lines a second band of panels costs: its own borders and header.</summary>
    private const int BandChrome = 4;
    private static readonly Style FocusedBorder = new(Color.Cyan1);
    private static readonly Style UnfocusedBorder = new(Color.Grey23);
    private static readonly Style FocusedSelection = new(Color.Cyan1, decoration: Decoration.Bold);
    private static readonly Style NormalText = new(Color.Grey82);
    private static readonly Style MutedText = new(Color.Grey58);
    private static readonly Style ActiveText = new(Color.Orange1);
    private static readonly Style UnreadableText = new(Color.Red);
    private static readonly Style ErrorStyle = new(Color.Red);
    private static readonly Style InformationStyle = new(Color.Orange1);
    private static readonly BuildDetails Build = ReadBuildDetails();

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

    /// <summary>
    /// How many panels share one band. Bands are balanced rather than filled: four agents on a
    /// terminal that fits three become two and two, not three and one.
    /// </summary>
    internal static int PanelsPerRow(int consoleWidth, int agentCount)
    {
        if (agentCount <= 1) return 1;
        var fit = Math.Max(1, consoleWidth / MinimumPanelWidth);
        if (fit >= agentCount) return agentCount;
        var bands = (agentCount + fit - 1) / fit;
        return (agentCount + bands - 1) / bands;
    }

    private Rows BuildFrame(SessionManagerState state)
    {
        var showSearch = searchEditing || state.SearchQuery.Length > 0;
        var agents = state.VisibleAgents;
        // Four agents do not fit side by side on an 80-column terminal, so the panels wrap onto a
        // second band rather than each being squeezed below the width a title needs. A terminal
        // wide enough for all of them still gets the single row it always had.
        var perRow = PanelsPerRow(console.Profile.Width, agents.Count);
        var bands = (agents.Count + perRow - 1) / perRow;
        var visibleRows = Math.Max(1,
            (console.Profile.Height - LayoutRows - (showSearch ? 1 : 0) - (bands - 1) * BandChrome) / bands);
        var displayState = state.SetViewportRows(visibleRows);
        var availableWidth = Math.Max(MinimumPanelWidth * perRow + 1, console.Profile.Width);
        var panelWidth = Math.Max(MinimumPanelWidth, (availableWidth - 1) / perRow);

        var columns = Enumerable.Range(0, bands)
            .Select(band => new Columns(agents.Skip(band * perRow).Take(perRow)
                .Select(agent => BuildPanel(displayState, agent, panelWidth)).ToArray())
            {
                Expand = true
            })
            .ToArray();
        IRenderable panels = columns.Length == 1 ? columns[0] : new Rows(columns);
        var brand = new Panel(new Rows(
            new Markup("[cyan1 bold]<>[/] [white bold]agent[/][grey58]-[/][orange1 bold]sync[/]"),
            new Markup(
                $"[grey58]version[/] [white]{Markup.Escape(Build.Version)}[/]  " +
                $"[grey58]commit[/] [cyan1]{Markup.Escape(Build.Commit)}[/]  " +
                $"[grey58]by[/] [orange1]{Markup.Escape(Build.Author)}[/]")))
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
            $"[cyan1 bold]>[/] [grey58]Search:[/] [grey82]{Markup.Escape(state.SearchQuery)}[/]");
        var footer = new Markup(
            "[cyan1]Up/Dn[/] [grey58]move[/]   [cyan1]Lt/Rt[/] [grey58]panel[/]   " +
            "[cyan1]/[/] [grey58]search[/]   [cyan1]C[/] [grey58]copy[/]   " +
            "[cyan1]Del[/] [grey58]delete[/]   [cyan1]R[/] [grey58]refresh[/]   " +
            "[cyan1]Esc[/] [grey58]clear[/]   [cyan1]Q[/] [grey58]exit[/]   " +
            "[orange1]*[/] [grey58]active[/]   [red]![/] [grey58]unreadable[/]");
        pendingMessage = null;
        return showSearch
            ? new Rows(brand, panels, search, message, footer)
            : new Rows(brand, panels, message, footer);
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

    public ManagedAgent? ChooseCopyTarget(
        ManagedSession source,
        IReadOnlyList<ManagedAgent> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0) return null;
        var state = latestState ?? throw new InvalidOperationException("A session state must be rendered before a copy target prompt.");

        var choices = targets.Select((agent, index) => (Agent: agent, Key: (char)('1' + index))).ToArray();
        pendingMessage = new PendingMessage(
            "Copy to: " + string.Join("   ", choices.Select(choice => choice.Key + ") " + AgentName(choice.Agent))) +
            "   Esc) cancel",
            false);
        Render(state);
        try
        {
            while (true)
            {
                var key = input.ReadKey(cancellationToken);
                if (key.Key == ConsoleKey.Escape) return null;
                foreach (var choice in choices)
                    if (key.KeyChar == choice.Key) return choice.Agent;
            }
        }
        finally
        {
            pendingMessage = null;
            Render(state);
        }
    }

    internal static string AgentName(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => "Codex",
        ManagedAgent.Grok => "Grok",
        ManagedAgent.Claude => "Claude",
        _ => agent.ToString()
    };

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
        var sessions = state.Snapshot.For(agent);
        var offset = state.ViewportOffset(agent);
        var selectedIndex = state.SelectedIndex(agent);
        var table = new Table
        {
            Border = TableBorder.None,
            Expand = true,
            ShowHeaders = true
        };
        var timestampWidth = 16;
        table.AddColumn(new TableColumn(new Text("SESSION", MutedText))
        {
            Width = Math.Max(4, width - timestampWidth - 6),
            NoWrap = true
        });
        table.AddColumn(new TableColumn(new Text("UPDATED", MutedText))
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
                var titleStyle = selected && focused
                    ? FocusedSelection
                    : !session.CanRead
                        ? UnreadableText
                        : session.IsActive
                            ? ActiveText
                            : NormalText;
                var timestampStyle = selected && focused ? FocusedSelection : MutedText;
                table.AddRow(
                    new Text(FormatTitle(session, width, selected && focused), titleStyle),
                    new Text(session.LastModifiedAt.ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), timestampStyle));
            }
        }

        var name = AgentName(agent).ToUpperInvariant();
        var header = new PanelHeader(focused ? $"[cyan1 bold]* {name}[/]" : $"[grey58]  {name}[/]");
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

    private static string FormatTitle(ManagedSession session, int panelWidth, bool focusedSelection)
    {
        var marker = (session.IsActive, session.CanRead) switch
        {
            (true, false) => "*! ",
            (true, true) => "* ",
            (false, false) => "! ",
            _ => string.Empty
        };
        var selection = focusedSelection ? "> " : "  ";
        var maximum = Math.Max(4, panelWidth - 25);
        var prefix = selection + marker;
        if (prefix.Length >= maximum) return prefix[..maximum];
        var availableTitle = maximum - prefix.Length;
        var title = NormalizeWhitespace(session.Title);
        if (title.Length == 0) title = session.SessionId;
        if (title.Length > availableTitle)
            title = availableTitle == 1 ? "…" : string.Concat(title.AsSpan(0, availableTitle - 1), "…");
        return prefix + title;
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

    private static BuildDetails ReadBuildDetails()
    {
        var assembly = typeof(SpectreSessionManagerView).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var versionParts = (informationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "unknown")
            .Split('+', 2);
        var revision = versionParts.Length == 2 ? versionParts[1] : string.Empty;
        var commit = revision.Length > 7 ? revision[..7] : revision;
        var author = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        return new BuildDetails(
            versionParts[0],
            string.IsNullOrWhiteSpace(commit) ? "unknown" : commit,
            string.IsNullOrWhiteSpace(author) ? "unknown" : author);
    }

    private void WriteMessage(string message, bool isError)
    {
        console.Write(new Text(message, isError ? ErrorStyle : InformationStyle));
        console.WriteLine();
    }

    private sealed record PendingMessage(string Message, bool IsError);
    private sealed record BuildDetails(string Version, string Commit, string Author);
}
