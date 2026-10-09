using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Hermes;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Hermes;

public sealed class HermesCopyDestinationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hermes-copy-dest-" + Guid.NewGuid().ToString("N"));

    public HermesCopyDestinationTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Labels_NameWindowsAndWslSeparately()
    {
        var labels = CopyDestination.LabelsForHermesHomes(
        [
            @"C:\Users\Gamer\.hermes",
            @"\\wsl.localhost\Ubuntu\home\gamer\.hermes"
        ]);

        Assert.Equal(["Hermes (Windows)", "Hermes (WSL)"], labels);
    }

    [Fact]
    public void Labels_AddTheDistroWhenTwoWslHomesWouldOtherwiseMatch()
    {
        var labels = CopyDestination.LabelsForHermesHomes(
        [
            @"\\wsl.localhost\Ubuntu\home\gamer\.hermes",
            @"\\wsl.localhost\Debian\home\gamer\.hermes"
        ]);

        Assert.Equal(["Hermes (WSL Ubuntu)", "Hermes (WSL Debian)"], labels);
    }

    [Fact]
    public void AvailableCopyDestinations_ListsEachHermesHome()
    {
        var windows = Home("windows");
        var wsl = Home(Path.Combine("wsl.localhost", "Ubuntu", "home", "gamer"));
        var operations = Operations(WithCompanions(windows, wsl));
        var source = new ManagedSession(
            ManagedAgent.Codex, "codex-1", Path.Combine(root, "missing.jsonl"), "title",
            DateTimeOffset.UnixEpoch, false, true);

        var hermes = operations.AvailableCopyDestinations(source)
            .Where(destination => destination.Agent == ManagedAgent.Hermes)
            .ToArray();

        Assert.Equal(2, hermes.Length);
        Assert.Equal("Hermes (Windows)", hermes[0].Label);
        Assert.Equal("Hermes (WSL)", hermes[1].Label);
        Assert.Equal(Path.GetFullPath(windows.Home), hermes[0].HermesHome);
        Assert.Equal(Path.GetFullPath(wsl.Home), hermes[1].HermesHome);
    }

    [Fact]
    public void AvailableCopyDestinations_SkipsTheHomeTheSessionAlreadyLivesIn()
    {
        var windows = Home("windows");
        var wsl = Home(Path.Combine("wsl.localhost", "Ubuntu", "home", "gamer"));
        var operations = Operations(WithCompanions(windows, wsl));
        var source = new ManagedSession(
            ManagedAgent.Hermes,
            "20250305_091523_a1b2c3d4",
            windows.AnchorPath(HermesPaths.DefaultProfileName, "20250305_091523_a1b2c3d4"),
            "title",
            DateTimeOffset.UnixEpoch,
            false,
            true);

        var hermes = operations.AvailableCopyDestinations(source)
            .Where(destination => destination.Agent == ManagedAgent.Hermes)
            .ToArray();

        var only = Assert.Single(hermes);
        Assert.Equal("Hermes (WSL)", only.Label);
        Assert.Equal(Path.GetFullPath(wsl.Home), only.HermesHome);
    }

    [Fact]
    public async Task CopyAsync_WritesIntoTheChosenHermesHome()
    {
        var windows = Home("windows");
        var wsl = Home(Path.Combine("wsl.localhost", "Ubuntu", "home", "gamer"));
        var sessions = Path.Combine(root, "codex", "sessions");
        var archived = Path.Combine(root, "codex", "archived");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archived);
        var codex = new CodexPaths(Path.Combine(root, "codex"), sessions, archived, Path.Combine(root, "codex", "attachments"));
        var sessionId = "codex-into-wsl";
        var path = Path.Combine(sessions, "rollout-" + sessionId + ".jsonl");
        await File.WriteAllTextAsync(path, CodexTranscript(sessionId));
        var operations = new LocalSessionOperations(
            codex,
            null,
            new Idle(),
            new UnusedDeleter(),
            null,
            null,
            new CodexHistorySync.Core.Conversion.CodexConversationReader(),
            new CodexHistorySync.Core.Conversion.GrokConversationReader(),
            hermesPaths: WithCompanions(windows, wsl),
            hermesWriter: new CodexHistorySync.Core.Conversion.HermesConversationWriter(windows));
        var source = new ManagedSession(
            ManagedAgent.Codex, sessionId, path, "Copied title", DateTimeOffset.UnixEpoch, false, true);
        var wslDestination = operations.AvailableCopyDestinations(source)
            .Single(destination => destination.Label == "Hermes (WSL)");

        var copiedId = await operations.CopyAsync(source, wslDestination, CancellationToken.None);

        var written = HermesSessionDatabase.ReadOne(wsl, HermesPaths.DefaultProfileName, copiedId);
        Assert.NotNull(written);
        Assert.Contains(written.Messages, message => message.Any(cell => cell.Text == "question from codex"));
        Assert.Null(HermesSessionDatabase.ReadOne(windows, HermesPaths.DefaultProfileName, copiedId));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private HermesPaths Home(string relative)
    {
        var home = Path.Combine(root, relative);
        Directory.CreateDirectory(home);
        return new HermesPaths(home);
    }

    private static HermesPaths WithCompanions(HermesPaths primary, params HermesPaths[] companions) =>
        primary with { Companions = companions };

    private static LocalSessionOperations Operations(HermesPaths hermes) =>
        new(null, null, new Idle(), new UnusedDeleter(), null, null, hermesPaths: hermes,
            hermesWriter: new CodexHistorySync.Core.Conversion.HermesConversationWriter(hermes));

    private static string CodexTranscript(string id) =>
        string.Join('\n',
            JsonSerializer.Serialize(new
            {
                type = "session_meta",
                payload = new { id, timestamp = "2026-08-09T08:00:00Z", cwd = @"C:\Repos\Demo", title = "Copied title" }
            }),
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                payload = new
                {
                    type = "message",
                    role = "user",
                    timestamp = "2026-08-09T09:00:00Z",
                    content = new[] { new { type = "input_text", text = "question from codex" } }
                }
            }),
            JsonSerializer.Serialize(new
            {
                type = "response_item",
                payload = new
                {
                    type = "message",
                    role = "assistant",
                    timestamp = "2026-08-09T10:00:00Z",
                    content = new[] { new { type = "output_text", text = "answer" } }
                }
            })) + "\n";

    private sealed class Idle : IManagedSessionActiveState
    {
        public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(ManagedAgent agent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<bool> IsActiveAsync(ManagedAgent agent, string sessionId, string nativePath, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class UnusedDeleter : IManagedSessionDirectoryDeleter
    {
        public Task DeleteAsync(string sessionsRoot, string sessionDirectory, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
