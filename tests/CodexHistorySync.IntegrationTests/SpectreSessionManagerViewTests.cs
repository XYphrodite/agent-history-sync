using System.Text;
using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Management;
using Spectre.Console;
using Spectre.Console.Rendering;

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
    public void ReadCommand_maps_slash_to_search_and_escape_to_clear_an_active_filter()
    {
        var input = new FakeInput(Key('/', ConsoleKey.Oem2), Key(ConsoleKey.Escape));
        var view = new SpectreSessionManagerView(CreateConsole(out _, 80, 24), input);

        Assert.Equal(SessionManagerCommand.Search, view.ReadCommand(CancellationToken.None));

        view.Render(new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "one", "One")], [])).WithSearchQuery("one"));

        Assert.Equal(SessionManagerCommand.ClearSearch, view.ReadCommand(CancellationToken.None));
    }

    [Fact]
    public void ReadSearchQuery_supports_live_typing_backspace_enter_and_escape_clear()
    {
        var input = new FakeInput(
            Key('a', ConsoleKey.A),
            Key('l', ConsoleKey.L),
            Key(ConsoleKey.Backspace),
            Key('p', ConsoleKey.P),
            Key(ConsoleKey.Enter),
            Key(ConsoleKey.Escape));
        var console = CreateConsole(out var output, 100, 24);
        var view = new SpectreSessionManagerView(console, input);
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "alpha", "Alpha")],
            [Session(ManagedAgent.Grok, "beta", "Beta")]));

        var committed = view.ReadSearchQuery(state, CancellationToken.None);
        var cleared = view.ReadSearchQuery(state.WithSearchQuery(committed), CancellationToken.None);

        Assert.Equal("ap", committed);
        Assert.Equal(string.Empty, cleared);
        Assert.Contains("Search: al", output.ToString());
        Assert.Contains("Search: ap", output.ToString());
    }

    [Fact]
    public void Render_shows_search_query_and_no_matching_sessions_in_both_panels()
    {
        var console = CreateConsole(out var output, 100, 24);
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "one", "Alpha")],
            [Session(ManagedAgent.Grok, "two", "Beta")]))
            .WithSearchQuery("missing");

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        var rendered = output.ToString();
        Assert.Contains("Search: missing", rendered);
        Assert.Equal(2, Count(rendered, "No matching sessions"));
        Assert.Contains("/ search", rendered);
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
        Assert.Contains("CODEX", rendered);
        Assert.Contains("GROK", rendered);
        Assert.Equal(2, Count(rendered, "SESSION"));
        Assert.Equal(2, Count(rendered, "UPDATED"));
        Assert.Contains("*", rendered);
        Assert.Contains("!", rendered);
        Assert.Contains("Up/Dn move", rendered);
        Assert.Contains("Lt/Rt panel", rendered);
        Assert.Contains("/ search", rendered);
        Assert.Contains("Q exit", rendered);
    }

    [Fact]
    public void Render_uses_compact_grok_inspired_visual_language()
    {
        var console = CreateConsole(out var output, 120, 24);
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "codex", "Codex title", isActive: true)],
            [Session(ManagedAgent.Grok, "grok", "Grok title", canRead: false)]));

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        var rendered = output.ToString();
        Assert.Contains("* CODEX", rendered);
        Assert.Contains("GROK", rendered);
        Assert.Equal(2, Count(rendered, "SESSION"));
        Assert.Equal(2, Count(rendered, "UPDATED"));
        Assert.Contains("> * Codex title", rendered);
        Assert.Contains("! Grok title", rendered);
        Assert.Contains("Up/Dn move", rendered);
        Assert.Contains("/ search", rendered);
        Assert.Contains("* active", rendered);
        Assert.Contains("! unreadable", rendered);
        Assert.DoesNotContain("[A]", rendered);
        Assert.DoesNotContain("[U]", rendered);
    }

    [Fact]
    public void Render_places_ascii_logo_above_panels_and_uses_terminal_safe_active_marker()
    {
        var console = CreateConsole(out var output, 120, 24);
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "codex", "Codex title", isActive: true)], []));

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        var rendered = output.ToString();
        var logoTop = rendered.IndexOf('╭');
        var logo = rendered.IndexOf("<> agent-sync", StringComparison.Ordinal);
        var logoBottom = rendered.IndexOf('╰');
        var panel = rendered.IndexOf("* CODEX", StringComparison.Ordinal);
        Assert.True(logoTop >= 0 && logoTop < logo && logo < logoBottom && logoBottom < panel,
            "Expected the compact logo inside its own frame above the panels.");
        Assert.Equal(3, Count(rendered, "╭"));
        Assert.Contains("version 0.6.1", rendered);
        Assert.Matches("commit [0-9a-f]{7}", rendered);
        Assert.Contains("by XYphrodite", rendered);
        Assert.Contains("> * Codex title", rendered);
        Assert.Contains("* active", rendered);
        Assert.DoesNotContain("●", rendered);
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
        Assert.DoesNotContain("title-6", output.ToString());
        Assert.DoesNotContain("title-0", output.ToString());
    }

    [Fact]
    public void Render_collapses_multiline_title_and_keeps_selected_row_in_small_viewport()
    {
        var sessions = Enumerable.Range(0, 8)
            .Select(index => Session(ManagedAgent.Codex, $"id-{index}",
                index == 7 ? "  Selected\r\n\t session   title  " : $"title-{index}"))
            .ToArray();
        var state = new SessionManagerState(Snapshot(sessions, []));
        for (var index = 0; index < 7; index++)
            state = state.ApplyNavigation(SessionManagerCommand.MoveDown);
        var console = CreateConsole(out var output, 80, 10);

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        var rendered = output.ToString();
        Assert.Contains("Selected se…", rendered);
        Assert.DoesNotContain("Selected\r", rendered);
        Assert.DoesNotContain("title-0", rendered);
    }

    [Fact]
    public void Render_uses_session_id_for_whitespace_only_title()
    {
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "fallback-session-id", " \r\n\t ")], []));
        var console = CreateConsole(out var output, 100, 12);

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        Assert.Contains("fallback-session-id", output.ToString());
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

        Assert.Contains("*!", output.ToString());
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
        var session = Session(ManagedAgent.Codex, "safe", "Title [private]");
        view.Render(new SessionManagerState(Snapshot([session], [])));

        var confirmed = view.ConfirmLocalDelete(session, CancellationToken.None);

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
    public async Task Composed_loop_keeps_refusal_visible()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true);
        var input = new RecordingInput(output, Key(ConsoleKey.C), Key(ConsoleKey.Q));
        var view = new SpectreSessionManagerView(console, input);
        var snapshot = Snapshot([Session(ManagedAgent.Codex, "active", "Active", isActive: true)], []);
        var application = new SessionManagerApplication(new FixedCatalog(snapshot), new RejectOperations(), view);

        await application.RunAsync(CancellationToken.None);

        Assert.DoesNotContain("Active sessions cannot be copied.", input.RenderedBeforeReads[0]);
        var subsequentFrame = input.RenderedBeforeReads[1];
        var panels = subsequentFrame.LastIndexOf("UPDATED", StringComparison.Ordinal);
        var refusal = subsequentFrame.LastIndexOf("Active sessions cannot be copied.", StringComparison.Ordinal);
        var footer = subsequentFrame.LastIndexOf("exit", StringComparison.Ordinal);
        Assert.True(panels < refusal && refusal < footer,
            "Expected the refusal inside the subsequent live frame.");
    }

    [Fact]
    public async Task Live_confirmation_uses_frame()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var input = new RecordingInput(output, Key(ConsoleKey.Delete), Key(ConsoleKey.Y), Key(ConsoleKey.Q));
        var view = new SpectreSessionManagerView(console, input);
        var snapshot = Snapshot([Session(ManagedAgent.Codex, "codex", "Codex")], []);
        var application = new SessionManagerApplication(new FixedCatalog(snapshot), new RejectOperations(), view);

        await application.RunAsync(CancellationToken.None);

        var confirmationFrame = input.RenderedBeforeReads[1];
        var panels = confirmationFrame.LastIndexOf("UPDATED", StringComparison.Ordinal);
        var warning = confirmationFrame.LastIndexOf("Local only: delete", StringComparison.Ordinal);
        var footer = confirmationFrame.LastIndexOf("exit", StringComparison.Ordinal);
        Assert.True(panels < warning && warning < footer,
            "Expected the confirmation inside the live frame.");
        var rendered = output.ToString();
        Assert.Contains("Local only: delete", rendered);
        Assert.Contains("Sync may restore it", rendered);
        Assert.DoesNotContain("\u001b[2J", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initial_failure_survives_alternate_screen_exit()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var view = new SpectreSessionManagerView(console, new FakeInput());
        var application = new SessionManagerApplication(new FailingCatalog(), new RejectOperations(), view);

        await application.RunAsync(CancellationToken.None);

        var rendered = output.ToString();
        var leaveDisplay = rendered.LastIndexOf("\u001b[?1049l", StringComparison.Ordinal);
        var message = rendered.LastIndexOf("Session refresh failed.", StringComparison.Ordinal);
        Assert.True(leaveDisplay >= 0 && message > leaveDisplay,
            "Expected the initial refresh failure after leaving the alternate screen.");
        Assert.Equal(1, Count(rendered, "Session refresh failed."));
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
        Assert.Contains("\u001b[?1049h\u001b[H", rendered, StringComparison.Ordinal);
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

    [Fact]
    public async Task Spectre_input_cancellation_unwinds_wait_and_restores_terminal_without_a_key()
    {
        var innerConsole = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var waitingInput = new WaitingAnsiConsoleInput();
        var console = new ConsoleWithInput(innerConsole, waitingInput);
        var view = new SpectreSessionManagerView(console, new SpectreSessionManagerInput(console));
        var application = new SessionManagerApplication(
            new FixedCatalog(Snapshot([Session(ManagedAgent.Codex, "codex", "Codex")], [])),
            new RejectOperations(),
            view);
        using var cancellation = new CancellationTokenSource();

        var run = Task.Run(() => application.RunAsync(cancellation.Token));
        await waitingInput.Waiting.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var stoppedWithoutKey = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1))) == run;
        waitingInput.Release();
        if (!stoppedWithoutKey) await run;

        Assert.True(stoppedWithoutKey, "Expected cancellation to stop the blocking Spectre input without another key.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var rendered = output.ToString();
        Assert.Equal(1, Count(rendered, "\u001b[?1049h"));
        Assert.Equal(1, Count(rendered, "\u001b[?1049l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25h"));
    }

    [Fact]
    public void Spectre_input_pre_cancelled_token_throws_operation_cancelled()
    {
        var console = CreateConsole(out _, 80, 24, ansi: true, interactive: true);
        var input = new SpectreSessionManagerInput(console);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => input.ReadKey(cancellation.Token));
    }

    [Fact]
    public async Task Confirmation_wait_propagates_display_cancellation_without_another_key()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var input = new DeleteThenWaitingInput();
        var view = new SpectreSessionManagerView(console, input);
        var application = new SessionManagerApplication(
            new FixedCatalog(Snapshot([Session(ManagedAgent.Codex, "codex", "Codex")], [])),
            new RejectOperations(),
            view);
        using var cancellation = new CancellationTokenSource();

        var run = Task.Run(() => application.RunAsync(cancellation.Token));
        await input.WaitingForConfirmation.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var stoppedWithoutKey = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1))) == run;
        input.Release();
        if (!stoppedWithoutKey) await run;

        Assert.True(stoppedWithoutKey, "Expected confirmation to observe the display cancellation token.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var rendered = output.ToString();
        Assert.Equal(1, Count(rendered, "\u001b[?1049h"));
        Assert.Equal(1, Count(rendered, "\u001b[?1049l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25h"));
    }

    [Fact]
    public async Task Overlapping_display_session_fails_before_emitting_terminal_codes()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var view = new SpectreSessionManagerView(console, new FakeInput());
        Exception? overlap = null;

        await view.RunDisplayAsync(async cancellationToken =>
        {
            overlap = await Record.ExceptionAsync(() =>
                view.RunDisplayAsync(_ => Task.CompletedTask, cancellationToken));
        }, CancellationToken.None);

        Assert.IsType<InvalidOperationException>(overlap);
        var rendered = output.ToString();
        Assert.Equal(1, Count(rendered, "\u001b[?1049h"));
        Assert.Equal(1, Count(rendered, "\u001b[?1049l"));
    }

    [Fact]
    public async Task Alternate_screen_acquisition_failure_still_attempts_restoration()
    {
        var output = new FailOnceTextWriter("\u001b[?1049h");
        var console = CreateConsole(output, 80, 24, ansi: true, interactive: true);
        var view = new SpectreSessionManagerView(console, new FakeInput());
        var interactionStarted = false;

        await Assert.ThrowsAsync<IOException>(() => view.RunDisplayAsync(_ =>
        {
            interactionStarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.False(interactionStarted);
        Assert.Equal(1, Count(output.ToString(), "\u001b[?1049l"));
    }

    [Fact]
    public async Task Teardown_write_failure_cannot_leak_exit_message_into_next_session()
    {
        var output = new FailOnceTextWriter("\u001b[?1049l");
        var console = CreateConsole(output, 80, 24, ansi: true, interactive: true);
        var view = new SpectreSessionManagerView(console, new FakeInput());

        await Assert.ThrowsAsync<IOException>(() => view.RunDisplayAsync(_ =>
        {
            view.ShowMessage("stale exit message", isError: true);
            return Task.CompletedTask;
        }, CancellationToken.None));
        await view.RunDisplayAsync(_ => Task.CompletedTask, CancellationToken.None);

        Assert.DoesNotContain("stale exit message", output.ToString());
    }

    [Fact]
    public async Task Display_session_restores_terminal_after_unexpected_exception()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var view = new SpectreSessionManagerView(console,
            new ThrowingInput(new InvalidOperationException("Unexpected input failure.")));
        var application = new SessionManagerApplication(
            new FixedCatalog(Snapshot([Session(ManagedAgent.Codex, "codex", "Codex")], [])),
            new RejectOperations(),
            view);

        await Assert.ThrowsAsync<InvalidOperationException>(() => application.RunAsync(CancellationToken.None));

        var rendered = output.ToString();
        Assert.Equal(1, Count(rendered, "\u001b[?1049h"));
        Assert.Equal(1, Count(rendered, "\u001b[?1049l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25l"));
        Assert.Equal(1, Count(rendered, "\u001b[?25h"));
    }

    [Fact]
    public async Task Display_teardown_does_not_leak_pending_message_into_next_session()
    {
        var console = CreateConsole(out var output, 80, 24, ansi: true, interactive: true);
        var view = new SpectreSessionManagerView(console, new FakeInput());
        var state = new SessionManagerState(Snapshot([Session(ManagedAgent.Codex, "codex", "Codex")], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => view.RunDisplayAsync(_ =>
        {
            view.Render(state);
            view.ShowMessage("stale message", isError: true);
            return Task.FromException(new InvalidOperationException("Stop before the next frame."));
        }, CancellationToken.None));
        await view.RunDisplayAsync(_ =>
        {
            view.Render(state);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.DoesNotContain("stale message", output.ToString());
    }

    [Fact]
    public void Render_draws_one_panel_per_configured_agent()
    {
        var console = CreateConsole(out var output, 140, 30);
        var state = new SessionManagerState(ThreeAgentSnapshot(
            [Session(ManagedAgent.Codex, "one", "Codex title")],
            [Session(ManagedAgent.Grok, "two", "Grok title")],
            [Session(ManagedAgent.Claude, "three", "Claude title")]));

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        var rendered = output.ToString();
        Assert.Contains("CODEX", rendered, StringComparison.Ordinal);
        Assert.Contains("GROK", rendered, StringComparison.Ordinal);
        Assert.Contains("CLAUDE", rendered, StringComparison.Ordinal);
        Assert.Contains("Claude title", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_keeps_the_two_panel_layout_without_a_claude_home()
    {
        var console = CreateConsole(out var output, 140, 30);
        var state = new SessionManagerState(Snapshot(
            [Session(ManagedAgent.Codex, "one", "Codex title")],
            [Session(ManagedAgent.Grok, "two", "Grok title")]));

        new SpectreSessionManagerView(console, new FakeInput()).Render(state);

        var rendered = output.ToString();
        Assert.Contains("CODEX", rendered, StringComparison.Ordinal);
        Assert.Contains("GROK", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("CLAUDE", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ChooseCopyTarget_offers_numbered_agents_and_returns_the_pressed_one()
    {
        var console = CreateConsole(out var output, 140, 30);
        var view = new SpectreSessionManagerView(console, new FakeInput(Key('2', ConsoleKey.D2)));
        var source = Session(ManagedAgent.Codex, "one", "Codex title");
        view.Render(new SessionManagerState(ThreeAgentSnapshot([source], [], [])));

        var target = view.ChooseCopyTarget(source, [ManagedAgent.Grok, ManagedAgent.Claude], CancellationToken.None);

        Assert.Equal(ManagedAgent.Claude, target);
        var rendered = output.ToString();
        Assert.Contains("1) Grok", rendered, StringComparison.Ordinal);
        Assert.Contains("2) Claude", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ChooseCopyTarget_returns_null_on_escape()
    {
        var view = new SpectreSessionManagerView(
            CreateConsole(out _, 140, 30), new FakeInput(Key(ConsoleKey.Escape)));
        var source = Session(ManagedAgent.Codex, "one", "Codex title");
        view.Render(new SessionManagerState(ThreeAgentSnapshot([source], [], [])));

        Assert.Null(view.ChooseCopyTarget(source, [ManagedAgent.Grok, ManagedAgent.Claude], CancellationToken.None));
    }

    private static SessionCatalogSnapshot Snapshot(
        IReadOnlyList<ManagedSession> codex,
        IReadOnlyList<ManagedSession> grok) => new(codex, grok);

    private static SessionCatalogSnapshot ThreeAgentSnapshot(
        IReadOnlyList<ManagedSession> codex,
        IReadOnlyList<ManagedSession> grok,
        IReadOnlyList<ManagedSession> claude) =>
        new(codex, grok, claude) { ConfiguredAgents = ManagedAgents.All };

    private static ManagedSession Session(
        ManagedAgent agent,
        string id,
        string title,
        bool isActive = false,
        bool canRead = true) =>
        new(agent, id, $"C:\\sessions\\{id}", title,
            DateTimeOffset.Parse("2026-08-09T12:34:56Z"), isActive, canRead);

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
    private static ConsoleKeyInfo Key(char character, ConsoleKey key) => new(character, key, false, false, false);

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

    private static IAnsiConsole CreateConsole(
        TextWriter writer,
        int width,
        int height,
        bool ansi = false,
        bool interactive = false) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ansi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = interactive ? InteractionSupport.Yes : InteractionSupport.No,
            Out = new FixedConsoleOutput(writer, width, height)
        });

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

    private sealed class RecordingInput(StringWriter output, params ConsoleKeyInfo[] keys) : ISessionManagerInput
    {
        private readonly Queue<ConsoleKeyInfo> keys = new(keys);

        public List<string> RenderedBeforeReads { get; } = [];

        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderedBeforeReads.Add(output.ToString());
            return keys.Dequeue();
        }
    }

    private sealed class CancelingInput : ISessionManagerInput
    {
        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    private sealed class ThrowingInput(Exception exception) : ISessionManagerInput
    {
        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken) => throw exception;
    }

    private sealed class DeleteThenWaitingInput : ISessionManagerInput
    {
        private readonly ManualResetEventSlim release = new();
        private readonly TaskCompletionSource waitingSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;

        public Task WaitingForConfirmation => waitingSource.Task;

        public ConsoleKeyInfo ReadKey(CancellationToken cancellationToken)
        {
            var read = Interlocked.Increment(ref reads);
            if (read == 1) return Key(ConsoleKey.Delete);
            if (read > 2) return Key(ConsoleKey.Q);
            waitingSource.TrySetResult();
            WaitHandle.WaitAny([cancellationToken.WaitHandle, release.WaitHandle]);
            cancellationToken.ThrowIfCancellationRequested();
            return Key(ConsoleKey.N);
        }

        public void Release() => release.Set();
    }

    private sealed class WaitingAnsiConsoleInput : IAnsiConsoleInput
    {
        private readonly ManualResetEventSlim release = new();
        private readonly TaskCompletionSource waitingSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Waiting => waitingSource.Task;

        public bool IsKeyAvailable() => false;

        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            waitingSource.TrySetResult();
            release.Wait();
            return Key(ConsoleKey.Q);
        }

        public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
        {
            waitingSource.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public void Release() => release.Set();
    }

    private sealed class ConsoleWithInput(IAnsiConsole inner, IAnsiConsoleInput input) : IAnsiConsole
    {
        public Profile Profile => inner.Profile;
        public IAnsiConsoleCursor Cursor => inner.Cursor;
        public IAnsiConsoleInput Input => input;
        public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;
        public RenderPipeline Pipeline => inner.Pipeline;
        public void Clear(bool home) => inner.Clear(home);
        public void Write(IRenderable renderable) => inner.Write(renderable);
        public void WriteAnsi(Action<AnsiWriter> action) => inner.WriteAnsi(action);
    }

    private sealed class FailOnceTextWriter(string sequence) : StringWriter
    {
        private bool failed;

        public override void Write(string? value)
        {
            if (!failed && value?.Contains(sequence, StringComparison.Ordinal) == true)
            {
                failed = true;
                throw new IOException("Injected terminal write failure.");
            }

            base.Write(value);
        }

        public override void Write(ReadOnlySpan<char> buffer) => Write(buffer.ToString());
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    }

    private sealed class FixedCatalog(SessionCatalogSnapshot snapshot) : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FailingCatalog : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromException<SessionCatalogSnapshot>(new IOException("Catalog unavailable."));
    }

    private sealed class RejectOperations : ILocalSessionOperations
    {
        public IReadOnlyList<ManagedAgent> AvailableCopyTargets(ManagedSession source) =>
            ManagedAgents.Destinations(source.Agent);

        public Task<string> CopyAsync(ManagedSession source, ManagedAgent target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Controller should reject active sessions before operations.");

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
