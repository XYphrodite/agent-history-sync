using CodexHistorySync.Cli.Management;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.IntegrationTests;

public sealed class SessionManagerApplicationTests
{
    [Fact]
    public async Task RunAsyncUsesOneDisplaySession()
    {
        var catalog = new MutableCatalog(Snapshot([], []));
        var view = new ScriptedView(SessionManagerCommand.Exit);
        var application = new SessionManagerApplication(catalog, new MutatingOperations(catalog), view);

        await application.RunAsync(CancellationToken.None);

        Assert.Equal(1, view.DisplaySessions);
        Assert.Single(view.RenderedStates);
    }

    [Fact]
    public async Task Refresh_copy_and_confirmed_delete_render_both_updated_panels()
    {
        var catalog = new MutableCatalog(Snapshot(
            codex: [Session(ManagedAgent.Codex, "keep"), Session(ManagedAgent.Codex, "copy-source")],
            grok: [Session(ManagedAgent.Grok, "grok-original")]));
        var view = new ScriptedView(SessionManagerCommand.MoveDown, SessionManagerCommand.Refresh,
            SessionManagerCommand.Copy, SessionManagerCommand.Delete, SessionManagerCommand.Exit);
        view.DeleteConfirmations.Enqueue(true);
        var application = new SessionManagerApplication(catalog, new MutatingOperations(catalog), view);

        await application.RunAsync(CancellationToken.None);

        Assert.Contains(view.RenderedStates, state => state.Snapshot.Grok.Any(session => session.SessionId == "copied-session"));
        var finalState = view.RenderedStates.Last();
        Assert.Equal(["keep"], finalState.Snapshot.Codex.Select(session => session.SessionId));
        Assert.Contains("copied-session", finalState.Snapshot.Grok.Select(session => session.SessionId));
        Assert.Contains(view.Messages, message => message.Message == "Local deletion may be restored by sync." && !message.IsError);
    }

    [Fact]
    public async Task Unconfirmed_delete_keeps_the_selected_session_available()
    {
        var catalog = new MutableCatalog(Snapshot([Session(ManagedAgent.Codex, "keep")], []));
        var view = new ScriptedView(SessionManagerCommand.Delete, SessionManagerCommand.Exit);
        view.DeleteConfirmations.Enqueue(false);
        var application = new SessionManagerApplication(catalog, new MutatingOperations(catalog), view);

        await application.RunAsync(CancellationToken.None);

        Assert.Equal(["keep"], view.RenderedStates.Last().Snapshot.Codex.Select(session => session.SessionId));
        Assert.Contains(view.Messages, message => message.Message == "Local deletion may be restored by sync." && !message.IsError);
    }

    [Fact]
    public async Task Active_and_unread_sessions_are_refused_without_changing_the_rendered_catalog()
    {
        var catalog = new MutableCatalog(Snapshot(
            [Session(ManagedAgent.Codex, "active", isActive: true), Session(ManagedAgent.Codex, "unread", canRead: false)], []));
        var view = new ScriptedView(SessionManagerCommand.Copy, SessionManagerCommand.MoveDown,
            SessionManagerCommand.Copy, SessionManagerCommand.Exit);
        var application = new SessionManagerApplication(catalog, new MutatingOperations(catalog), view);

        await application.RunAsync(CancellationToken.None);

        Assert.Equal(["active", "unread"], view.RenderedStates.Last().Snapshot.Codex.Select(session => session.SessionId));
        Assert.Contains(view.Messages, message => message.Message == "Active sessions cannot be copied." && message.IsError);
        Assert.Contains(view.Messages, message => message.Message == "This session cannot be copied." && message.IsError);
    }

