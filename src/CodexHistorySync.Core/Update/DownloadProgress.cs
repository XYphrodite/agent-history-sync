namespace CodexHistorySync.Core.Update;

/// <summary>
/// Copies a download while reporting how many bytes have landed. The CLI turns those reports
/// into a Spectre progress bar; tests assert the numbers without a console.
/// </summary>
public static class DownloadProgress
{
    public const int BufferSize = 80 * 1024;

    public static async Task CopyAsync(
        Stream source,
        Stream destination,
        long? totalLength,
        Action<long, long?>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Invoke(0, totalLength);
        var buffer = new byte[BufferSize];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Invoke(received, totalLength);
        }
    }
}
