using CodexHistorySync.Cli;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.IntegrationTests;

/// <summary>
/// `agent-sync titles` is the supported way in and out of session titling, so the file it writes
/// is checked through the command rather than through the store behind it.
/// </summary>
public sealed class CliTitlesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"chs-cli-titles-{Guid.NewGuid():N}");

    [Fact]
    public async Task Titles_reports_that_nothing_is_configured_and_says_how_to_turn_it_on()
    {
        var fixture = new TitlesFixture(_root);

        var exitCode = await fixture.RunAsync("titles");

        Assert.Equal(0, exitCode);
        Assert.Contains("titling=off", fixture.Console.OutputText, StringComparison.Ordinal);
        Assert.Contains("agent-sync titles set", fixture.Console.OutputText, StringComparison.Ordinal);
        Assert.Empty(fixture.Console.ErrorText);
    }

    [Fact]
    public async Task Titles_set_stores_the_endpoint_and_shows_it_back()
    {
        var fixture = new TitlesFixture(_root);

        var exitCode = await fixture.RunAsync("titles", "set", "http://100.105.87.52:11434");

        Assert.Equal(0, exitCode);
        Assert.Contains("titling=on", fixture.Console.OutputText, StringComparison.Ordinal);
        Assert.Contains("endpoint=http://100.105.87.52:11434", fixture.Console.OutputText, StringComparison.Ordinal);
        Assert.Contains($"model={SessionTitleOptions.DefaultModel}", fixture.Console.OutputText, StringComparison.Ordinal);
        Assert.Contains("language=auto", fixture.Console.OutputText, StringComparison.Ordinal);

        var stored = SessionTitleConfiguration.Load(_root);
        Assert.True(stored.IsConfigured);
        Assert.Equal("http://100.105.87.52:11434", stored.Options.Endpoint);
    }

    [Fact]
    public async Task Titles_set_takes_a_model_and_a_language()
    {
        var fixture = new TitlesFixture(_root);

        var exitCode = await fixture.RunAsync(
            "titles", "set", "http://127.0.0.1:11434", "--model", "gpt-oss:20b", "--language", "ru");

        Assert.Equal(0, exitCode);
        var stored = SessionTitleConfiguration.Load(_root);
        Assert.Equal("gpt-oss:20b", stored.Options.Model);
        Assert.Equal("ru", stored.Options.Language);
    }

    [Fact]
    public async Task Titles_set_refuses_an_endpoint_off_this_machine_and_stores_nothing()
    {
        // Refused where it is typed, rather than stored and quietly ignored on the next load.
        var fixture = new TitlesFixture(_root);

        var exitCode = await fixture.RunAsync("titles", "set", "https://api.openai.com/v1");

        Assert.Equal(2, exitCode);
        Assert.Contains("Endpoint refused", fixture.Console.ErrorText, StringComparison.Ordinal);
        Assert.False(SessionTitleConfiguration.Load(_root).IsConfigured);
        Assert.False(File.Exists(SessionTitleConfiguration.PathFor(_root)));
    }

    [Fact]
    public async Task Titles_set_refuses_a_language_it_does_not_speak()
    {
        var fixture = new TitlesFixture(_root);

        var exitCode = await fixture.RunAsync(
            "titles", "set", "http://127.0.0.1:11434", "--language", "klingon");

        Assert.NotEqual(0, exitCode);
        Assert.False(SessionTitleConfiguration.Load(_root).IsConfigured);
    }

    [Fact]
    public async Task Titles_off_removes_the_configuration()
    {
        var fixture = new TitlesFixture(_root);
        await fixture.RunAsync("titles", "set", "http://127.0.0.1:11434");

        var exitCode = await fixture.RunAsync("titles", "off");

        Assert.Equal(0, exitCode);
        Assert.Contains("titling=off", fixture.Console.OutputText, StringComparison.Ordinal);
        Assert.False(SessionTitleConfiguration.Load(_root).IsConfigured);
    }

    [Fact]
    public async Task Titles_off_says_so_when_there_was_nothing_to_remove()
    {
        var fixture = new TitlesFixture(_root);

        Assert.Equal(0, await fixture.RunAsync("titles", "off"));
        Assert.Contains("nothing was configured", fixture.Console.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Titles_test_refuses_to_probe_what_is_not_configured()
    {
        var fixture = new TitlesFixture(_root);

        var exitCode = await fixture.RunAsync("titles", "test");

        Assert.Equal(2, exitCode);
        Assert.Contains("not configured", fixture.Console.ErrorText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("titles", "set")]
    [InlineData("titles", "nonsense")]
    [InlineData("titles", "off", "extra")]
    public async Task Titles_refuses_a_command_shape_it_does_not_know(params string[] args)
    {
        var fixture = new TitlesFixture(_root);

        Assert.NotEqual(0, await fixture.RunAsync(args));
    }

    [Fact]
    public async Task Help_lists_the_titles_command()
    {
        var fixture = new TitlesFixture(_root);

        await fixture.RunAsync("--help");

        Assert.Contains("titles set <endpoint>", fixture.Console.OutputText, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class TitlesFixture
    {
        private readonly CliApplication _application;

        public TitlesFixture(string root)
        {
            Directory.CreateDirectory(root);
            _application = new CliApplication(
                new UnusedServices(), Console, localAppDataDirectory: root);
        }

        public RecordingConsole Console { get; } = new();

        public Task<int> RunAsync(params string[] args) => _application.RunAsync(args, CancellationToken.None);
    }

    /// <summary>Titling touches no repository, so every service call here is a defect.</summary>
    private sealed class UnusedServices : ICliServices
    {
        public Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken) => throw Unused();
        public Task<CliGateResult> VerifyPrivateRepositoryAsync(string remoteUrl, CancellationToken cancellationToken) => throw Unused();
        public Task<CliInitializationResult> InitializeAsync(string remoteUrl, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken) => throw Unused();
        public Task<CliAuthenticatedRepository> AuthenticateRepositoryAsync(string remoteUrl, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken) => throw Unused();
        public Task<CliGateResult> ProbeCompatibilityAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken) => throw Unused();
        public Task<CompatibilityResult> ProbeCompatibilitySessionAsync(string sourceSession, string codexExecutable, CancellationToken cancellationToken) => throw Unused();
        public Task<CliJoinPlan> PlanJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken) => throw Unused();
        public Task<SyncResult> ApplyJoinAsync(CliAuthenticatedRepository repository, CliJoinPlan plan, CancellationToken cancellationToken) => throw Unused();
        public Task AbortJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken) => throw Unused();
        public Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken) => throw Unused();
        public Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken) => throw Unused();
        public Task<CliDoctorReport> RunDoctorAsync(CancellationToken cancellationToken) => throw Unused();
        public Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CancellationToken cancellationToken) => throw Unused();
        public Task<CliResolutionResult> ResolveAsync(string conflictId, CliResolution resolution, string? exportDirectory, CancellationToken cancellationToken) => throw Unused();

        private static NotSupportedException Unused() =>
            new("agent-sync titles must not reach the repository.");
    }

    private sealed class RecordingConsole : ICliConsole
    {
        private readonly StringWriter _output = new();
        private readonly StringWriter _error = new();

        public string OutputText => _output.ToString();
        public string ErrorText => _error.ToString();

        public void WriteLine(string value) => _output.WriteLine(value);
        public void WriteError(string value) => _error.WriteLine(value);

        public Task<char[]> ReadSecretAsync(string prompt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("agent-sync titles must not ask for a secret.");
    }
}
