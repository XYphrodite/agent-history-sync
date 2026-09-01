using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public sealed class SessionViewerApplication(
    ILocalSessionCatalog catalog,
    ISessionContentReader contentReader,
    ISessionExporter exporter,
    ILocalSessionOperations operations,
    ISessionViewerView view,
    ISessionAnnotationStore? annotations = null,
    ISessionTitleSuggester? suggester = null,
    string? titlingRejection = null)
{
    private const string DeleteSyncWarning = "Local deletion may be restored by sync.";

    /// <summary>Stamped on a title typed for a session whose conversation could not be read.</summary>
    private const string ManualDigestHash = "manual";

    /// <summary>How often a running suggestion looks up to see whether a key is waiting.</summary>
    private static readonly TimeSpan SuggestionPollInterval = TimeSpan.FromMilliseconds(50);

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
            // Reading happens here, never while a frame is being built (design D3). Holding a
            // movement key would otherwise read one whole session per row — measured at 79 ms
            // median and 394 ms worst on this machine — so a pending keystroke defers the read
            // until the selection settles.
            var selectedKey = KeyFor(state.SelectedSession);
            if (selectedKey != loadedFor)
            {
                if (view.IsInputPending)
                {
                    if (state.Content.Status != SessionContentStatus.Loading)
                        state = state.WithContent(new SessionContentState(SessionContentStatus.Loading));
                }
                else
                {
                    loadedFor = selectedKey;
                    state = await LoadAsync(state, cancellationToken).ConfigureAwait(false);
                }
            }

            view.Render(state);
            var command = view.ReadCommand(cancellationToken);
            if (command == SessionViewerCommand.Exit) return;

            switch (command)
            {
                case SessionViewerCommand.Search:
                    state = state.WithSearchQuery(view.ReadSearchQuery(state, cancellationToken));
                    break;
                case SessionViewerCommand.FilterList:
                    state = state.WithListFilter(view.ReadListFilter(state, cancellationToken));
                    break;
                case SessionViewerCommand.Export:
                    await ExportAsync(state, cancellationToken).ConfigureAwait(false);
                    break;
                case SessionViewerCommand.GenerateAnnotation:
                    (state, loadedFor) = await GenerateAnnotationAsync(state, loadedFor, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case SessionViewerCommand.EditAnnotation:
                    (state, loadedFor) = await EditAnnotationAsync(state, loadedFor, cancellationToken)
                        .ConfigureAwait(false);
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
        if (TryGetCached(key) is { } hit) return state.WithContent(Loaded(session, hit));

        view.Render(state.WithContent(new SessionContentState(SessionContentStatus.Loading)));
        try
        {
            var conversation = await contentReader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
            Remember(key, conversation);
            return state.WithContent(Loaded(session, conversation));
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

    private SessionContentState Loaded(ManagedSession session, PortableConversation conversation) => new(
        SessionContentStatus.Loaded,
        ConversationDocument.Build(conversation, view.ContentWidth),
        AnnotationIsStale: session.Annotation is { } annotation &&
                           !string.Equals(annotation.DigestHash, SessionDigest.Build(conversation).Hash,
                               StringComparison.Ordinal));

    private async Task<(SessionViewerState State, string? LoadedFor)> GenerateAnnotationAsync(
        SessionViewerState state,
        string? loadedFor,
        CancellationToken cancellationToken)
    {
        var session = state.SelectedSession;
        if (session is null)
        {
            view.ShowMessage("No session is selected.", true);
            return (state, loadedFor);
        }

        if (annotations is null || suggester is null || !suggester.IsConfigured)
        {
            view.ShowMessage(TitlingUnavailable(), true);
            return (state, loadedFor);
        }

        // A title someone typed is not something a model gets to overwrite unasked.
        if (session.Annotation is { Source: SessionAnnotationSource.Edited } &&
            !view.ConfirmAnnotationOverwrite(session, cancellationToken))
            return (state, loadedFor);

        var conversation = await ConversationForAsync(session, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            view.ShowMessage("This session could not be read.", true);
            return (state, loadedFor);
        }

        var digest = SessionDigest.Build(conversation);
        if (digest.IsEmpty)
        {
            view.ShowMessage("There is nothing to name in this session.", true);
            return (state, loadedFor);
        }

        view.ShowMessage("Naming this session\u2026", false);
        view.Render(state);
        var (cancelled, draft) = await SuggestAsync(digest, cancellationToken).ConfigureAwait(false);
        // A keystroke is its own answer: the user asked for the screen back, not for a message.
        if (cancelled) return (state, loadedFor);
        if (draft is null)
        {
            view.ShowMessage("The titling endpoint answered nothing usable.", true);
            return (state, loadedFor);
        }

        return await StoreAsync(
            state,
            loadedFor,
            session,
            new SessionAnnotation(
                draft.Title,
                draft.Description,
                SessionAnnotationSource.Generated,
                digest.Hash,
                draft.Model,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(SessionViewerState State, string? LoadedFor)> EditAnnotationAsync(
        SessionViewerState state,
        string? loadedFor,
        CancellationToken cancellationToken)
    {
        var session = state.SelectedSession;
        if (session is null)
        {
            view.ShowMessage("No session is selected.", true);
            return (state, loadedFor);
        }

        if (annotations is null)
        {
            view.ShowMessage("Titles cannot be stored on this machine.", true);
            return (state, loadedFor);
        }

        // Typing a title asks nothing of a model, so it works with no endpoint configured.
        var edit = view.ReadAnnotation(session, session.Annotation, cancellationToken);
        if (edit is null || string.IsNullOrWhiteSpace(edit.Title)) return (state, loadedFor);

        var conversation = await ConversationForAsync(session, cancellationToken).ConfigureAwait(false);
        var description = string.IsNullOrWhiteSpace(edit.Description) ? null : edit.Description.Trim();
        return await StoreAsync(
            state,
            loadedFor,
            session,
            new SessionAnnotation(
                edit.Title.Trim(),
                description,
                SessionAnnotationSource.Edited,
                conversation is null ? ManualDigestHash : SessionDigest.Build(conversation).Hash,
                null,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the suggestion off the render path and gives the screen back the moment a key is
    /// pressed. A model of this size answers in tens of seconds, which is far too long to hold.
    /// </summary>
    private async Task<(bool Cancelled, SessionAnnotationDraft? Draft)> SuggestAsync(
        SessionDigestResult digest,
        CancellationToken cancellationToken)
    {
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var suggestion = suggester!.SuggestAsync(digest, abandon.Token);
        var abandoned = false;

        while (!suggestion.IsCompleted)
        {
            if (view.IsInputPending)
            {
                abandoned = true;
                await abandon.CancelAsync().ConfigureAwait(false);
                break;
            }

            var settled = await Task.WhenAny(suggestion, Task.Delay(SuggestionPollInterval, abandon.Token))
                .ConfigureAwait(false);
            if (settled == suggestion) break;
        }

        try
        {
            return (abandoned, await suggestion.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return (true, null);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    private async Task<(SessionViewerState State, string? LoadedFor)> StoreAsync(
        SessionViewerState state,
        string? loadedFor,
        ManagedSession session,
        SessionAnnotation annotation,
        CancellationToken cancellationToken)
    {
        try
        {
            await annotations!.SaveAsync(
                new SessionAnnotationKey(session.Agent, session.SessionId), annotation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            view.ShowMessage("That title could not be stored.", true);
            return (state, loadedFor);
        }

        view.ShowMessage($"Named: {annotation.Title}", false);
        // The rescan is what carries the new title onto the row, through the same overlay every
        // other title comes through.
        return (await RefreshAsync(state, cancellationToken).ConfigureAwait(false), null);
    }

    private async Task<PortableConversation?> ConversationForAsync(
        ManagedSession session,
        CancellationToken cancellationToken)
    {
        var key = KeyFor(session)!;
        if (TryGetCached(key) is { } cached) return cached;

        try
        {
            var conversation = await contentReader.ReadAsync(session, cancellationToken).ConfigureAwait(false);
            Remember(key, conversation);
            return conversation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string TitlingUnavailable() =>
        string.IsNullOrWhiteSpace(titlingRejection)
            ? "Titling is not configured."
            : $"Titling is not configured: {titlingRejection}";

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
            // The session is gone, so its title goes with it rather than outliving it as an
            // orphan that would travel to every other machine.
            if (annotations is not null)
                await annotations
                    .DeleteAsync(new SessionAnnotationKey(session.Agent, session.SessionId), cancellationToken)
                    .ConfigureAwait(false);
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
