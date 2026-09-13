using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli.Management;
using CodexHistorySync.Cli.Search;
using CodexHistorySync.Cli.Mcp;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using CodexHistorySync.Windows;
using Spectre.Console;

namespace CodexHistorySync.Cli;

internal sealed class DefaultSessionViewerRunner(SessionViewerApplication application) : ISessionManagerRunner
{
    private readonly SessionViewerApplication application = application ?? throw new ArgumentNullException(nameof(application));

    public Task RunAsync(CancellationToken cancellationToken) => application.RunAsync(cancellationToken);
}


internal sealed class DefaultSessionManagerRunner(SessionManagerApplication application) : ISessionManagerRunner
{
    private readonly SessionManagerApplication application = application ?? throw new ArgumentNullException(nameof(application));

    public Task RunAsync(CancellationToken cancellationToken) => application.RunAsync(cancellationToken);
}


public sealed class DefaultAgentCliOperations : IAgentCliOperations
{
    private readonly AgentWorker worker;
    private readonly AgentScheduler scheduler;
    private readonly Func<string?> executablePath;

    public DefaultAgentCliOperations(AgentWorker worker, AgentScheduler scheduler, Func<string?> executablePath)
    {
        this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
    }

    public Task RunAsync(CancellationToken cancellationToken) => worker.RunAsync(cancellationToken);

    public Task InstallAsync(CancellationToken cancellationToken)
    {
        var executable = executablePath();
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("The agent executable path is unavailable.");
        return scheduler.InstallAsync(executable, cancellationToken);
    }

    public Task UninstallAsync(CancellationToken cancellationToken) => scheduler.UninstallAsync(cancellationToken);
}


public sealed class SystemCliConsole : ICliConsole
{
    public void WriteLine(string value) => Console.Out.WriteLine(value);
    public void WriteError(string value) => Console.Error.WriteLine(value);

    public Task<char[]> ReadSecretAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Console.IsInputRedirected) throw new CliGateException("Passphrases require an interactive console.");
        Console.Error.Write(prompt);
        var result = new List<char>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (result.Count != 0) result.RemoveAt(result.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) result.Add(key.KeyChar);
        }
        Console.Error.WriteLine();
        return Task.FromResult(result.ToArray());
    }
}

