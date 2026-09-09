using CodexHistorySync.Core.Update;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CodexHistorySync.Cli;

/// <summary>The update command's live display, with one plain line per phase in redirected output.</summary>
internal sealed class SelfUpdateProgressDisplay(IAnsiConsole console)
{
    public Task<SelfUpdateReport> RunAsync(
        SelfUpdateService service,
        SelfUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!console.Profile.Capabilities.Interactive)
        {
            SelfUpdatePhase? lastPhase = null;
            return service.UpdateAsync(request, cancellationToken, progress =>
            {
                if (progress.Phase == lastPhase) return;
                lastPhase = progress.Phase;
                console.WriteLine(Description(progress));
            });
        }

        return console.Progress()
            .AutoClear(true)
            .Columns(
                new TaskDescriptionColumn(),
                new DownloadColumn(new ProgressBarColumn { Width = Math.Clamp(console.Profile.Width - 65, 8, 40) }),
                new DownloadColumn(new PercentageColumn(), requiresTotal: true),
                new DownloadColumn(new DownloadedColumn(), requiresTotal: true),
                new DownloadColumn(new TransferSpeedColumn()),
                new DownloadColumn(new RemainingTimeColumn(), requiresTotal: true),
                new SpinnerColumn())
            .StartAsync(async context =>
            {
                SelfUpdatePhase? lastPhase = null;
                ProgressTask? task = null;
                try
                {
                    return await service.UpdateAsync(request, cancellationToken, progress =>
                    {
                        if (progress.Phase != lastPhase)
                        {
                            task?.StopTask();
                            var description = progress.Phase == SelfUpdatePhase.Downloading
                                ? progress.Release!.Tag
                                : Description(progress);
                            task = context.AddTask(Markup.Escape(description), maxValue: 1);
                            task.HideWhenCompleted = true;
                            task.Tag = progress.Phase;
                            lastPhase = progress.Phase;
                        }

                        task!.IsIndeterminate = progress.Phase != SelfUpdatePhase.Downloading ||
                            progress.TotalBytes is not > 0;
                        if (progress.Phase == SelfUpdatePhase.Downloading)
                        {
                            // A chunked response may carry no length; keep an indeterminate bar
                            // until we know the total, rather than inventing a percentage or ETA.
                            task.MaxValue = progress.TotalBytes is > 0
                                ? Math.Max(progress.TotalBytes.Value, progress.ReceivedBytes)
                                : Math.Max(1, (double)progress.ReceivedBytes + 1);
                            task.Value = progress.ReceivedBytes;
                        }
                    }).ConfigureAwait(false);
                }
                finally
                {
                    task?.StopTask();
                }
            });
    }

    private static string Description(SelfUpdateProgress progress) => progress.Phase switch
    {
        SelfUpdatePhase.Checking => "Checking GitHub...",
        SelfUpdatePhase.Downloading => $"Downloading {progress.Release!.Tag} (agent-sync.exe)...",
        SelfUpdatePhase.Verifying => "Verifying checksum and executable...",
        SelfUpdatePhase.Installing => "Installing and checking the new version...",
        _ => throw new ArgumentOutOfRangeException(nameof(progress))
    };

    // Only the download has meaningful byte counts and rates; other phases show a spinner.
    private sealed class DownloadColumn(ProgressColumn inner, bool requiresTotal = false) : ProgressColumn
    {
        protected override bool NoWrap => true;

        public override int? GetColumnWidth(RenderOptions options) => inner.GetColumnWidth(options);

        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime) =>
            task.Tag is SelfUpdatePhase.Downloading && (!requiresTotal || !task.IsIndeterminate)
                ? inner.Render(options, task, deltaTime)
                : new Text(string.Empty);
    }
}
