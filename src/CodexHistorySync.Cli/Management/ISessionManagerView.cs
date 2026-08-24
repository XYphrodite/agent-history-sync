using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

public interface ISessionManagerView
{
    Task RunDisplayAsync(Func<CancellationToken, Task> interaction, CancellationToken cancellationToken);
    void Render(SessionManagerState state);
    SessionManagerCommand ReadCommand(CancellationToken cancellationToken);
    string ReadSearchQuery(SessionManagerState state, CancellationToken cancellationToken);
    bool ConfirmLocalDelete(ManagedSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Asks which agent to copy into. Only called with more than one candidate; null cancels.
    /// </summary>
    ManagedAgent? ChooseCopyTarget(
        ManagedSession source,
        IReadOnlyList<ManagedAgent> targets,
        CancellationToken cancellationToken);
    void ShowMessage(string message, bool isError);
}
