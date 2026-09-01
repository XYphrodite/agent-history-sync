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
    public void ReadCommand_maps_slash_to_filter_while_the_list_has_focus()
    {
        // Filtering forty sessions by title and finding a word inside one of them are the same
        // gesture aimed at different panes, so the key follows the focus.
        var view = new SpectreSessionViewerView(Console(out _, 120, 30), new FakeInput(Key('/', ConsoleKey.Oem2)));
        view.Render(Loaded());

        Assert.Equal(SessionViewerCommand.FilterList, view.ReadCommand(CancellationToken.None));
    }

    [Fact]
    public void ReadCommand_maps_slash_to_find_while_the_text_has_focus()
    {
        var view = new SpectreSessionViewerView(Console(out _, 120, 30), new FakeInput(Key('/', ConsoleKey.Oem2)));
        view.Render(Loaded().Apply(SessionViewerCommand.FocusContent));

        Assert.Equal(SessionViewerCommand.Search, view.ReadCommand(CancellationToken.None));
    }

    [Fact]
    public void ReadCommand_never_defaults_to_find_before_anything_is_rendered()
    {
        // No state means no focus to follow; the list is where the viewer starts.
        var view = new SpectreSessionViewerView(Console(out _, 120, 30), new FakeInput(Key('/', ConsoleKey.Oem2)));

        Assert.Equal(SessionViewerCommand.FilterList, view.ReadCommand(CancellationToken.None));
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
        // Two borders, the column header, the viewport, a message line and the footer — plus the
        // four the brand header takes: its own two borders and the two lines between them.
        Assert.Equal(state.ViewportRows + 9, lines.Length);
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

        // The brand header closes on a line of its own, so the two reading panels are the only
        // ones that may share a closing row \u2014 and they must.
        var bottoms = output.ToString().Split('\n')
            .Where(line => line.Contains('\u2570'))
            .ToArray();
        Assert.Equal(2, bottoms.Length);
        Assert.Equal(1, bottoms[0].Count(character => character == '\u2570'));
        Assert.Equal(2, bottoms[1].Count(character => character == '\u2570'));
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

    [Fact]
    public void Render_shows_the_description_on_the_frame_it_first_exists()
    {
        // Naming a session and then having to press a key to see the description would read as
        // the naming not having worked.
        var console = Console(out var output, 160, 30);
        var view = new SpectreSessionViewerView(console, new FakeInput());

        view.Render(LoadedWithDescription("what this session actually did"));

        Assert.Contains("what this session actually did", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_marks_a_description_made_from_an_older_conversation()
    {
        var console = Console(out var output, 160, 30);
        var view = new SpectreSessionViewerView(console, new FakeInput());

        view.Render(LoadedWithDescription("named before the session grew", stale: true));

        Assert.Contains("(stale)", output.ToString(), StringComparison.Ordinal);
    }

    private static SessionViewerState LoadedWithDescription(string description, bool stale = false)
    {
        var annotation = new CodexHistorySync.Core.Annotations.SessionAnnotation(
            "A title of our own",
            description,
            CodexHistorySync.Core.Annotations.SessionAnnotationSource.Generated,
            "digest-hash",
            "qwen3:8b",
            DateTimeOffset.UnixEpoch);
        var loaded = Loaded();
        var annotated = loaded.AllSessions[0] with { Annotation = annotation };
        return SessionViewerState
            .Create(new SessionCatalogSnapshot([annotated], [], [], []), viewportRows: 10)
            .WithContent(loaded.Content with { AnnotationIsStale = stale });
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
