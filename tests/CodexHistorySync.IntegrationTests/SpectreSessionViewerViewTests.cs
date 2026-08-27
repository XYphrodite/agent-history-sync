using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using System.Text;
using Spectre.Console;

namespace CodexHistorySync.IntegrationTests;

public sealed class SpectreSessionViewerViewTests
{
    public static TheoryData<ConsoleKey, SessionViewerCommand> KeyMappings => new()
    {
        { ConsoleKey.UpArrow, SessionViewerCommand.MoveUp },
        { ConsoleKey.DownArrow, SessionViewerCommand.MoveDown },
        { ConsoleKey.LeftArrow, SessionViewerCommand.FocusList },
        { ConsoleKey.RightArrow, SessionViewerCommand.FocusContent },
        { ConsoleKey.PageUp, SessionViewerCommand.PageUp },
        { ConsoleKey.PageDown, SessionViewerCommand.PageDown },
        { ConsoleKey.Home, SessionViewerCommand.Home },
        { ConsoleKey.End, SessionViewerCommand.End },
        { ConsoleKey.N, SessionViewerCommand.NextMatch },
        { ConsoleKey.E, SessionViewerCommand.Export },
        { ConsoleKey.Delete, SessionViewerCommand.Delete },
        { ConsoleKey.R, SessionViewerCommand.Refresh },
        { ConsoleKey.Q, SessionViewerCommand.Exit },
        { ConsoleKey.Escape, SessionViewerCommand.Exit }
    };

    [Theory]
    [MemberData(nameof(KeyMappings))]
    public void ReadCommand_maps_supported_keys(ConsoleKey key, SessionViewerCommand expected)
    {
        var view = new SpectreSessionViewerView(Console(out _, 120, 30), new FakeInput(Key(key)));

        Assert.Equal(expected, view.ReadCommand(CancellationToken.None));
    }

    [Fact]
    public void ReadCommand_maps_slash_to_find()
    {
        var view = new SpectreSessionViewerView(Console(out _, 120, 30), new FakeInput(Key('/', ConsoleKey.Oem2)));

        Assert.Equal(SessionViewerCommand.Search, view.ReadCommand(CancellationToken.None));
    }

