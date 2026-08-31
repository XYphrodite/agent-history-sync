using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.IntegrationTests;

public sealed class SessionViewerApplicationTests
{
    [Fact]
    public async Task The_selected_session_is_loaded_and_rendered()
    {
        var view = new ScriptedView(SessionViewerCommand.Exit);
        var reader = new RecordingReader();

        await Run(view, reader);

        var last = view.RenderedStates.Last();
        Assert.Equal(SessionContentStatus.Loaded, last.Content.Status);
        Assert.Equal(["newest"], reader.Read.Select(session => session.SessionId));
        Assert.Contains(last.Content.Document!.Lines, line => line.Text == "answer for newest");
    }

    [Fact]
    public async Task A_loading_frame_is_shown_before_the_read_lands()
    {
        var view = new ScriptedView(SessionViewerCommand.Exit);

        await Run(view, new RecordingReader());

        Assert.Contains(view.RenderedStates, state => state.Content.Status == SessionContentStatus.Loading);
    }

    [Fact]
    public async Task Moving_the_selection_loads_the_newly_selected_session()
    {
        var view = new ScriptedView(SessionViewerCommand.MoveDown, SessionViewerCommand.Exit);
        var reader = new RecordingReader();

        await Run(view, reader);

        Assert.Equal(["newest", "middle"], reader.Read.Select(session => session.SessionId));
    }

    [Fact]
    public async Task Returning_to_a_session_reuses_the_cached_read()
    {
        var view = new ScriptedView(
            SessionViewerCommand.MoveDown, SessionViewerCommand.MoveUp, SessionViewerCommand.Exit);
        var reader = new RecordingReader();

        await Run(view, reader);

        // newest, middle, then newest again from cache rather than a third read.
        Assert.Equal(["newest", "middle"], reader.Read.Select(session => session.SessionId));
    }

    [Fact]
    public async Task Sessions_passed_through_while_keys_are_still_queued_are_never_read()
    {
        // Holding a movement key would otherwise read one whole session per row; reading is the
        // expensive half, measured at 79 ms median and 394 ms worst on real sessions.
        var view = new ScriptedView(
            SessionViewerCommand.MoveDown, SessionViewerCommand.MoveDown, SessionViewerCommand.Exit)
        {
            PendingInputTurns = 3
        };
        var reader = new RecordingReader();

        await Run(view, reader);

        Assert.Empty(reader.Read);
        Assert.All(view.RenderedStates, state => Assert.NotEqual(SessionContentStatus.Loaded, state.Content.Status));
    }

    [Fact]
    public async Task The_session_the_selection_settles_on_is_the_one_that_is_read()
    {
        // Two movements arrive together, then input goes quiet: only the destination is read.
        var view = new ScriptedView(
            SessionViewerCommand.MoveDown, SessionViewerCommand.MoveDown, SessionViewerCommand.Exit)
        {
            PendingInputTurns = 2
        };
        var reader = new RecordingReader();

        await Run(view, reader);

        Assert.Equal(["oldest"], reader.Read.Select(session => session.SessionId));
    }

