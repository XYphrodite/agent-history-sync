using System.Diagnostics;
using System.Text.Json;
using CodexHistorySync.Cli;

namespace CodexHistorySync.IntegrationTests;

public sealed class CliProfileTests
{
    [Theory]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("equals")]
    public void Global_profile_option_preserves_command_arguments(string placement)
    {
        string[] args = placement switch
        {
            "prefix" => ["--profile", "Xeon", "search", "two words"],
            "suffix" => ["search", "two words", "--profile", "Xeon"],
            _ => ["search", "--profile=Xeon", "two words"]
        };
        var parsed = CliProfileArguments.Parse(args);
        Assert.Equal("xeon", parsed.Name);
        Assert.Equal(["search", "two words"], parsed.Command);
        Assert.False(parsed.Select);
    }

    [Theory]
    [InlineData("--profile")]
    [InlineData("--profile=")]
    [InlineData("--profile=../escape")]
    [InlineData("--profile=bad/name")]
    [InlineData("--profile=xeon\n")]
    public void Invalid_profile_syntax_is_rejected(string option) =>
        Assert.Throws<CliProfileException>(() => CliProfileArguments.Parse(["push", option]));

    [Theory]
    [InlineData("--profile=one", "--profile=two")]
    [InlineData("--profile=one", "--select-profile")]
    [InlineData("--select-profile", "--select-profile")]
    public void Multiple_selectors_are_rejected(string first, string second) =>
        Assert.Throws<CliProfileException>(() => CliProfileArguments.Parse(["sync", first, second]));

    [Fact]
    public void Separator_allows_searching_for_literal_profile_flags()
    {
        var parsed = CliProfileArguments.Parse(["--profile", "reader", "search", "--", "--profile", "example"]);
        Assert.Equal("reader", parsed.Name);
        Assert.Equal(["search", "--profile", "example"], parsed.Command);
    }

    [Fact]
    public async Task Registering_and_removing_an_existing_profile_preserves_its_configuration_and_key()
    {
        using var fixture = new Fixture();
        var existing = Path.Combine(fixture.Root, "existing");
        Directory.CreateDirectory(Path.Combine(existing, "CodexHistorySync", "keys"));
        var config = Path.Combine(existing, "CodexHistorySync", "config.json");
        var key = Path.Combine(existing, "CodexHistorySync", "keys", "test.key");
        await File.WriteAllTextAsync(config, "existing configuration sentinel");
        await File.WriteAllBytesAsync(key, [1, 7, 9]);

        var profile = await fixture.Store.AddAsync("Xeon", existing, null, CancellationToken.None);
        Assert.Equal("xeon", profile.Name);
        Assert.Equal(existing, profile.DataDirectory);
        await Assert.ThrowsAsync<CliProfileException>(() => fixture.Store.AddAsync("XEON", fixture.Root, null, CancellationToken.None));
        await fixture.Store.RemoveAsync("xeon", CancellationToken.None);

        Assert.Equal("existing configuration sentinel", await File.ReadAllTextAsync(config));
        Assert.Equal(new byte[] { 1, 7, 9 }, await File.ReadAllBytesAsync(key));
        Assert.Equal("default", Assert.Single(await fixture.Store.ListAsync(CancellationToken.None)).Name);
    }

    [Fact]
    public async Task Concurrent_registrations_do_not_lose_a_profile()
    {
        using var fixture = new Fixture();
        var otherStore = new CliProfileStore(fixture.BaseData);
        await Task.WhenAll(fixture.Store.AddAsync("one", null, null, CancellationToken.None),
            otherStore.AddAsync("two", null, null, CancellationToken.None));
        Assert.Equal(["default", "one", "two"], (await fixture.Store.ListAsync(CancellationToken.None)).Select(profile => profile.Name));
    }

    [Fact]
    public async Task Listing_an_unconfigured_installation_does_not_create_files()
    {
        using var fixture = new Fixture();
        Assert.Equal(fixture.BaseData, Assert.Single(await fixture.Store.ListAsync(CancellationToken.None)).DataDirectory);
        Assert.False(Directory.Exists(fixture.BaseData));
    }

    [Fact]
    public async Task A_typo_never_runs_the_command_against_the_default_repository()
    {
        using var fixture = new Fixture();
        var console = new RecordingConsole();
        var called = false;
        var exit = await CliEntryPoint.RunAsync(["push", "--profile", "missing"], CancellationToken.None,
            run: (_, _) => { called = true; return Task.FromResult(0); }, console: console, store: fixture.Store);
        Assert.Equal(2, exit);
        Assert.False(called);
        Assert.Contains("Unknown profile 'missing'", console.Errors);
    }

    [Fact]
    public async Task Commands_without_a_selector_keep_working_with_a_broken_profile_registry()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Store.RegistryPath)!);
        await File.WriteAllTextAsync(fixture.Store.RegistryPath, "malformed registry");
        var exit = await CliEntryPoint.RunAsync(["update", "--check"], CancellationToken.None,
            run: (args, _) => { Assert.Equal(["update", "--check"], args); return Task.FromResult(0); },
            console: new RecordingConsole(), store: fixture.Store);
        Assert.Equal(0, exit);
        Assert.Equal(fixture.BaseData, (await fixture.Store.ResolveAsync("default", CancellationToken.None)).DataDirectory);
        await Assert.ThrowsAsync<CliProfileException>(() => fixture.Store.ResolveAsync("reader", CancellationToken.None));
    }

    [Fact]
    public async Task Selecting_a_profile_routes_all_homes_and_restores_environment_after_failure()
    {
        using var fixture = new Fixture();
        var data = Path.Combine(fixture.Root, "reader-data");
        var homes = Path.Combine(fixture.Root, "reader-homes");
        await fixture.Store.AddAsync("reader", data, homes, CancellationToken.None);
        var environment = new Dictionary<string, string?>
        {
            ["LOCALAPPDATA"] = "old-data", ["CODEX_HOME"] = "old-codex", ["GROK_HOME"] = null,
            ["CLAUDE_CONFIG_DIR"] = "old-claude", ["CONTINUE_GLOBAL_DIR"] = "old-continue"
        };
        var original = environment.ToDictionary(pair => pair.Key, pair => pair.Value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CliEntryPoint.RunAsync(
            ["--sessions", "--profile", "reader"], CancellationToken.None, console: new RecordingConsole(), store: fixture.Store,
            apply: profile => new CliProfileEnvironment(profile, name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value),
            run: (args, _) =>
            {
                Assert.Equal(["--sessions"], args);
                Assert.Equal(data, environment["LOCALAPPDATA"]);
                Assert.Equal(Path.Combine(homes, "codex"), environment["CODEX_HOME"]);
                Assert.Equal(Path.Combine(homes, "grok"), environment["GROK_HOME"]);
                Assert.Equal(Path.Combine(homes, "claude"), environment["CLAUDE_CONFIG_DIR"]);
                Assert.Equal(Path.Combine(homes, "continue"), environment["CONTINUE_GLOBAL_DIR"]);
                throw new InvalidOperationException("test failure");
            }));
        Assert.Equal(original.OrderBy(pair => pair.Key), environment.OrderBy(pair => pair.Key));
        Assert.False(Directory.Exists(homes));
    }

    [Fact]
    public async Task Interactive_selection_applies_only_to_that_invocation()
    {
        using var fixture = new Fixture();
        await fixture.Store.AddAsync("reader", null, null, CancellationToken.None);
        var applied = new List<string>();
        var console = new RecordingConsole();
        var exit = await CliEntryPoint.RunAsync(["status", "--select-profile"], CancellationToken.None,
            console: console, store: fixture.Store,
            select: (profiles, _) =>
            {
                Assert.Equal(["default", "reader"], profiles.Select(profile => profile.Name));
                return Task.FromResult<string?>("reader");
            },
            apply: profile => { applied.Add(profile.Name); return new EmptyScope(); },
            run: (args, _) => { Assert.Equal(["status"], args); return Task.FromResult(0); });
        Assert.Equal(0, exit);
        Assert.Equal(["reader"], applied);
        Assert.Contains("Profile: reader", console.Output);
        Assert.False(File.Exists(Path.Combine(fixture.BaseData, "CodexHistorySync", "config.json")));
    }

    [Fact]
    public async Task Desktop_profile_selection_starts_the_viewer_on_the_original_sta_thread()
    {
        using var fixture = new Fixture();
        await fixture.Store.AddAsync("reader", null, null, CancellationToken.None);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var entryThread = Environment.CurrentManagedThreadId;
                var exit = CliEntryPoint.RunAsync(["--sessions", "--select-profile"], CancellationToken.None,
                    console: new RecordingConsole(), store: fixture.Store, apply: _ => new EmptyScope(),
                    select: async (_, _) => { await Task.Yield(); return "reader"; },
                    run: (args, _) =>
                    {
                        Assert.Equal(["--sessions"], args);
                        Assert.Equal(entryThread, Environment.CurrentManagedThreadId);
                        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                        return Task.FromResult(0);
                    }).GetAwaiter().GetResult();
                completion.SetResult(exit);
            }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.Equal(0, await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Named_profiles_cannot_silently_install_a_default_background_task()
    {
        using var fixture = new Fixture();
        await fixture.Store.AddAsync("xeon", null, null, CancellationToken.None);
        var called = false;
        var exit = await CliEntryPoint.RunAsync(["agent", "install", "--profile", "xeon"], CancellationToken.None,
            store: fixture.Store, console: new RecordingConsole(),
            run: (_, _) => { called = true; return Task.FromResult(0); });
        Assert.Equal(2, exit);
        Assert.False(called);
    }

    [Fact]
    public async Task Separate_processes_search_only_the_selected_history_and_write_separate_catalogs()
    {
        using var fixture = new Fixture();
        var one = Path.Combine(fixture.Root, "one-data");
        var two = Path.Combine(fixture.Root, "two-data");
        var oneHomes = Path.Combine(fixture.Root, "one-homes");
        var twoHomes = Path.Combine(fixture.Root, "two-homes");
        await WriteSessionAsync(oneHomes, "alphamarkerprofile");
        await WriteSessionAsync(twoHomes, "betamarkerprofile");
        Assert.Equal(0, (await fixture.RunAsync("profile", "add", "one", "--data-dir", one, "--sessions-dir", oneHomes)).Exit);
        Assert.Equal(0, (await fixture.RunAsync("profile", "add", "two", "--data-dir", two, "--sessions-dir", twoHomes)).Exit);

        var first = await fixture.RunAsync("search", "alphamarkerprofile", "--profile", "one");
        Assert.Equal(0, first.Exit);
        Assert.Contains("alphamarkerprofile", first.Output);
        Assert.DoesNotContain("betamarkerprofile", first.Output);
        Assert.True(File.Exists(Path.Combine(one, "CodexHistorySync", "catalog.db")));
        Assert.False(File.Exists(Path.Combine(two, "CodexHistorySync", "catalog.db")));
        Assert.False(File.Exists(Path.Combine(fixture.BaseData, "CodexHistorySync", "catalog.db")));

        var second = await fixture.RunAsync("--profile=two", "search", "betamarkerprofile");
        Assert.Equal(0, second.Exit);
        Assert.Contains("betamarkerprofile", second.Output);
        Assert.True(File.Exists(Path.Combine(two, "CodexHistorySync", "catalog.db")));
        var noLeak = await fixture.RunAsync("search", "alphamarkerprofile", "--profile", "two");
        Assert.Equal(1, noLeak.Exit);
        Assert.Empty(noLeak.Output);
    }

    [Fact]
    public async Task Interactive_selector_fails_promptly_when_input_is_redirected()
    {
        using var fixture = new Fixture();
        var result = await fixture.RunAsync("status", "--select-profile");
        Assert.Equal(2, result.Exit);
        Assert.Contains("requires a terminal", result.Errors);
    }

    [Fact]
    public async Task Search_failure_report_stays_inside_the_selected_profile()
    {
        using var fixture = new Fixture();
        var data = Path.Combine(fixture.Root, "reader-data");
        var homes = Path.Combine(fixture.Root, "reader-homes");
        await WriteSessionAsync(homes, "searchprofilelog");
        await fixture.Store.AddAsync("reader", data, homes, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(data, "CodexHistorySync"));
        await File.WriteAllTextAsync(Path.Combine(data, "CodexHistorySync", "catalog.db"), "invalid sqlite database");
        var result = await fixture.RunAsync("search", "searchprofilelog", "--profile", "reader");
        Assert.Equal(1, result.Exit);
        Assert.Contains("Operation failed", result.Errors);
        Assert.True(File.Exists(Path.Combine(data, "CodexHistorySync", "logs", "last-failure.log")));
        Assert.False(File.Exists(Path.Combine(fixture.BaseData, "CodexHistorySync", "logs", "last-failure.log")));
    }

    private static Task WriteSessionAsync(string homes, string text)
    {
        var sessions = Directory.CreateDirectory(Path.Combine(homes, "continue", "sessions")).FullName;
        return File.WriteAllTextAsync(Path.Combine(sessions, McpProcessFixture.SessionId + ".json"), JsonSerializer.Serialize(new
        {
            sessionId = McpProcessFixture.SessionId, title = text, workspaceDirectory = "",
            history = new[] { new { message = new { role = "user", content = text } } }
        }));
    }

    private sealed class RecordingConsole : ICliConsole
    {
        private readonly List<string> errors = [];
        private readonly List<string> output = [];
        internal string Errors => string.Join('\n', errors);
        internal string Output => string.Join('\n', output);
        public void WriteLine(string value) => output.Add(value);
        public void WriteError(string value) => errors.Add(value);
        public Task<char[]> ReadSecretAsync(string prompt, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected secret prompt.");
    }

    private sealed class EmptyScope : IDisposable { public void Dispose() { } }

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "chs-profiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            BaseData = Path.Combine(Root, "base-data");
            Store = new CliProfileStore(BaseData);
        }

        internal string Root { get; }
        internal string BaseData { get; }
        internal CliProfileStore Store { get; }

        internal async Task<(int Exit, string Output, string Errors)> RunAsync(params string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(typeof(Fixture).Assembly.Location, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(typeof(Fixture).Assembly.Location, ".deps.json"), typeof(CliApplication).Assembly.Location }.Concat(args))
                start.ArgumentList.Add(argument);
            start.Environment["LOCALAPPDATA"] = BaseData;
            foreach (var variable in new[] { "CODEX_HOME", "GROK_HOME", "CLAUDE_CONFIG_DIR", "CONTINUE_GLOBAL_DIR" })
                start.Environment[variable] = Path.Combine(Root, "missing-" + variable);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            return (process.ExitCode, await output, await errors);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
