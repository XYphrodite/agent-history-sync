using System.Text;
using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Management;
using Spectre.Console;

namespace CodexHistorySync.IntegrationTests;

public sealed class SpectreSessionManagerViewTests
{
    public static TheoryData<ConsoleKey, SessionManagerCommand> KeyMappings => new()
    {
        { ConsoleKey.UpArrow, SessionManagerCommand.MoveUp },
        { ConsoleKey.DownArrow, SessionManagerCommand.MoveDown },
        { ConsoleKey.LeftArrow, SessionManagerCommand.FocusLeft },
        { ConsoleKey.RightArrow, SessionManagerCommand.FocusRight },
        { ConsoleKey.C, SessionManagerCommand.Copy },
        { ConsoleKey.Delete, SessionManagerCommand.Delete },
        { ConsoleKey.R, SessionManagerCommand.Refresh },
        { ConsoleKey.Q, SessionManagerCommand.Exit },
        { ConsoleKey.Escape, SessionManagerCommand.Exit }
    };

    [Theory]
    [MemberData(nameof(KeyMappings))]
    public void ReadCommand_maps_supported_console_keys(ConsoleKey key, SessionManagerCommand expected)
    {
        var input = new FakeInput(Key(key));
        var view = new SpectreSessionManagerView(CreateConsole(out _, 80, 24), input);

        var command = view.ReadCommand(CancellationToken.None);

        Assert.Equal(expected, command);
    }

