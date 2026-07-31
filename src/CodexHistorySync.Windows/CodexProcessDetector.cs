using System.ComponentModel;
using System.Diagnostics;

namespace CodexHistorySync.Windows;

public sealed record CodexProcessDetectorOptions(
    string? ConfiguredExecutablePath = null,
    IReadOnlyCollection<string>? KnownProcessNames = null,
    IReadOnlyCollection<string>? KnownExecutableRoots = null,
    TimeSpan? InaccessibleProcessPollInterval = null)
{
    internal static readonly string[] DefaultProcessNames = ["codex", "ChatGPT", "Codex"];

    internal IReadOnlyCollection<string> EffectiveProcessNames =>
        KnownProcessNames is { Count: > 0 } ? KnownProcessNames : DefaultProcessNames;

    internal IReadOnlyCollection<string> EffectiveExecutableRoots =>
        KnownExecutableRoots ?? DefaultExecutableRoots();

    private static string[] DefaultExecutableRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
        {
            Path.Combine(local, "Programs", "OpenAI"),
            Path.Combine(local, "Programs", "ChatGPT"),
            Path.Combine(local, "Programs", "Codex"),
            Path.Combine(local, "Microsoft", "WindowsApps"),
            Path.Combine(programFiles, "WindowsApps")
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
    }
}

internal interface IProcessCatalog
{
    IReadOnlyList<IProcessObservation> FindByNames(IReadOnlySet<string> names);
}

internal interface IProcessObservation : IDisposable
{
    int Id { get; }
    string Name { get; }
    string GetExecutablePath();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public sealed class CodexProcessDetector : Core.Codex.ICodexProcessDetector
{
    private readonly IProcessCatalog catalog;
    private readonly HashSet<string> processNames;
    private readonly HashSet<string> configuredProcessNames;
    private readonly string? configuredExecutablePath;
    private readonly string[] trustedRoots;
    private readonly TimeSpan inaccessiblePollInterval;

    public CodexProcessDetector(CodexProcessDetectorOptions? options = null)
        : this(options ?? new CodexProcessDetectorOptions(), new SystemProcessCatalog()) { }

    internal CodexProcessDetector(CodexProcessDetectorOptions options, IProcessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        configuredExecutablePath = CanonicalizeOptional(options.ConfiguredExecutablePath);
        processNames = new HashSet<string>(options.EffectiveProcessNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
        configuredProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (configuredExecutablePath is not null)
        {
            var name = Path.GetFileNameWithoutExtension(configuredExecutablePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                configuredProcessNames.Add(name);
                processNames.Add(name);
            }
        }
        if (processNames.Count == 0) throw new ArgumentException("At least one Codex process name is required.", nameof(options));
        trustedRoots = options.EffectiveExecutableRoots.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        inaccessiblePollInterval = options.InaccessibleProcessPollInterval ?? TimeSpan.FromMilliseconds(250);
        if (inaccessiblePollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public bool IsRunning()
    {
        IReadOnlyList<IProcessObservation>? candidates = null;
        try
        {
            candidates = catalog.FindByNames(processNames);
            return candidates.Any(IsMatchingProcess);
        }
        catch (Exception exception) when (IsInspectionDenied(exception))
        {
            return true;
        }
        finally
        {
            if (candidates is not null) foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<IProcessObservation>? candidates = null;
            try
            {
                candidates = catalog.FindByNames(processNames);
                var matching = candidates.Where(IsMatchingProcess).ToArray();
                if (matching.Length == 0) return;
                try
                {
                    await Task.WhenAll(matching.Select(process => process.WaitForExitAsync(cancellationToken)))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsInspectionDenied(exception))
                {
                    await Task.Delay(inaccessiblePollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsInspectionDenied(exception))
            {
                await Task.Delay(inaccessiblePollInterval, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (candidates is not null) foreach (var candidate in candidates) candidate.Dispose();
            }
        }
    }

    private bool IsMatchingProcess(IProcessObservation process)
    {
        try
        {
            if (configuredExecutablePath is null && trustedRoots.Length == 0) return true;
            var executable = Path.GetFullPath(process.GetExecutablePath());
            if (configuredExecutablePath is not null &&
                StringComparer.OrdinalIgnoreCase.Equals(executable, configuredExecutablePath)) return true;
            if (configuredProcessNames.Contains(process.Name)) return false;
            return trustedRoots.Any(root => IsWithin(executable, root));
        }
        catch (Exception exception) when (IsInspectionDenied(exception))
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalPath = Path.GetFullPath(path);
        return canonicalPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CanonicalizeOptional(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static bool IsInspectionDenied(Exception exception) =>
        exception is UnauthorizedAccessException or System.Security.SecurityException ||
        exception is Win32Exception;

    private sealed class SystemProcessCatalog : IProcessCatalog
    {
        public IReadOnlyList<IProcessObservation> FindByNames(IReadOnlySet<string> names)
        {
            var result = new List<IProcessObservation>();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (names.Contains(process.ProcessName)) result.Add(new SystemProcessObservation(process));
                    else process.Dispose();
                }
                catch (InvalidOperationException)
                {
                    process.Dispose();
                }
                catch
                {
                    process.Dispose();
                    foreach (var candidate in result) candidate.Dispose();
                    throw;
                }
            }
            return result;
        }
    }

    private sealed class SystemProcessObservation(Process process) : IProcessObservation
    {
        public int Id => process.Id;
        public string Name => process.ProcessName;
        public string GetExecutablePath() => process.MainModule?.FileName
            ?? throw new Win32Exception(5, "The process executable path is unavailable.");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
}
