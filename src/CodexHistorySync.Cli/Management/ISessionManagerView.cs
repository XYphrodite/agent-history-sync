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

    /// <summary>
    /// Asks which destination to copy into, including a separate row per Hermes home. Null cancels.
    /// </summary>
    CopyDestination? ChooseCopyDestination(
        ManagedSession source,
        IReadOnlyList<CopyDestination> targets,
        CancellationToken cancellationToken)
    {
        var chosen = ChooseCopyTarget(source, targets.Select(target => target.Agent).ToArray(), cancellationToken);
        return chosen is null ? null : targets.FirstOrDefault(target => target.Agent == chosen);
    }

    void ShowMessage(string message, bool isError);
}