    [Fact]
    public void Render_writes_two_panels_exact_headings_markers_and_footer()
    {
        var console = CreateConsole(out var output, 100, 24);
        var view = new SpectreSessionManagerView(console, new FakeInput());
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "codex", "Codex title", isActive: true)],
            [Session(ManagedAgent.Grok, "grok", "Grok title", canRead: false)]));

        view.Render(state);

        var rendered = output.ToString();
        Assert.Contains("Codex", rendered);
        Assert.Contains("Grok", rendered);
        Assert.Equal(2, Count(rendered, "Title"));
        Assert.Equal(2, Count(rendered, "Last modified"));
        Assert.Contains("[A]", rendered);
        Assert.Contains("[U]", rendered);
        Assert.Contains("↑/↓ select  ←/→ panel  C copy  Del delete  R refresh  Q/Esc exit", rendered);
    }

    [Fact]
    public void Render_uses_distinct_focus_and_selection_styling()
    {
        var snapshot = Snapshot(
            [Session(ManagedAgent.Codex, "one", "Same"), Session(ManagedAgent.Codex, "two", "Same")],
            [Session(ManagedAgent.Grok, "three", "Same")]);
        var first = new SessionManagerState(snapshot);
        var second = first.ApplyNavigation(SessionManagerCommand.MoveDown)
            .ApplyNavigation(SessionManagerCommand.FocusRight);

        var firstConsole = CreateConsole(out var firstOutput, 100, 24, ansi: true);
        var secondConsole = CreateConsole(out var secondOutput, 100, 24, ansi: true);
        new SpectreSessionManagerView(firstConsole, new FakeInput()).Render(first);
        new SpectreSessionManagerView(secondConsole, new FakeInput()).Render(second);

        Assert.NotEqual(firstOutput.ToString(), secondOutput.ToString());
        Assert.Contains("\u001b[", firstOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[", secondOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_scrolls_to_selected_row_using_terminal_height()
    {
        var sessions = Enumerable.Range(0, 8)
            .Select(index => Session(ManagedAgent.Codex, $"id-{index}", $"title-{index}"))
            .ToArray();
        var state = new SessionManagerState(Snapshot(sessions, []));
        for (var index = 0; index < 7; index++) state = state.ApplyNavigation(SessionManagerCommand.MoveDown);
        var console = CreateConsole(out var output, 80, 10);

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        Assert.Contains("title-7", output.ToString());
        Assert.DoesNotContain("title-0", output.ToString());
    }

    [Fact]
    public void Render_handles_empty_lists_and_minimum_dimensions()
    {
        var console = CreateConsole(out var output, 60, 8);

        var exception = Record.Exception(() =>
            new SpectreSessionManagerView(console, new FakeInput()).Render(new SessionManagerState(Snapshot([], []))));

        Assert.Null(exception);
        Assert.Equal(2, Count(output.ToString(), "None"));
    }

    [Fact]
    public void Render_shows_combined_active_and_unreadable_status_at_minimum_width()
    {
        var console = CreateConsole(out var output, 60, 8);
        var session = Session(ManagedAgent.Codex, "both", "Both", isActive: true, canRead: false);

        new SpectreSessionManagerView(console, new FakeInput()).Render(
            new SessionManagerState(Snapshot([session], [])));

        Assert.Contains("[AU]", output.ToString());
    }

    [Fact]
    public void Render_escapes_and_truncates_dynamic_titles_without_mutating_state()
    {
        var title = "[red]" + new string('x', 120) + "[/]";
        var state = new SessionManagerState(Snapshot([Session(ManagedAgent.Codex, "safe", title)], []));
        var console = CreateConsole(out var output, 100, 12);

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        Assert.Contains("[red]", output.ToString());
        Assert.DoesNotContain(title, output.ToString());
        Assert.Equal(title, state.SelectedSession!.Title);
    }

    [Fact]
    public void Confirmation_says_deletion_is_local_only_and_sync_may_restore()
    {
        var console = CreateConsole(out var output, 80, 24);
        var input = new FakeInput(Key(ConsoleKey.Y));
        var view = new SpectreSessionManagerView(console, input);

        var confirmed = view.ConfirmLocalDelete(Session(ManagedAgent.Codex, "safe", "Title [private]"));

        Assert.True(confirmed);
        Assert.Contains("local only", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sync may restore", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Title [private]", output.ToString());
    }

    [Fact]
    public void ShowMessage_escapes_dynamic_markup()
    {
        var console = CreateConsole(out var output, 80, 24);
        var view = new SpectreSessionManagerView(console, new FakeInput());

        view.ShowMessage("[red]private[/]", isError: true);

        Assert.Contains("[red]private[/]", output.ToString());
    }

    [Fact]
    public async Task Composed_loop_keeps_refusal_visible_after_the_next_redraw()
    {
        const string clear = "\u001b[2J\u001b[H";
        var console = CreateConsole(out var output, 80, 24, ansi: true);
        var input = new FakeInput(Key(ConsoleKey.C), Key(ConsoleKey.Q));
        var view = new SpectreSessionManagerView(console, input);
        var snapshot = Snapshot([Session(ManagedAgent.Codex, "active", "Active", isActive: true)], []);
        var application = new SessionManagerApplication(new FixedCatalog(snapshot), new RejectOperations(), view);

        await application.RunAsync(CancellationToken.None);

        var rendered = output.ToString();
        var visibleFrame = rendered[(rendered.LastIndexOf(clear, StringComparison.Ordinal) + clear.Length)..];
        Assert.Contains("Active sessions cannot be copied.", visibleFrame);
    }

    [Fact]
    public async Task Composed_loop_uses_one_live_alternate_screen()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var input = new FakeInput(Key(ConsoleKey.DownArrow), Key(ConsoleKey.RightArrow), Key(ConsoleKey.Q));
        var view = new SpectreSessionManagerView(console, input);
        var snapshot = Snapshot(
            [Session(ManagedAgent.Codex, "codex-one", "Codex one"), Session(ManagedAgent.Codex, "codex-two", "Codex two")],
            [Session(ManagedAgent.Grok, "grok", "Grok")]);
        var application = new SessionManagerApplication(new FixedCatalog(snapshot), new RejectOperations(), view);

        await application.RunAsync(CancellationToken.None);

        var rendered = output.ToString();
        Assert.Equal(1, Count(rendered, "\u001b[?1049h"));
        Assert.Equal(1, Count(rendered, "\u001b[?1049l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25h"));
        Assert.DoesNotContain("\u001b[2J", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Display_session_restores_terminal_after_cancellation()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var view = new SpectreSessionManagerView(console, new CancelingInput());
        var application = new SessionManagerApplication(
            new FixedCatalog(Snapshot([Session(ManagedAgent.Codex, "codex", "Codex")], [])),
            new RejectOperations(),
            view);

        await Assert.ThrowsAsync<OperationCanceledException>(() => application.RunAsync(CancellationToken.None));

        var rendered = output.ToString();
        Assert.Equal(1, Count(rendered, "\u001b[?1049h"));
        Assert.Equal(1, Count(rendered, "\u001b[?1049l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25h"));
    }

    private static SessionCatalogSnapshot Snapshot(
        IReadOnlyList<ManagedSession> codex,
        IReadOnlyList<ManagedSession> grok) => new(codex, grok);

    private static ManagedSession Session(
        ManagedAgent agent,
        string id,
        string title,
        bool isActive = false,
        bool canRead = true) =>
        new(agent, id, $"C:\\sessions\\{id}", title,
            DateTimeOffset.Parse("2026-08-09T12:34:56Z"), isActive, canRead);

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static IAnsiConsole CreateConsole(
        out StringWriter writer,
        int width,
        int height,
        bool ansi = false,
        bool interactive = false)
    {
        writer = new StringWriter();
        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ansi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = interactive ? InteractionSupport.Yes : InteractionSupport.No,
            Out = new FixedConsoleOutput(writer, width, height)
        });
    }

    private static int Count(string value, string part) =>
        value.Split(part, StringSplitOptions.None).Length - 1;

    private sealed class FakeInput(params ConsoleKeyInfo[] keys) : ISessionManagerInput
    {
        private readonly Queue<ConsoleKeyInfo> keys = new(keys);

        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return keys.Dequeue();
        }
    }

    private sealed class CancelingInput : ISessionManagerInput
    {
        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    private sealed class FixedCatalog(SessionCatalogSnapshot snapshot) : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RejectOperations : ILocalSessionOperations
    {
        public Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Controller should reject active sessions before operations.");

        public Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Controller should reject active sessions before operations.");
    }

    private sealed class FixedConsoleOutput(TextWriter writer, int width, int height) : IAnsiConsoleOutput
    {
        public TextWriter Writer { get; } = writer;
        public bool IsTerminal => true;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public void SetEncoding(Encoding encoding) { }
    }
}
