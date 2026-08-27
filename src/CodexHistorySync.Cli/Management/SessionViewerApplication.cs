using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public sealed class SessionViewerApplication(
    ILocalSessionCatalog catalog,
    ISessionContentReader contentReader,
    ISessionExporter exporter,
    ILocalSessionOperations operations,
    ISessionViewerView view)
{
    private const string DeleteSyncWarning = "Local deletion may be restored by sync.";

    private readonly ILocalSessionCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ISessionContentReader contentReader = contentReader ?? throw new ArgumentNullException(nameof(contentReader));
    private readonly ISessionExporter exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    private readonly ILocalSessionOperations operations = operations ?? throw new ArgumentNullException(nameof(operations));
    private readonly ISessionViewerView view = view ?? throw new ArgumentNullException(nameof(view));

    /// <summary>
    /// The few most recently read conversations, so stepping through the list and back does not
    /// re-read a multi-megabyte transcript. Deliberately small: each entry holds a whole session.
    /// </summary>
    private const int CacheCapacity = 3;
    private readonly LinkedList<(string Key, PortableConversation Conversation)> cache = new();

    public Task RunAsync(CancellationToken cancellationToken) =>
        view.RunDisplayAsync(RunLoopAsync, cancellationToken);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        SessionViewerState state;
        try
        {
            state = SessionViewerState.Create(await catalog.ScanAsync(cancellationToken), view.ContentRows);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            view.ShowMessage("Session refresh failed.", true);
            return;
        }

        string? loadedFor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            state = state.SetViewportRows(view.ContentRows);
            // Reading happens here, never while a frame is being built (design D3).
            if (KeyFor(state.SelectedSession) != loadedFor)
            {
                loadedFor = KeyFor(state.SelectedSession);
                state = await LoadAsync(state, cancellationToken).ConfigureAwait(false);
            }

            view.Render(state);
            var command = view.ReadCommand(cancellationToken);
            if (command == SessionViewerCommand.Exit) return;

            switch (command)
            {
                case SessionViewerCommand.Search:
                    state = state.WithSearchQuery(view.ReadSearchQuery(state, cancellationToken));
                    break;
                case SessionViewerCommand.Export:
                    await ExportAsync(state, cancellationToken).ConfigureAwait(false);
                    break;
                case SessionViewerCommand.Delete:
                    (state, loadedFor) = await DeleteAsync(state, cancellationToken).ConfigureAwait(false);
                    break;
                case SessionViewerCommand.Refresh:
                    (state, loadedFor) = (await RefreshAsync(state, cancellationToken).ConfigureAwait(false), null);
                    break;
                default:
                    state = state.Apply(command);
                    break;
            }
        }
    }

    private async Task<SessionViewerState> LoadAsync(SessionViewerState state, CancellationToken cancellationToken)
    {
        var session = state.SelectedSession;
        if (session is null) return state.WithContent(new SessionContentState(SessionContentStatus.Empty));

        var key = KeyFor(session)!;
        if (TryGetCached(key) is { } hit) return state.WithContent(Loaded(hit));

        view.Render(state.WithContent(new SessionContentState(SessionContentStatus.Loading)));
        try
        {
            var conversation = await contentReader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
            Remember(key, conversation);
            return state.WithContent(Loaded(conversation));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A broken session must not take the list down with it.
            return state.WithContent(new SessionContentState(
                SessionContentStatus.Failed, Message: "This session could not be read."));
        }
    }

    private SessionContentState Loaded(PortableConversation conversation) => new(
        SessionContentStatus.Loaded,
        ConversationDocument.Build(conversation, view.ContentWidth));

    private async Task ExportAsync(SessionViewerState state, CancellationToken cancellationToken)
    {
        var session = state.SelectedSession;
        if (session is null || TryGetCached(KeyFor(session)!) is not { } conversation)
        {
            view.ShowMessage("Open a session before exporting it.", true);
            return;
        }

        try
        {
            var path = await exporter.ExportAsync(session, conversation, cancellationToken).ConfigureAwait(false);
            view.ShowMessage($"Exported to {path}", false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            view.ShowMessage("Export failed.", true);
        }
    }

    private async Task<(SessionViewerState State, string? LoadedFor)> DeleteAsync(
        SessionViewerState state,
        CancellationToken cancellationToken)
    {
        var session = state.SelectedSession;
        if (session is null)
        {
            view.ShowMessage("No session is selected.", true);
            return (state, KeyFor(state.SelectedSession));
        }
        if (session.IsActive)
        {
            view.ShowMessage("Active sessions cannot be deleted.", true);
            return (state, KeyFor(session));
        }

        view.ShowMessage(DeleteSyncWarning, false);
        if (!view.ConfirmLocalDelete(session, cancellationToken)) return (state, KeyFor(session));

        try
        {
            await operations.DeleteAsync(session, cancellationToken).ConfigureAwait(false);
            cache.Clear();
            return (await RefreshAsync(state, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            view.ShowMessage($"Delete failed for session {SafeId(session.SessionId)}.", true);
            return (state, KeyFor(session));
        }
    }

    private async Task<SessionViewerState> RefreshAsync(SessionViewerState state, CancellationToken cancellationToken)
    {
        try
        {
            return state.ReplaceSnapshot(await catalog.ScanAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            view.ShowMessage("Session refresh failed.", true);
            return state;
        }
    }

    private PortableConversation? TryGetCached(string key)
    {
        for (var node = cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Key != key) continue;
            cache.Remove(node);
            cache.AddFirst(node);
            return node.Value.Conversation;
        }
        return null;
    }

    private void Remember(string key, PortableConversation conversation)
    {
        cache.AddFirst((key, conversation));
        while (cache.Count > CacheCapacity) cache.RemoveLast();
    }

    private static string? KeyFor(ManagedSession? session) =>
        session is null ? null : $"{session.Agent} {session.SessionId}";

    private static string SafeId(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && sessionId.Length <= 64 &&
        sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? sessionId
            : "unknown";
}
