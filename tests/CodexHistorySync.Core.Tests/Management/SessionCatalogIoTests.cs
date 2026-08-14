using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class SessionCatalogIoTests
{
    [Fact]
    public async Task PrefixAndTailNeverConsumeMoreThanTheBudget()
    {
        // Reading beyond the requested bound would make large histories unbounded catalog work.
        await using var fixture = new CatalogIoFixture();
        var path = await fixture.WriteAsync(new string('a', 200_000));
        var io = new SystemSessionCatalogIo();

        var prefix = await io.ReadPrefixAsync(path, 64 * 1024, CancellationToken.None);
        var tail = await io.ReadTailAsync(path, 64 * 1024, CancellationToken.None);

        Assert.Equal(64 * 1024, prefix.BytesRead);
        Assert.Equal(64 * 1024, tail.BytesRead);
        Assert.False(prefix.IsComplete);
        Assert.False(tail.IsComplete);
    }

    [Fact]
    public async Task RunAsyncLimitsConcurrentReadsToConfiguredMaximum()
    {
        // Releasing the semaphore before an operation completes would allow more than eight reads at once.
        using var limiter = new SessionCatalogReadLimiter(8);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var peak = 0;
        var operations = Enumerable.Range(0, 24).Select(_ => limiter.RunAsync(async token =>
        {
            var observed = Interlocked.Increment(ref current);
            UpdateMaximum(ref peak, observed);
            if (observed == 8)
                entered.TrySetResult();

            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref current);
            return 0;
        }, cancellation.Token)).ToArray();

        await entered.Task.WaitAsync(cancellation.Token);
        release.TrySetResult();
        await Task.WhenAll(operations);

        Assert.Equal(8, peak);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (candidate <= observed || Interlocked.CompareExchange(ref maximum, candidate, observed) == observed)
                return;
        }
    }

    private sealed class CatalogIoFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public async Task<string> WriteAsync(string content)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "history.jsonl");
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
