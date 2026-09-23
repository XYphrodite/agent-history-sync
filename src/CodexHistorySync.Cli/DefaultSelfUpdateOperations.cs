using SelfUpdateKit;
using Spectre.Console;

namespace CodexHistorySync.Cli;

/// <summary>
/// Wires the update service to this process: the binary it replaces is the one running, and
/// the probe that decides whether the replacement worked is that binary answering
/// <c>--help</c>, a switch every published release answers. The probe and the install rules
/// come from the shared kit; this class only fixes the repository and the display.
/// </summary>
internal sealed class DefaultSelfUpdateOperations : ISelfUpdateOperations
{
    private const string InstalledExecutable = "agent-sync.exe";

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

        var options = AgentSyncUpdate.Options();
        using var source = new GitHubReleaseSource(options);
        var service = new SelfUpdateService(path, CliVersion.Current, source, options);
        var display = new SelfUpdateProgressDisplay(AnsiConsole.Console);
        return await display.RunAsync(service, request, cancellationToken).ConfigureAwait(false);
    }
}
