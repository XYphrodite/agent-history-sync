using System.Diagnostics;
using CodexHistorySync.Core.Update;

namespace CodexHistorySync.Cli;

/// <summary>
/// Wires the update service to this process: the binary it replaces is the one running, and
/// the probe that decides whether the replacement worked is that binary answering
/// <c>--help</c>, a switch every published release answers.
/// </summary>
internal sealed class DefaultSelfUpdateOperations : ISelfUpdateOperations
{
    private const string InstalledExecutable = "agent-sync.exe";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<string?> resolveExecutablePath;

    public DefaultSelfUpdateOperations(Func<string?>? resolveExecutablePath = null) =>
        this.resolveExecutablePath = resolveExecutablePath ?? (() => Environment.ProcessPath);

    public async Task<SelfUpdateReport> UpdateAsync(SelfUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = resolveExecutablePath();
        // Under `dotnet run` the process path is the host, not this tool. Replacing whatever
        // happens to be hosting the CLI is never the intent, so the command declines instead.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetFileName(path), InstalledExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Self-update is only available for an installed agent-sync.exe.");

        using var source = new GitHubReleaseSource();
        var service = new SelfUpdateService(path, CliVersion.Current, source, probe: ProbeAsync);
        return await service.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ProbeAsync(string executablePath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--help");

        using var process = Process.Start(start);
        if (process is null) return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A new binary that cannot answer --help in half a minute is not one to keep.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return false;
        }

        return process.ExitCode == 0;
    }
}
