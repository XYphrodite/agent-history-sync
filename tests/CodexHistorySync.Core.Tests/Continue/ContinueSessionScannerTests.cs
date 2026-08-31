using System.Text.Json.Nodes;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Continue;

public sealed class ContinueSessionScannerTests
{
    private const string StableId = "10000000-0000-0000-0000-000000000001";
    private const string ChangingId = "20000000-0000-0000-0000-000000000002";

    [Fact]
    public async Task ScanDetailedAsyncUsesOneStabilityWindowAndRejectsAChangedCandidate()
    {
        // One shared wait keeps startup flat; a per-candidate wait would make it linear.
        await using var fixture = new ContinueScanFixture();
        var stable = fixture.WriteSession(StableId, "stable", TimeSpan.FromHours(1));
        var changing = fixture.WriteSession(ChangingId, "changing", TimeSpan.FromHours(1));
        var waits = 0;
        var scanner = new ContinueSessionScanner(async cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            waits++;
            await File.AppendAllTextAsync(changing, " ", cancellationToken);
        });

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(1, waits);
        Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(stable));
        Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(changing));
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ContinueSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncDefersASessionWrittenInsideTheActivityWindow()
    {
        await using var fixture = new ContinueScanFixture();
        fixture.WriteSession(StableId, "being typed into", TimeSpan.Zero);
        var scanner = new ContinueSessionScanner(_ => Task.CompletedTask);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ContinueSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncPublishesAQuietSession()
    {
        await using var fixture = new ContinueScanFixture();
        var quiet = fixture.WriteSession(StableId, "finished", TimeSpan.FromHours(1));
        var scanner = new ContinueSessionScanner(_ => Task.CompletedTask);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        var item = Assert.Single(result.Objects);
        Assert.Equal(Path.GetFullPath(quiet), item.SourcePath);
        Assert.Equal(ObjectKind.ContinueSession, item.Kind);
        Assert.Equal("co-" + StableId, item.Id.Value);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.ContinueSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncNeverTreatsTheIndexAsASession()
    {
        // sessions.json sits in the same directory as the sessions. Scanning it as one would
        // publish the whole local session list as if it were a conversation.
        await using var fixture = new ContinueScanFixture();
        fixture.WriteSession(StableId, "real", TimeSpan.FromHours(1));
        File.SetLastWriteTimeUtc(fixture.Paths.IndexFilePath, DateTime.UtcNow - TimeSpan.FromHours(1));
        var scanner = new ContinueSessionScanner(_ => Task.CompletedTask);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Single(result.Objects);
        Assert.DoesNotContain(result.Objects, item => ContinuePaths.IsIndexFile(item.SourcePath));
    }

    [Fact]
    public async Task ScanDetailedAsyncPublishesASessionTheIndexDoesNotList()
    {
        await using var fixture = new ContinueScanFixture();
        var orphan = fixture.WriteSession(StableId, "unlisted", TimeSpan.FromHours(1), listInIndex: false);
        var scanner = new ContinueSessionScanner(_ => Task.CompletedTask);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(orphan), Assert.Single(result.Objects).SourcePath);
    }

    [Fact]
    public async Task ScanDetailedAsyncReportsUncertainWhenTheSessionsRootIsMissing()
    {
        await using var fixture = new ContinueScanFixture();
        var paths = new ContinuePaths(fixture.Paths.Home, Path.Combine(fixture.Paths.Home, "absent"));
        var scanner = new ContinueSessionScanner(_ => Task.CompletedTask);

        var result = await scanner.ScanDetailedAsync(paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ContinueSession));
    }

    [Fact]
    public async Task ScanDetailedAsyncDefersAnUnreadableSession()
    {
        await using var fixture = new ContinueScanFixture();
        var stable = fixture.WriteSession(StableId, "readable", TimeSpan.FromHours(1));
        var locked = fixture.WriteSession(ChangingId, "locked", TimeSpan.FromHours(1));
        var scanner = new ContinueSessionScanner(_ => Task.CompletedTask);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

            Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(stable));
            Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(locked));
            Assert.False(result.IsAbsenceConfirmed(ObjectKind.ContinueSession));
        }
    }

    [Fact]
    public async Task ScanDetailedAsyncIgnoresFilesBelowTheSessionsLevel()
    {
        await using var fixture = new ContinueScanFixture();
        var nested = Path.Combine(fixture.Paths.Sessions, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, StableId + ".json"),
            "{\"sessionId\":\"" + StableId + "\",\"title\":\"nested\",\"history\":[]}");
        var scanner = new ContinueSessionScanner(_ => Task.CompletedTask);

        var result = await scanner.ScanDetailedAsync(fixture.Paths, CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.ContinueSession));
    }

    private sealed class ContinueScanFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(), $"codex-history-sync-continue-scanner-{Guid.NewGuid():N}");
        private readonly List<JsonObject> entries = [];

        public ContinueScanFixture()
        {
            var home = Path.Combine(root, ".continue");
            var sessions = Path.Combine(home, "sessions");
            Directory.CreateDirectory(sessions);
            Paths = new ContinuePaths(home, sessions);
            File.WriteAllText(Paths.IndexFilePath, ContinueSessionIndex.Serialize(entries));
        }

        public ContinuePaths Paths { get; }

        public string WriteSession(string sessionId, string title, TimeSpan quietFor, bool listInIndex = true)
        {
            var path = Path.Combine(Paths.Sessions, sessionId + ".json");
            var document = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["title"] = title,
                ["workspaceDirectory"] = "file:///c%3A/Repos/Demo",
                ["history"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["message"] = new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = title } }
                        }
                    }
                },
                ["mode"] = "agent"
            };
            File.WriteAllText(path, document.ToJsonString());

            if (listInIndex)
            {
                entries.Add(ContinueSessionIndex.Synthesize(sessionId, title, "file:///c%3A/Repos/Demo",
                    DateTimeOffset.UtcNow, 1));
                File.WriteAllText(Paths.IndexFilePath, ContinueSessionIndex.Serialize(entries));
            }

            if (quietFor > TimeSpan.Zero) File.SetLastWriteTimeUtc(path, DateTime.UtcNow - quietFor);
            return path;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
