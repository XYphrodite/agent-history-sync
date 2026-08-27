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
