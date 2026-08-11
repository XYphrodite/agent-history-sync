using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public interface ISessionManagerView
{
    Task RunDisplayAsync(Func<CancellationToken, Task> interaction, CancellationToken cancellationToken);
    void Render(SessionManagerState state);
    SessionManagerCommand ReadCommand(CancellationToken cancellationToken);
    bool ConfirmLocalDelete(ManagedSession session);
    void ShowMessage(string message, bool isError);
}
