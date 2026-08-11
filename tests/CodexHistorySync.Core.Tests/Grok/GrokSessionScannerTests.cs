using System.Text.Json;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Grok;

public sealed class GrokSessionScannerTests
{
    [Fact]
    public async Task ScanDetailedAsyncUsesOneStabilityWindowAndRejectsAChangedCandidate()
    {
        // A per-candidate wait makes startup linear and does not represent one catalog snapshot.
        await using var fixture = new GrokHomeFixture();
        var stableChat = await fixture.WriteSessionAsync("10000000-0000-0000-0000-000000000001");
        var changingChat = await fixture.WriteSessionAsync("20000000-0000-0000-0000-000000000002");
        var waits = 0;
        var scanner = new GrokSessionScanner(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            waits++;
            await File.AppendAllTextAsync(
                changingChat,
                "{\"role\":\"assistant\",\"content\":\"changed\"}\n",
                cancellationToken);
        });

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(1, waits);
        Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(stableChat));
        Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(changingChat));
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.GrokSession));
    }

    private sealed class GrokHomeFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), $"codex-history-sync-grok-scanner-{Guid.NewGuid():N}");

        public GrokHomeFixture()
        {
            var home = Path.Combine(root, ".grok");
            var sessions = Path.Combine(home, "sessions");
            Directory.CreateDirectory(sessions);
            Paths = new GrokPaths(home, sessions);
        }

        public GrokPaths Paths { get; }

        public async Task<string> WriteSessionAsync(string sessionId)
        {
            var session = Paths.SessionDirectory(root, sessionId);
            Directory.CreateDirectory(session);
            var chat = Path.Combine(session, "chat_history.jsonl");
            await File.WriteAllTextAsync(chat, "{\"role\":\"user\",\"content\":\"question\"}\n");
            await File.WriteAllTextAsync(
                Path.Combine(session, "summary.json"),
                JsonSerializer.Serialize(new { info = new { id = sessionId, cwd = root } }));
            return chat;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