    [Fact]
    public async Task Operation_failures_keep_the_loop_open_and_only_show_fixed_safe_messages()
    {
        const string injectedPath = "C:\\private\\<injected-path>";
        const string injectedException = "<injected-exception>";
        var catalog = new MutableCatalog(Snapshot([Session(ManagedAgent.Codex, "safe-id", nativePath: injectedPath)], []));
        var view = new ScriptedView(SessionManagerCommand.Copy, SessionManagerCommand.Delete, SessionManagerCommand.Exit);
        view.DeleteConfirmations.Enqueue(true);
        var application = new SessionManagerApplication(catalog,
            new FailingOperations(new InvalidOperationException(injectedException)), view);

        await application.RunAsync(CancellationToken.None);

        Assert.Equal("safe-id", view.RenderedStates.Last().SelectedSession!.SessionId);
        Assert.Contains(view.Messages, message => message.Message == "Copy failed for session safe-id." && message.IsError);
        Assert.Contains(view.Messages, message => message.Message == "Delete failed for session safe-id." && message.IsError);
        var displayed = string.Join("\n", view.Messages.Select(message => message.Message));
        Assert.DoesNotContain(injectedPath, displayed, StringComparison.Ordinal);
        Assert.DoesNotContain(injectedException, displayed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operation_failure_keeps_catalog_safe_session_id_characters()
    {
        var catalog = new MutableCatalog(Snapshot([Session(ManagedAgent.Codex, "session_1.2")], []));
        var view = new ScriptedView(SessionManagerCommand.Copy, SessionManagerCommand.Exit);
        var application = new SessionManagerApplication(catalog,
            new FailingOperations(new InvalidOperationException("irrelevant")), view);

        await application.RunAsync(CancellationToken.None);

        Assert.Contains(view.Messages,
            message => message.Message == "Copy failed for session session_1.2." && message.IsError);
    }

    [Fact]
    public async Task Search_and_clear_commands_filter_then_restore_both_panels()
    {
        var catalog = new MutableCatalog(Snapshot(
            [Session(ManagedAgent.Codex, "codex-needle"), Session(ManagedAgent.Codex, "codex-other")],
            [Session(ManagedAgent.Grok, "grok-needle"), Session(ManagedAgent.Grok, "grok-other")]));
        var view = new ScriptedView(
            SessionManagerCommand.Search,
            SessionManagerCommand.ClearSearch,
            SessionManagerCommand.Exit);
        view.SearchQueries.Enqueue("needle");
        var application = new SessionManagerApplication(catalog, new MutatingOperations(catalog), view);

        await application.RunAsync(CancellationToken.None);

        var filtered = Assert.Single(view.RenderedStates, state => state.SearchQuery == "needle");
        Assert.Equal(["codex-needle"], filtered.Snapshot.Codex.Select(session => session.SessionId));
        Assert.Equal(["grok-needle"], filtered.Snapshot.Grok.Select(session => session.SessionId));
        var restored = view.RenderedStates.Last();
        Assert.Equal(string.Empty, restored.SearchQuery);
        Assert.Equal(2, restored.Snapshot.Codex.Count);
        Assert.Equal(2, restored.Snapshot.Grok.Count);
    }

    [Fact]
    public async Task Copy_uses_the_selected_visible_search_result()
    {
        var catalog = new MutableCatalog(Snapshot(
            [Session(ManagedAgent.Codex, "other"), Session(ManagedAgent.Codex, "needle")], []));
        var view = new ScriptedView(
            SessionManagerCommand.Search,
            SessionManagerCommand.Copy,
            SessionManagerCommand.Exit);
        view.SearchQueries.Enqueue("needle");
        var operations = new RecordingOperations();
        var application = new SessionManagerApplication(catalog, operations, view);

        await application.RunAsync(CancellationToken.None);

        Assert.Equal("needle", Assert.Single(operations.Copied).SessionId);
    }

    private static SessionCatalogSnapshot Snapshot(IReadOnlyList<ManagedSession> codex, IReadOnlyList<ManagedSession> grok) =>
        new(codex, grok);

    private static ManagedSession Session(ManagedAgent agent, string id, bool isActive = false, bool canRead = true,
        string? nativePath = null) =>
        new(agent, id, nativePath ?? $"C:\\sessions\\{id}", id, DateTimeOffset.UnixEpoch, isActive, canRead);

    private sealed class ScriptedView(params SessionManagerCommand[] commands) : ISessionManagerView
    {
        private readonly Queue<SessionManagerCommand> commands = new(commands);

        public Queue<bool> DeleteConfirmations { get; } = new();
        public Queue<string> SearchQueries { get; } = new();
        public List<SessionManagerState> RenderedStates { get; } = [];
        public List<(string Message, bool IsError)> Messages { get; } = [];
        public int DisplaySessions { get; private set; }

        public async Task RunDisplayAsync(Func<CancellationToken, Task> interaction,
            CancellationToken cancellationToken)
        {
            DisplaySessions++;
            await interaction(cancellationToken);
        }

        public void Render(SessionManagerState state) => RenderedStates.Add(state);

        public SessionManagerCommand ReadCommand(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return commands.Dequeue();
        }

        public string ReadSearchQuery(SessionManagerState state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SearchQueries.Dequeue();
        }

        public bool ConfirmLocalDelete(ManagedSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DeleteConfirmations.Dequeue();
        }

        public void ShowMessage(string message, bool isError) => Messages.Add((message, isError));
    }

    private sealed class MutableCatalog(SessionCatalogSnapshot snapshot) : ILocalSessionCatalog
    {
        public SessionCatalogSnapshot Snapshot { get; set; } = snapshot;

        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class MutatingOperations(MutableCatalog catalog) : ILocalSessionOperations
    {
        public Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            catalog.Snapshot = catalog.Snapshot with
            {
                Grok = [.. catalog.Snapshot.Grok, Session(ManagedAgent.Grok, "copied-session")]
            };
            return Task.FromResult("ignored-native-path");
        }

        public Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            catalog.Snapshot = source.Agent == ManagedAgent.Codex
                ? catalog.Snapshot with { Codex = catalog.Snapshot.Codex.Where(session => session.SessionId != source.SessionId).ToArray() }
                : catalog.Snapshot with { Grok = catalog.Snapshot.Grok.Where(session => session.SessionId != source.SessionId).ToArray() };
            return Task.CompletedTask;
        }
    }

    private sealed class FailingOperations(Exception failure) : ILocalSessionOperations
    {
        public Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken) =>
            Task.FromException<string>(failure);

        public Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken) =>
            Task.FromException(failure);
    }

    private sealed class RecordingOperations : ILocalSessionOperations
    {
        public List<ManagedSession> Copied { get; } = [];

        public Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Copied.Add(source);
            return Task.FromResult("unused");
        }

        public Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
