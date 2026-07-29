using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexHistorySync.Git;

public sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public sealed class GitCommand
{
    private const int MaximumCapturedCharacters = 1_048_576;
    private static readonly Regex CredentialUrl = new(
        @"(?<scheme>[a-z][a-z0-9+.-]*://)(?<credentials>[^\s/@]+@)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CredentialQuery = new(
        "(?<prefix>[?&][^=\\s&?#]+)=(?<value>[^&\\s#]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ScpCredential = new(
        @"(?<![\w.-])(?<credential>(?!git@)[^\s/@:]+)@(?<host>[^\s/:]+):",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public GitCommand(string executable = "git", TimeSpan? timeout = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable)
            ? throw new ArgumentException("Git executable is required.", nameof(executable))
            : executable;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<GitCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(string.IsNullOrEmpty)) throw new ArgumentException("Git arguments cannot be empty.", nameof(arguments));
        var startInfo = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git process could not be started.");
            using var timeout = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var stdout = CaptureAsync(process.StandardOutput, CancellationToken.None);
            var stderr = CaptureAsync(process.StandardError, CancellationToken.None);
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            return new GitCommandResult(
                cancelled ? -1 : process.ExitCode,
                Redact(output),
                Redact(error),
                cancelled && timeout.IsCancellationRequested);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Unable to start Git: {Redact(exception.Message)}");
        }
    }

    internal static string Redact(string value)
    {
        var redacted = CredentialUrl.Replace(value, "${scheme}***@");
        redacted = CredentialQuery.Replace(redacted, "${prefix}=***");
        return ScpCredential.Replace(redacted, "***@${host}:");
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var result = new StringBuilder();
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var remaining = MaximumCapturedCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(remaining, read));
            truncated |= read > remaining;
        }
        if (truncated) result.Append("\n[output truncated]");
        return result.ToString();
    }
}