    [Fact]
    public void Render_shows_the_list_beside_the_conversation()
    {
        var console = Console(out var output, 140, 30);

        new SpectreSessionViewerView(console, new FakeInput()).Render(Loaded());

        var rendered = output.ToString();
        Assert.Contains("SESSIONS", rendered, StringComparison.Ordinal);
        Assert.Contains("codex", rendered, StringComparison.Ordinal);
        Assert.Contains("grok", rendered, StringComparison.Ordinal);
        Assert.Contains("User", rendered, StringComparison.Ordinal);
        Assert.Contains("a question worth reading", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_shows_a_placeholder_while_a_session_loads()
    {
        var console = Console(out var output, 140, 30);
        var state = SessionViewerState.Create(Snapshot())
            .WithContent(new SessionContentState(SessionContentStatus.Loading));

        new SpectreSessionViewerView(console, new FakeInput()).Render(state);

        Assert.Contains("loading", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_explains_a_session_it_could_not_read()
    {
        var console = Console(out var output, 140, 30);
        var state = SessionViewerState.Create(Snapshot())
            .WithContent(new SessionContentState(SessionContentStatus.Failed, Message: "This session could not be read."));

        new SpectreSessionViewerView(console, new FakeInput()).Render(state);

        Assert.Contains("could not be read", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_reports_the_match_position_while_a_search_is_active()
    {
        var console = Console(out var output, 140, 30);

        new SpectreSessionViewerView(console, new FakeInput()).Render(Loaded().WithSearchQuery("question"));

        var rendered = output.ToString();
        Assert.Contains("Find:", rendered, StringComparison.Ordinal);
        Assert.Contains("1/", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_says_when_a_search_matches_nothing()
    {
        var console = Console(out var output, 140, 30);

        new SpectreSessionViewerView(console, new FakeInput()).Render(Loaded().WithSearchQuery("zzz-absent"));

        Assert.Contains("no matches", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ContentRows_and_ContentWidth_follow_the_terminal()
    {
        var narrow = new SpectreSessionViewerView(Console(out _, 80, 24), new FakeInput());
        var wide = new SpectreSessionViewerView(Console(out _, 200, 60), new FakeInput());

        Assert.True(wide.ContentRows > narrow.ContentRows);
        Assert.True(wide.ContentWidth > narrow.ContentWidth);
        Assert.True(narrow.ContentWidth >= ConversationDocument.MinimumWidth);
    }

    [Fact]
    public void ConfirmLocalDelete_asks_and_only_y_confirms()
    {
        var yes = new SpectreSessionViewerView(Console(out _, 140, 30), new FakeInput(Key(ConsoleKey.Y)));
        var no = new SpectreSessionViewerView(Console(out _, 140, 30), new FakeInput(Key(ConsoleKey.N)));
        yes.Render(Loaded());
        no.Render(Loaded());
        var session = Loaded().SelectedSession!;

        Assert.True(yes.ConfirmLocalDelete(session, CancellationToken.None));
        Assert.False(no.ConfirmLocalDelete(session, CancellationToken.None));
    }

    [Fact]
    public void Render_keeps_every_list_row_on_a_single_line()
    {
        // A wrapped cell doubles a row's height, so the frame grows taller than the viewport
        // allows and the reading pane stops aligning with the list.
        var console = Console(out var output, 160, 30);
        var view = new SpectreSessionViewerView(console, new FakeInput());
        var state = SessionViewerState.Create(LongTitles(), viewportRows: 6)
            .WithContent(new SessionContentState(SessionContentStatus.Empty));

        view.Render(state);

        var lines = output.ToString().TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        // Two borders, the column header, the viewport, a message line and the footer.
        Assert.Equal(state.ViewportRows + 5, lines.Length);
    }

    [Fact]
    public void Render_never_writes_past_the_terminal_width()
    {
        var console = Console(out var output, 100, 24);

        new SpectreSessionViewerView(console, new FakeInput())
            .Render(SessionViewerState.Create(LongTitles(), viewportRows: 6));

        foreach (var line in output.ToString().Split('\n'))
            Assert.True(line.TrimEnd().Length <= 100, $"line wider than the terminal: '{line.TrimEnd()}'");
    }

    [Fact]
    public void Render_closes_both_panels_on_the_same_row()
    {
        var console = Console(out var output, 160, 30);

        new SpectreSessionViewerView(console, new FakeInput()).Render(Loaded());

        var bottoms = output.ToString().Split('\n')
            .Where(line => line.Contains('\u2570'))
            .ToArray();
        var single = Assert.Single(bottoms);
        Assert.Equal(2, single.Count(character => character == '\u2570'));
    }

    private static SessionCatalogSnapshot LongTitles()
    {
        var sessions = Enumerable.Range(0, 12)
            .Select(index => new ManagedSession(
                ManagedAgent.Claude,
                $"session-{index}",
                $@"C:\native\session-{index}",
                "a deliberately long session title that cannot fit the list column " + index,
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                IsActive: false,
                CanRead: true))
            .ToArray();
        return new SessionCatalogSnapshot([], [], sessions) { ConfiguredAgents = ManagedAgents.All };
    }

    private static SessionViewerState Loaded()
    {
        var conversation = new PortableConversation(
            ConversationAgent.Codex, "source", "Readable title", @"C:\Repos\Demo",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [
                new PortableTurn(ConversationRole.User, "a question worth reading"),
                new PortableTurn(ConversationRole.Assistant, "an answer worth keeping")
            ]);
        return SessionViewerState.Create(Snapshot(), viewportRows: 10)
            .WithContent(new SessionContentState(
                SessionContentStatus.Loaded, ConversationDocument.Build(conversation, 60)));
    }

    private static SessionCatalogSnapshot Snapshot() => new(
        [Session(ManagedAgent.Codex, "codex-one", 30)],
        [Session(ManagedAgent.Grok, "grok-one", 20)],
        []) { ConfiguredAgents = ManagedAgents.All };

    private static ManagedSession Session(ManagedAgent agent, string id, int minutes) =>
        new(agent, id, $@"C:\native\{id}", id, DateTimeOffset.UnixEpoch.AddMinutes(minutes), false, true);

    [Fact]
    public void IsInputPending_is_false_on_a_console_that_cannot_be_polled()
    {
        Assert.False(new SpectreSessionViewerView(Console(out _, 120, 30), new FakeInput()).IsInputPending);
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Key(char character, ConsoleKey key) => new(character, key, false, false, false);

    private static IAnsiConsole Console(out StringWriter writer, int width, int height)
    {
        writer = new StringWriter();
        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new FixedConsoleOutput(writer, width, height)
        });
    }

    /// <summary>AnsiConsoleOutput reports the real terminal size; the viewer needs a known one.</summary>
    private sealed class FixedConsoleOutput(TextWriter writer, int width, int height) : IAnsiConsoleOutput
    {
        public TextWriter Writer { get; } = writer;
        public bool IsTerminal => true;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public void SetEncoding(Encoding encoding) { }
    }

    private sealed class FakeInput(params ConsoleKeyInfo[] keys) : ISessionManagerInput
    {
        private readonly Queue<ConsoleKeyInfo> keys = new(keys);

        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken) =>
            keys.Count == 0 ? throw new InvalidOperationException("No key scripted.") : keys.Dequeue();
    }
}
