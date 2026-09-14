namespace CodexHistorySync.Cli;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The desktop event loop must start on the entry thread (also on macOS).
        // This is the process boundary; asynchronous I/O remains asynchronous inside the app.
        return RunAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await CliEntryPoint.RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        finally { Console.CancelKeyPress -= handler; }
    }
}
