using System.Diagnostics;
using System.Reflection;

namespace CodexHistorySync.IntegrationTests;

public sealed class ReleaseSurfaceTests
{
    [Fact]
    public async Task Release_cli_reports_version_0_9_1_and_advertises_manager_mode()
    {
        var cliDirectory = Path.Combine(RepositoryRoot(), "src", "CodexHistorySync.Cli", "bin", "Release", "net10.0", "win-x64");
        var executable = Path.Combine(cliDirectory, "agent-sync.exe");
        var assembly = Path.Combine(cliDirectory, "agent-sync.dll");

        Assert.True(File.Exists(executable), $"Built release executable was not found: {executable}");
        Assert.Equal("0.9.1", AssemblyName.GetAssemblyName(assembly).Version!.ToString(3));

        var result = await RunAsync(executable, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage: agent-sync", result.Output);
        Assert.Contains("[--manage]", result.Output);
    }

    [Fact]
    public async Task Release_cli_answers_the_probe_that_gates_its_own_update()
    {
        // An update keeps a freshly installed binary only if it answers --help; one that
        // cannot would be rolled back on every machine that tried to install it.
        var executable = Path.Combine(RepositoryRoot(), "src", "CodexHistorySync.Cli", "bin", "Release",
            "net10.0", "win-x64", "agent-sync.exe");

        Assert.True(File.Exists(executable), $"Built release executable was not found: {executable}");

        var probe = await RunAsync(executable, "--help");
        var version = await RunAsync(executable, "--version");

        Assert.Equal(0, probe.ExitCode);
        Assert.Equal(0, version.ExitCode);
        Assert.Contains("agent-sync 0.9.1", version.Output);
    }

    [Fact]
    public async Task Release_scripts_accept_help_switches()
    {
        var root = RepositoryRoot();
        var installer = Path.Combine(root, "scripts", "install.ps1");
        var publisher = Path.Combine(root, "scripts", "publish-release.ps1");

        var installerHelp = await RunAsync("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", installer, "-?");
        var publisherHelp = await RunAsync("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", publisher, "-?");

        Assert.Equal(0, installerHelp.ExitCode);
        Assert.Equal(0, publisherHelp.ExitCode);
    }

    [Fact]
    public async Task Release_publisher_rejects_an_invalid_version_with_0_5_2_guidance()
    {
        var publisher = Path.Combine(RepositoryRoot(), "scripts", "publish-release.ps1");

        var invalidVersion = await RunAsync("powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", publisher, "-Version", "invalid-version");

        Assert.NotEqual(0, invalidVersion.ExitCode);
        Assert.Contains("Version must look like 0.5.2", invalidVersion.Output);
    }

    private static async Task<ProcessResult> RunAsync(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput + await standardError);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "scripts")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
