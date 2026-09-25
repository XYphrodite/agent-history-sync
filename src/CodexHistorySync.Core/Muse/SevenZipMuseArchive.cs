using System.Diagnostics;
using System.Text;

namespace CodexHistorySync.Core.Muse;

internal interface IMuseArchive
{
    Task<string> ListAsync(CancellationToken ct);
    Task ExtractAsync(string entry, Stream destination, long maximumBytes, CancellationToken ct);
}

/// <summary>Reads through stdout only; 7-Zip never mounts or writes to the source image.</summary>
internal sealed class SevenZipMuseArchive(string executable, string image) : IMuseArchive
{
    public async Task<string> ListAsync(CancellationToken ct)
    {
        using var output = new MemoryStream();
        await RunAsync(["l", "-slt", "-ba", "-sccUTF-8", image, "-ir!session.jsonl",
            "-ir!session-index.db", "-ir!session-index.db-wal"], output, 32 * 1024 * 1024, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    public Task ExtractAsync(string entry, Stream destination, long maximumBytes, CancellationToken ct) =>
        RunAsync(["x", "-so", "-spd", image, "--", entry.Replace('/', '\\')], destination, maximumBytes, ct);

    private async Task RunAsync(string[] arguments, Stream destination, long maximumBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("Could not start 7-Zip to read the WSL disk.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new IOException("Install 7-Zip to read Muse sessions while WSL is stopped.", ex); }
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var buffer = new byte[65536];
            long total = 0;
            while (await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false) is var count && count > 0)
            {
                total += count;
                if (total > maximumBytes) throw new IOException("The WSL disk entry exceeded its expected size. Refresh the list.");
                await destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("7-Zip could not read the WSL disk. It may be busy or unsupported.");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { throw new IOException("Reading the WSL disk timed out.", ex); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { await errors.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
}
