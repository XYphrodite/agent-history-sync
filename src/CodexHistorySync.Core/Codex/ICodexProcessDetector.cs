namespace CodexHistorySync.Core.Codex;

public interface ICodexProcessDetector
{
    bool IsRunning();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}
