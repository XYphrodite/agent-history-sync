using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public sealed class SessionManagerApplication(
    ILocalSessionCatalog catalog,
    ILocalSessionOperations operations,
    ISessionManagerView view)
{
    private const string DeleteSyncWarning = "Local deletion may be restored by sync.";

    public Task RunAsync(CancellationToken cancellationToken) =>
        view.RunDisplayAsync(RunLoopAsync, cancellationToken);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        SessionManagerState state;
        try
        {
            state = new SessionManagerState(await catalog.ScanAsync(cancellationToken));
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

        while (!cancellationToken.IsCancellationRequested)
        {
            view.Render(state);
            var command = view.ReadCommand(cancellationToken);
            if (command == SessionManagerCommand.Exit) return;

            switch (command)
            {
                case SessionManagerCommand.MoveUp:
                case SessionManagerCommand.MoveDown:
                case SessionManagerCommand.FocusLeft:
                case SessionManagerCommand.FocusRight:
                    state = state.ApplyNavigation(command);
                    break;
                case SessionManagerCommand.Refresh:
                    state = await RefreshAsync(state, cancellationToken);
                    break;
                case SessionManagerCommand.Copy:
                    state = await CopyAsync(state, cancellationToken);
                    break;
                case SessionManagerCommand.Delete:
                    state = await DeleteAsync(state, cancellationToken);
                    break;
            }
        }
    }

    private async Task<SessionManagerState> RefreshAsync(SessionManagerState state, CancellationToken cancellationToken)
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

    private async Task<SessionManagerState> CopyAsync(SessionManagerState state, CancellationToken cancellationToken)
    {
        var source = GetActionableSession(state, "copied");
        if (source is null) return state;

        try
        {
            await operations.CopyAsync(source, cancellationToken);
            return await RefreshAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            view.ShowMessage($"Copy failed for session {SafeId(source.SessionId)}.", true);
            return state;
        }
    }

    private async Task<SessionManagerState> DeleteAsync(SessionManagerState state, CancellationToken cancellationToken)
    {
        var source = GetActionableSession(state, "deleted");
        if (source is null) return state;

        view.ShowMessage(DeleteSyncWarning, false);
        if (!view.ConfirmLocalDelete(source)) return state;

        try
        {
            await operations.DeleteAsync(source, cancellationToken);
            return await RefreshAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            view.ShowMessage($"Delete failed for session {SafeId(source.SessionId)}.", true);
            return state;
        }
    }

    private ManagedSession? GetActionableSession(SessionManagerState state, string action)
    {
        var source = state.SelectedSession;
        if (source is null)
        {
            view.ShowMessage("No session is selected.", true);
            return null;
        }

        if (source.IsActive)
        {
            view.ShowMessage($"Active sessions cannot be {action}.", true);
            return null;
        }

        if (!source.CanRead)
        {
            view.ShowMessage($"This session cannot be {action}.", true);
            return null;
        }

        return source;
    }

    private static string SafeId(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && sessionId.Length <= 64 &&
        sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? sessionId
            : "unknown";
}