    [Fact]
    public async Task A_session_that_cannot_be_read_explains_itself_and_leaves_the_list_usable()
    {
        var view = new ScriptedView(SessionViewerCommand.MoveDown, SessionViewerCommand.Exit);
        var reader = new RecordingReader { FailFor = "middle" };

        await Run(view, reader);

        var last = view.RenderedStates.Last();
        Assert.Equal(SessionContentStatus.Failed, last.Content.Status);
        Assert.Equal("This session could not be read.", last.Content.Message);
        Assert.Equal(SessionViewerFocus.List, last.Focus);
        Assert.Equal("middle", last.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task Export_reports_the_path_it_wrote()
    {
        var view = new ScriptedView(SessionViewerCommand.Export, SessionViewerCommand.Exit);
        var exporter = new RecordingExporter { Path = @"C:\Documents\agent-sync\codex-newest.md" };

        await Run(view, new RecordingReader(), exporter);

        Assert.Equal("newest", Assert.Single(exporter.Exported).SessionId);
        Assert.Contains(view.Messages, message =>
            message.Message == @"Exported to C:\Documents\agent-sync\codex-newest.md" && !message.IsError);
    }

    [Fact]
    public async Task Export_is_refused_while_the_session_is_unreadable()
    {
        var view = new ScriptedView(SessionViewerCommand.Export, SessionViewerCommand.Exit);
        var exporter = new RecordingExporter();

        await Run(view, new RecordingReader { FailFor = "newest" }, exporter);

        Assert.Empty(exporter.Exported);
        Assert.Contains(view.Messages, message => message.Message == "Open a session before exporting it." && message.IsError);
    }

    [Fact]
    public async Task A_confirmed_delete_removes_the_row()
    {
        var view = new ScriptedView(SessionViewerCommand.Delete, SessionViewerCommand.Exit);
        view.DeleteConfirmations.Enqueue(true);
        var catalog = new MutableCatalog(Snapshot());
        var operations = new RecordingOperations(catalog);

        await Run(view, new RecordingReader(), new RecordingExporter(), catalog, operations);

        Assert.Equal("newest", Assert.Single(operations.Deleted).SessionId);
        Assert.DoesNotContain(view.RenderedStates.Last().Sessions, session => session.SessionId == "newest");
        Assert.Contains(view.Messages, message => message.Message == "Local deletion may be restored by sync.");
    }

    [Fact]
    public async Task An_unconfirmed_delete_changes_nothing()
    {
        var view = new ScriptedView(SessionViewerCommand.Delete, SessionViewerCommand.Exit);
        view.DeleteConfirmations.Enqueue(false);
        var catalog = new MutableCatalog(Snapshot());
        var operations = new RecordingOperations(catalog);

        await Run(view, new RecordingReader(), new RecordingExporter(), catalog, operations);

        Assert.Empty(operations.Deleted);
        Assert.Contains(view.RenderedStates.Last().Sessions, session => session.SessionId == "newest");
    }

    [Fact]
    public async Task An_active_session_is_refused_before_the_confirmation()
    {
        var catalog = new MutableCatalog(Snapshot(activeNewest: true));
        var view = new ScriptedView(SessionViewerCommand.Delete, SessionViewerCommand.Exit);
        var operations = new RecordingOperations(catalog);

        await Run(view, new RecordingReader(), new RecordingExporter(), catalog, operations);

        Assert.Empty(operations.Deleted);
        Assert.Empty(view.DeleteConfirmations);
        Assert.Contains(view.Messages, message => message.Message == "Active sessions cannot be deleted." && message.IsError);
    }

    [Fact]
    public async Task A_failing_catalog_reports_once_and_stops()
    {
        var view = new ScriptedView(SessionViewerCommand.Exit);

        await new SessionViewerApplication(
            new ThrowingCatalog(), new RecordingReader(), new RecordingExporter(),
            new RecordingOperations(new MutableCatalog(Snapshot())), view).RunAsync(CancellationToken.None);

        Assert.Contains(view.Messages, message => message.Message == "Session refresh failed." && message.IsError);
        Assert.Empty(view.RenderedStates);
    }

    private static Task Run(
        ScriptedView view,
        RecordingReader reader,
        RecordingExporter? exporter = null,
        MutableCatalog? catalog = null,
        RecordingOperations? operations = null)
    {
        catalog ??= new MutableCatalog(Snapshot());
        return new SessionViewerApplication(
            catalog, reader, exporter ?? new RecordingExporter(),
            operations ?? new RecordingOperations(catalog), view).RunAsync(CancellationToken.None);
    }

    private static SessionCatalogSnapshot Snapshot(bool activeNewest = false) => new(
        [Session(ManagedAgent.Codex, "newest", 30, activeNewest), Session(ManagedAgent.Codex, "middle", 20)],
        [Session(ManagedAgent.Grok, "oldest", 10)],
        []) { ConfiguredAgents = ManagedAgents.All };

    private static ManagedSession Session(ManagedAgent agent, string id, int minutes, bool isActive = false) =>
        new(agent, id, $@"C:\native\{id}", id, DateTimeOffset.UnixEpoch.AddMinutes(minutes), isActive, CanRead: true);

    private sealed class ScriptedView(params SessionViewerCommand[] commands) : ISessionViewerView
    {
        private readonly Queue<SessionViewerCommand> commands = new(commands);

        public Queue<bool> DeleteConfirmations { get; } = new();
        public Queue<string> SearchQueries { get; } = new();
        public List<SessionViewerState> RenderedStates { get; } = [];
        public List<(string Message, bool IsError)> Messages { get; } = [];

        public int ContentRows => 10;
        public int ContentWidth => 40;

        /// <summary>How many more loop turns should report a keystroke already waiting.</summary>
        public int PendingInputTurns { get; set; }

        public bool IsInputPending => PendingInputTurns-- > 0;

        public Task RunDisplayAsync(Func<CancellationToken, Task> interaction, CancellationToken cancellationToken) =>
            interaction(cancellationToken);

        public void Render(SessionViewerState state) => RenderedStates.Add(state);

        public SessionViewerCommand ReadCommand(CancellationToken cancellationToken) => commands.Dequeue();

        public string ReadSearchQuery(SessionViewerState state, CancellationToken cancellationToken) =>
            SearchQueries.Dequeue();

        public Queue<string> ListFilters { get; } = new();

        public string ReadListFilter(SessionViewerState state, CancellationToken cancellationToken) =>
            ListFilters.Dequeue();

        public bool ConfirmLocalDelete(ManagedSession session, CancellationToken cancellationToken) =>
            DeleteConfirmations.Dequeue();

        public void ShowMessage(string message, bool isError) => Messages.Add((message, isError));
    }

    private sealed class RecordingReader : ISessionContentReader
    {
        public List<ManagedSession> Read { get; } = [];
        public string? FailFor { get; set; }

        public Task<PortableConversation> ReadAsync(ManagedSession session, CancellationToken cancellationToken)
        {
            Read.Add(session);
            if (FailFor == session.SessionId) throw new InvalidDataException("broken");
            return Task.FromResult(new PortableConversation(
                ConversationAgent.Codex, session.SessionId, "title " + session.SessionId, @"C:\Repos\Demo",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                [
                    new PortableTurn(ConversationRole.User, "question for " + session.SessionId),
                    new PortableTurn(ConversationRole.Assistant, "answer for " + session.SessionId)
                ]));
        }
    }

    private sealed class RecordingExporter : ISessionExporter
    {
        public List<ManagedSession> Exported { get; } = [];
        public string Path { get; set; } = @"C:\exported.md";

        public Task<string> ExportAsync(ManagedSession session, PortableConversation conversation, CancellationToken cancellationToken)
        {
            Exported.Add(session);
            return Task.FromResult(Path);
        }
    }

    private sealed class MutableCatalog(SessionCatalogSnapshot snapshot) : ILocalSessionCatalog
    {
        public SessionCatalogSnapshot Snapshot { get; set; } = snapshot;

        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
    }

    private sealed class ThrowingCatalog : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromException<SessionCatalogSnapshot>(new IOException("catalog unavailable"));
    }

    private sealed class RecordingOperations(MutableCatalog catalog) : ILocalSessionOperations
    {
        public List<ManagedSession> Deleted { get; } = [];

        public IReadOnlyList<ManagedAgent> AvailableCopyTargets(ManagedSession source) =>
            ManagedAgents.Destinations(source.Agent);

        public Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The viewer does not copy.");

        public Task<string> CopyAsync(ManagedSession source, ManagedAgent target, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The viewer does not copy.");

        public Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken)
        {
            Deleted.Add(source);
            catalog.Snapshot = catalog.Snapshot with
            {
                Codex = catalog.Snapshot.Codex.Where(session => session.SessionId != source.SessionId).ToArray()
            };
            return Task.CompletedTask;
        }
    }
}
