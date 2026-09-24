using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Desktop.Tests;

internal sealed class IncrementalDesktopCatalog : ILocalSessionCatalog
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken ct) => throw new InvalidOperationException("The desktop must consume incremental updates.");
    public async IAsyncEnumerable<SessionCatalogSnapshot> ScanIncrementallyAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var ready = new ManagedSession(ManagedAgent.Codex, "ready", "ready", "Ready session", DateTimeOffset.UtcNow, false, true);
        var first = new SessionCatalogSnapshot([ready], [], [], [], [], [])
        { ConfiguredAgents = [ManagedAgent.Codex, ManagedAgent.Muse], PendingAgents = [ManagedAgent.Muse] };
        yield return first;
        await Release.Task.WaitAsync(ct);
        yield return first with
        {
            Muse = [ready with { Agent = ManagedAgent.Muse, SessionId = "muse", NativePath = "muse" }],
            PendingAgents = []
        };
    }
}

internal sealed class EmptyDesktopCatalog : ILocalSessionCatalog
{
    public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken ct) => Task.FromResult(
        new SessionCatalogSnapshot([], [], [], [], [], []) { ConfiguredAgents = [] });
}
