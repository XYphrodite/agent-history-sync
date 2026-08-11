using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public interface ISessionManagerView
{
    void Render(SessionManagerState state);
    SessionManagerCommand ReadCommand(CancellationToken cancellationToken);
    bool ConfirmLocalDelete(ManagedSession session);
    void ShowMessage(string message, bool isError);
}
