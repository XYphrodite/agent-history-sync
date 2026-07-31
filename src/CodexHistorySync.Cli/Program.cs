using CodexHistorySync.Cli;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler handler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += handler;
try
{
    return await CliComposition.CreateDefault().RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 0;
}
finally
{
    Console.CancelKeyPress -= handler;
}
