using System.Diagnostics;
using System.Reflection;

namespace CodexHistorySync.IntegrationTests;

public sealed class ReleaseSurfaceTests
{
    [Fact]
    public async Task Release_single_file_serves_mcp_and_reads_the_local_sqlite_catalog()
    {
        var executable = Path.Combine(RepositoryRoot(), "src", "CodexHistorySync.Cli", "bin", "Release",
            "net10.0", "win-x64", "publish", "agent-sync.exe");
        Assert.True(File.Exists(executable), $"Published executable was not found: {executable}");
        await using var fixture = await McpProcessFixture.StartAsync(executable);
        var initialized = await fixture.InitializeAsync();
        Assert.Equal(DeclaredVersion(), initialized.GetProperty("serverInfo").GetProperty("version").GetString());
        var found = SessionMcpProcessTests.Data(await fixture.CallAsync("search_sessions", new { query = "handshake" }));
        Assert.Single(found.GetProperty("sessions").EnumerateArray());
        var read = SessionMcpProcessTests.Data(await fixture.CallAsync("get_session", new
        {
            agent = "continue", session_id = McpProcessFixture.SessionId
        }));
        Assert.Contains(McpProcessFixture.UserText, read.GetProperty("text").GetString());
        await fixture.FinishAsync();
    }

    [Fact]
    public async Task Release_cli_reports_the_declared_version_and_advertises_manager_mode()
    {
        var cliDirectory = Path.Combine(RepositoryRoot(), "src", "CodexHistorySync.Cli", "bin", "Release", "net10.0", "win-x64");
        var executable = Path.Combine(cliDirectory, "agent-sync.exe");
        var assembly = Path.Combine(cliDirectory, "agent-sync.dll");

        Assert.True(File.Exists(executable), $"Built release executable was not found: {executable}");
        Assert.Equal(DeclaredVersion(), AssemblyName.GetAssemblyName(assembly).Version!.ToString(3));

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
        Assert.Contains($"agent-sync {DeclaredVersion()}", version.Output);
    }

    [Fact]
    public async Task Release_single_file_search_loads_sqlite_without_a_sidecar_dll()
    {
        // GitHub ships only agent-sync.exe. Native e_sqlite3 must come from inside that file,
        // or search dies with TypeInitializationException the moment SqliteConnection is
        // constructed — which is what 0.11.0 did on a clean install.
        var published = Path.Combine(RepositoryRoot(), "src", "CodexHistorySync.Cli", "bin", "Release",
            "net10.0", "win-x64", "publish", "agent-sync.exe");
        Assert.True(File.Exists(published), $"Published single-file executable was not found: {published}");

        var sandbox = Path.Combine(Path.GetTempPath(), "chs-sqlite-probe-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(sandbox, "data");
        Directory.CreateDirectory(sandbox);
        Directory.CreateDirectory(data);
        var isolated = Path.Combine(sandbox, "agent-sync.exe");
        File.Copy(published, isolated);
        try
        {
            var result = await RunAsync(
                isolated,
                ["search", "zzzz-no-such-phrase-sqlite-probe"],
                new Dictionary<string, string?>
                {
                    ["LOCALAPPDATA"] = data,
                    ["CODEX_HOME"] = Path.Combine(data, "missing-codex"),
                    ["GROK_HOME"] = Path.Combine(data, "missing-grok"),
                    ["CLAUDE_CONFIG_DIR"] = Path.Combine(data, "missing-claude"),
                    ["CONTINUE_GLOBAL_DIR"] = Path.Combine(data, "missing-continue")
                });

            Assert.DoesNotContain("TypeInitializationException", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("DllNotFoundException", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("Dll was not found", result.Output, StringComparison.Ordinal);
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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

    private static Task<ProcessResult> RunAsync(string fileName, params string[] arguments) =>
        RunAsync(fileName, arguments, environment: null);

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
                start.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput + await standardError);
    }

    /// <summary>
    /// The version the build declares, read rather than repeated. A number written here as well
    /// would have to be edited on the way to every release, and would fail the suite on the way
    /// to the ones where it was forgotten.
    /// </summary>
    /// <remarks>
    /// These tests read the release artifact already on disk. A stale one fails here rather than
    /// quietly passing, which is the point: it is the artifact a release would ship.
    /// Rebuild with <c>dotnet build src/CodexHistorySync.Cli -c Release -r win-x64</c>.
    /// </remarks>
    private static string DeclaredVersion()
    {
        var properties = Path.Combine(RepositoryRoot(), "Directory.Build.props");
        var match = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(properties), @"<Version>([^<]+)</Version>");
        Assert.True(match.Success, $"{properties} declares no <Version>.");
        return match.Groups[1].Value.Trim();
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
