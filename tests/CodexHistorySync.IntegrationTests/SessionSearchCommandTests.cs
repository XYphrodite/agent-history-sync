using CodexHistorySync.Cli;
using CodexHistorySync.Cli.Search;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;

namespace CodexHistorySync.IntegrationTests;

public sealed class SessionSearchCommandTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "chs-search-cmd-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Search_finds_a_phrase_that_lives_only_in_the_conversation()
    {
        var continueHome = Path.Combine(root, ".continue");
        var sessions = Path.Combine(continueHome, "sessions");
        Directory.CreateDirectory(sessions);
        var sessionId = "9490954d-d7dd-4cbe-984c-6172d60bf3dc";
        await File.WriteAllTextAsync(
            Path.Combine(sessions, sessionId + ".json"),
            """
            {"sessionId":"9490954d-d7dd-4cbe-984c-6172d60bf3dc","title":"hello","workspaceDirectory":"","history":[{"message":{"role":"user","content":[{"type":"text","text":"please document the unique xylophone handshake"}]}},{"message":{"role":"assistant","content":"done"}}]}
            """);

        var catalog = new LocalSessionCatalog(
            codexPaths: null,
            grokPaths: null,
            activeState: new IdleActiveState(),
            claudePaths: null,
            continuePaths: new ContinuePaths(continueHome, sessions));
        var console = new RecordingConsole();
        using var index = new SessionSearchIndex(root);
        var command = new SessionSearchCommand(
            catalog, index, new SessionContentReader(), console);

        var exitCode = await command.SearchAsync("xylophone handshake", CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("continue", console.Output, StringComparison.Ordinal);
        Assert.Contains(sessionId, console.Output, StringComparison.Ordinal);
        Assert.Contains("hello", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(sessions, console.Output, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class IdleActiveState : IManagedSessionActiveState
    {
        public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(
            ManagedAgent agent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<bool> IsActiveAsync(
            ManagedAgent agent, string sessionId, string nativePath, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class RecordingConsole : ICliConsole
    {
        private readonly StringWriter output = new();

        public string Output => output.ToString();

        public void WriteLine(string value) => output.WriteLine(value);
        public void WriteError(string value) => output.WriteLine(value);
        public Task<char[]> ReadSecretAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<char>());
    }
}
