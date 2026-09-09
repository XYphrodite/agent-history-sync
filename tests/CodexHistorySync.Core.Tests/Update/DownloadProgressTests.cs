using CodexHistorySync.Core.Update;

namespace CodexHistorySync.Core.Tests.Update;

public sealed class DownloadProgressTests
{
    [Fact]
    public async Task CopyAsync_ReportsZeroThenEachChunkThenTheTotal()
    {
        var payload = new byte[DownloadProgress.BufferSize + 20];
        Random.Shared.NextBytes(payload);
        await using var source = new MemoryStream(payload);
        await using var destination = new MemoryStream();
        var reports = new List<(long Received, long? Total)>();

        await DownloadProgress.CopyAsync(source, destination, payload.Length,
            (received, total) => reports.Add((received, total)), CancellationToken.None);

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal((0, (long?)payload.Length), reports[0]);
        Assert.Equal((payload.Length, (long?)payload.Length), reports[^1]);
        Assert.True(reports.Count >= 3);
        Assert.All(reports, report => Assert.Equal(payload.Length, report.Total));
    }

    [Fact]
    public async Task CopyAsync_RunsWithoutAListener()
    {
        var payload = "MZ"u8.ToArray();
        await using var source = new MemoryStream(payload);
        await using var destination = new MemoryStream();

        await DownloadProgress.CopyAsync(source, destination, payload.Length, progress: null, CancellationToken.None);

        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task CopyAsync_ReportsBytesWithoutInventingAnUnknownTotal()
    {
        await using var source = new MemoryStream("MZ"u8.ToArray());
        await using var destination = new MemoryStream();
        var reports = new List<(long Received, long? Total)>();

        await DownloadProgress.CopyAsync(source, destination, null,
            (received, total) => reports.Add((received, total)), CancellationToken.None);

        Assert.Equal(2, reports[^1].Received);
        Assert.All(reports, report => Assert.Null(report.Total));
    }

    [Fact]
    public async Task CopyAsync_CancellationStopsTheTransferBeforeMoreBytesAreWritten()
    {
        await using var source = new MemoryStream(new byte[DownloadProgress.BufferSize * 3]);
        await using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadProgress.CopyAsync(
            source, destination, source.Length, (received, _) =>
            {
                if (received > 0) cancellation.Cancel();
            }, cancellation.Token));

        Assert.InRange(destination.Length, 1, source.Length - 1);
    }
}
