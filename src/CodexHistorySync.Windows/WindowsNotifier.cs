using System.Diagnostics;

namespace CodexHistorySync.Windows;

public enum AgentNotificationKind
{
    PendingRestart,
    UnresolvedConflict,
    RepeatedFailure,
    Recovered
}

public sealed record AgentNotification(AgentNotificationKind Kind, int Count = 0);

public interface IAgentNotifier
{
    Task NotifyAsync(AgentNotification notification, CancellationToken cancellationToken);
}

public sealed class WindowsNotifier : IAgentNotifier
{
    public async Task NotifyAsync(AgentNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!Enum.IsDefined(notification.Kind) || notification.Count < 0)
            throw new ArgumentOutOfRangeException(nameof(notification));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows notifications require Windows.");
        var startInfo = new ProcessStartInfo("msg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(Environment.UserName);
        startInfo.ArgumentList.Add("/TIME:15");
        startInfo.ArgumentList.Add(Message(notification));
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The Windows notification process could not be started.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("The Windows notification could not be delivered.");
    }

    private static string Message(AgentNotification notification) => notification.Kind switch
    {
        AgentNotificationKind.PendingRestart => $"Codex History Sync: {notification.Count} incoming change(s) will be imported after Codex exits.",
        AgentNotificationKind.UnresolvedConflict => $"Codex History Sync: {notification.Count} conflict(s) require resolution.",
        AgentNotificationKind.RepeatedFailure => $"Codex History Sync: automatic synchronization has failed {notification.Count} times.",
        AgentNotificationKind.Recovered => "Codex History Sync: automatic synchronization recovered.",
        _ => throw new ArgumentOutOfRangeException(nameof(notification))
    };
}
