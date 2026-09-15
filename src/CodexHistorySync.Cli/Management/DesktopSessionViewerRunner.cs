using CodexHistorySync.Core.Management;
using CodexHistorySync.Desktop;

namespace CodexHistorySync.Cli.Management;

internal sealed class DesktopSessionViewerRunner(DesktopSessionServices services) : ISessionManagerRunner
{
    public Task RunAsync(CancellationToken cancellationToken)
    {
        SessionViewerApp.Run(services, cancellationToken);
        return Task.CompletedTask;
    }
}

internal sealed class DesktopSessionManagerRunner(DesktopSessionServices services) : ISessionManagerRunner
{
    public Task RunAsync(CancellationToken cancellationToken)
    {
        SessionManagerApp.Run(services, cancellationToken);
        return Task.CompletedTask;
    }
}

// Non-Windows viewing never exposes delete or copy; active-state detection is not needed for reading.
internal sealed class ReadOnlySessionActiveState : IManagedSessionActiveState
{
    public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(ManagedAgent agent, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    public Task<bool> IsActiveAsync(ManagedAgent agent, string sessionId, string nativePath, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
