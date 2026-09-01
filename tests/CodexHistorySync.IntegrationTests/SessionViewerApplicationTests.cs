using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Annotations;
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

    [Fact]
    public async Task Naming_a_session_stores_what_the_model_answered()
    {
        var view = new ScriptedView(SessionViewerCommand.GenerateAnnotation, SessionViewerCommand.Exit);
        var annotations = new RecordingAnnotations();
        var suggester = new ScriptedSuggester
        {
            Draft = new SessionAnnotationDraft("Event log stopped", "It came back once the service did.", "qwen3:8b")
        };

        await Run(view, new RecordingReader(), annotations: annotations, suggester: suggester);

        var saved = Assert.Single(annotations.Saved);
        Assert.Equal(new SessionAnnotationKey(ManagedAgent.Codex, "newest"), saved.Key);
        Assert.Equal("Event log stopped", saved.Value.Title);
        Assert.Equal("It came back once the service did.", saved.Value.Description);
        Assert.Equal(SessionAnnotationSource.Generated, saved.Value.Source);
        Assert.Equal("qwen3:8b", saved.Value.Model);
        Assert.Equal(SessionDigest.Build(RecordingReader.Conversation("newest")).Hash, saved.Value.DigestHash);
    }

    [Fact]
    public async Task Naming_sends_the_digest_of_the_open_session()
    {
        var view = new ScriptedView(SessionViewerCommand.GenerateAnnotation, SessionViewerCommand.Exit);
        var suggester = new ScriptedSuggester { Draft = new SessionAnnotationDraft("T", "D", "m") };

        await Run(view, new RecordingReader(), annotations: new RecordingAnnotations(), suggester: suggester);

        Assert.Equal(1, suggester.Calls);
        Assert.Contains("question for newest", suggester.LastDigest!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Naming_says_so_when_titling_is_not_configured()
    {
        var view = new ScriptedView(SessionViewerCommand.GenerateAnnotation, SessionViewerCommand.Exit);
        var annotations = new RecordingAnnotations();

        await Run(view, new RecordingReader(), annotations: annotations,
            rejection: "'8.8.8.8' is a public address.");

        Assert.Empty(annotations.Saved);
        var message = Assert.Single(view.Messages, entry => entry.IsError);
        Assert.Contains("not configured", message.Message, StringComparison.Ordinal);
        Assert.Contains("8.8.8.8", message.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Naming_reports_an_endpoint_that_answered_nothing()
    {
        var view = new ScriptedView(SessionViewerCommand.GenerateAnnotation, SessionViewerCommand.Exit);
        var annotations = new RecordingAnnotations();

        await Run(view, new RecordingReader(), annotations: annotations,
            suggester: new ScriptedSuggester { Draft = null });

        Assert.Empty(annotations.Saved);
        Assert.Contains(view.Messages, entry => entry.IsError);
    }

    [Fact]
    public async Task Naming_asks_before_it_replaces_a_title_that_was_typed_by_hand()
    {
        var catalog = new MutableCatalog(SnapshotAnnotated(new SessionAnnotation(
            "Typed by hand", "Mine.", SessionAnnotationSource.Edited, "hash", null, DateTimeOffset.UnixEpoch)));
        var view = new ScriptedView(SessionViewerCommand.GenerateAnnotation, SessionViewerCommand.Exit);
        view.AnnotationOverwrites.Enqueue(false);
        var annotations = new RecordingAnnotations();
        var suggester = new ScriptedSuggester { Draft = new SessionAnnotationDraft("T", "D", "m") };

        await Run(view, new RecordingReader(), catalog: catalog, annotations: annotations, suggester: suggester);

        Assert.Empty(annotations.Saved);
        Assert.Equal(0, suggester.Calls);
    }

    [Fact]
    public async Task Naming_replaces_a_generated_title_without_asking()
    {
        var catalog = new MutableCatalog(SnapshotAnnotated(new SessionAnnotation(
            "Named by the model", "Its own.", SessionAnnotationSource.Generated, "hash", "m", DateTimeOffset.UnixEpoch)));
        var view = new ScriptedView(SessionViewerCommand.GenerateAnnotation, SessionViewerCommand.Exit);
        var annotations = new RecordingAnnotations();

        await Run(view, new RecordingReader(), catalog: catalog, annotations: annotations,
            suggester: new ScriptedSuggester { Draft = new SessionAnnotationDraft("Newer", "D", "m") });

        Assert.Equal("Newer", Assert.Single(annotations.Saved).Value.Title);
        Assert.Empty(view.AnnotationOverwrites);
    }

    [Fact]
    public async Task Naming_is_abandoned_when_a_keystroke_is_already_waiting()
    {
        // The screen stays the user's: a model that thinks for half a minute never holds a key.
        var view = new ScriptedView(SessionViewerCommand.GenerateAnnotation, SessionViewerCommand.Exit)
        {
            PendingInputTurns = 100
        };
        var annotations = new RecordingAnnotations();

        await Run(view, new RecordingReader(), annotations: annotations, suggester: new ScriptedSuggester
        {
            Draft = new SessionAnnotationDraft("Too late", "D", "m"),
            DelayMilliseconds = 30_000
        });

        Assert.Empty(annotations.Saved);
    }

    [Fact]
    public async Task Editing_stores_what_was_typed()
    {
        var view = new ScriptedView(SessionViewerCommand.EditAnnotation, SessionViewerCommand.Exit);
        view.AnnotationEdits.Enqueue(new SessionAnnotationEdit("Typed title", "Typed description"));
        var annotations = new RecordingAnnotations();

        await Run(view, new RecordingReader(), annotations: annotations);

        var saved = Assert.Single(annotations.Saved);
        Assert.Equal("Typed title", saved.Value.Title);
        Assert.Equal("Typed description", saved.Value.Description);
        Assert.Equal(SessionAnnotationSource.Edited, saved.Value.Source);
    }

    [Fact]
    public async Task Editing_needs_no_endpoint()
    {
        // Typing a title is not asking a model: it works on a machine with no titling configured.
        var view = new ScriptedView(SessionViewerCommand.EditAnnotation, SessionViewerCommand.Exit);
        view.AnnotationEdits.Enqueue(new SessionAnnotationEdit("Typed", null));
        var annotations = new RecordingAnnotations();

        await Run(view, new RecordingReader(), annotations: annotations, rejection: "no endpoint");

        Assert.Single(annotations.Saved);
    }

    [Fact]
    public async Task Abandoning_an_edit_stores_nothing()
    {
        var view = new ScriptedView(SessionViewerCommand.EditAnnotation, SessionViewerCommand.Exit);
        view.AnnotationEdits.Enqueue(null);
        var annotations = new RecordingAnnotations();

        await Run(view, new RecordingReader(), annotations: annotations);

        Assert.Empty(annotations.Saved);
    }

    [Fact]
    public async Task An_annotation_made_from_an_older_conversation_is_reported_as_stale()
    {
        var catalog = new MutableCatalog(SnapshotAnnotated(new SessionAnnotation(
            "Named before the session grew", "d", SessionAnnotationSource.Generated,
            "a-hash-from-earlier", "m", DateTimeOffset.UnixEpoch)));
        var view = new ScriptedView(SessionViewerCommand.Exit);

        await Run(view, new RecordingReader(), catalog: catalog);

        Assert.True(view.RenderedStates.Last().Content.AnnotationIsStale);
    }

    [Fact]
    public async Task An_annotation_made_from_this_conversation_is_not_stale()
    {
        var catalog = new MutableCatalog(SnapshotAnnotated(new SessionAnnotation(
            "Named just now", "d", SessionAnnotationSource.Generated,
            SessionDigest.Build(RecordingReader.Conversation("newest")).Hash, "m", DateTimeOffset.UnixEpoch)));
        var view = new ScriptedView(SessionViewerCommand.Exit);

        await Run(view, new RecordingReader(), catalog: catalog);

        Assert.False(view.RenderedStates.Last().Content.AnnotationIsStale);
    }

    private static Task Run(
        ScriptedView view,
        RecordingReader reader,
        RecordingExporter? exporter = null,
        MutableCatalog? catalog = null,
        RecordingOperations? operations = null,
        RecordingAnnotations? annotations = null,
        ISessionTitleSuggester? suggester = null,
        string? rejection = null)
    {
        catalog ??= new MutableCatalog(Snapshot());
        return new SessionViewerApplication(
            catalog, reader, exporter ?? new RecordingExporter(),
            operations ?? new RecordingOperations(catalog), view,
            annotations, suggester, rejection).RunAsync(CancellationToken.None);
    }

    private static SessionCatalogSnapshot SnapshotAnnotated(SessionAnnotation annotation) => new(
        [
            Session(ManagedAgent.Codex, "newest", 30) with
            {
                TitleSource = ManagedTitleSource.Fallback, Annotation = annotation
            },
            Session(ManagedAgent.Codex, "middle", 20)
        ],
        [Session(ManagedAgent.Grok, "oldest", 10)],
        []) { ConfiguredAgents = ManagedAgents.All };

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

        public Queue<SessionAnnotationEdit?> AnnotationEdits { get; } = new();

        public Queue<bool> AnnotationOverwrites { get; } = new();

        public SessionAnnotationEdit? ReadAnnotation(
            ManagedSession session,
            SessionAnnotation? current,
            CancellationToken cancellationToken) => AnnotationEdits.Dequeue();

        public bool ConfirmAnnotationOverwrite(ManagedSession session, CancellationToken cancellationToken) =>
            AnnotationOverwrites.Dequeue();

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
            return Task.FromResult(Conversation(session.SessionId));
        }

        public static PortableConversation Conversation(string sessionId) => new(
            ConversationAgent.Codex, sessionId, "title " + sessionId, @"C:\Repos\Demo",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [
                new PortableTurn(ConversationRole.User, "question for " + sessionId),
                new PortableTurn(ConversationRole.Assistant, "answer for " + sessionId)
            ]);
    }

    private sealed class RecordingAnnotations : ISessionAnnotationStore
    {
        public Dictionary<SessionAnnotationKey, SessionAnnotation> Saved { get; } = [];

        public Task<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>> LoadAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>>(Saved);

        public Task SaveAsync(
            SessionAnnotationKey key,
            SessionAnnotation annotation,
            CancellationToken cancellationToken)
        {
            Saved[key] = annotation;
            return Task.CompletedTask;
        }

        public List<SessionAnnotationKey> Deleted { get; } = [];

        public Task DeleteAsync(SessionAnnotationKey key, CancellationToken cancellationToken)
        {
            Deleted.Add(key);
            Saved.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedSuggester : ISessionTitleSuggester
    {
        public bool IsConfigured => true;

        public string? LastFailure => null;

        public SessionAnnotationDraft? Draft { get; init; }

        public int DelayMilliseconds { get; init; }

        public int Calls { get; private set; }

        public string? LastDigest { get; private set; }

        public async Task<SessionAnnotationDraft?> SuggestAsync(
            SessionDigestResult digest,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastDigest = digest.Text;
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellationToken);
            return Draft;
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
