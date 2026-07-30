using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Codex;

public sealed class SessionScannerTests
{
    [Fact]
    public async Task ScanAsyncReturnsOnlyStableSessionJsonlFiles()
    {
        // Returning arbitrary files would leak credentials or local state.
        await using var fixture = await CodexHomeFixture.CreateAsync();
        var active = await fixture.WriteSessionAsync("sessions", "active.jsonl", "active-thread");
        var archived = await fixture.WriteSessionAsync("archived_sessions", "archived.jsonl", "archived-thread");
        await fixture.WriteSessionAsync("sessions", "state.sqlite.jsonl", "sqlite-disguised-thread");
        await fixture.WriteFileAsync("auth.json", "credential");
        await fixture.WriteFileAsync("state_5.sqlite", "state");
        await fixture.WriteFileAsync("logs_2.sqlite-wal", "state");
        await fixture.WriteFileAsync(".sandbox-secrets", "secret");
        await fixture.WriteFileAsync("tmp\\scratch.jsonl", "not a session\n");

        var found = await new SessionScanner(TimeSpan.Zero).ScanAsync(CodexPaths.Resolve(fixture.Home), CancellationToken.None);

        Assert.Contains(found, item => item.Kind == ObjectKind.ActiveSession && item.SourcePath == Path.GetFullPath(active));
        Assert.Contains(found, item => item.Kind == ObjectKind.ArchivedSession && item.SourcePath == Path.GetFullPath(archived));
        Assert.DoesNotContain(found, x => x.SourcePath.Contains("auth.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, x => x.SourcePath.Contains("sqlite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, x => x.SourcePath.Contains("sandbox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsyncDoesNotTreatAttachmentLikeJsonStringsAsAttachmentPaths()
    {
        // Broad JSON string discovery would allow a session to export arbitrary local files.
        await using var fixture = await CodexHomeFixture.CreateAsync();
        var outside = Path.Combine(Path.GetTempPath(), $"codex-history-sync-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "outside attachment");
        var attachment = await fixture.WriteFileAsync("attachments\\unreferenced.txt", "attachment");
        var attachmentLikeRecord = JsonSerializer.Serialize(new
        {
            type = "event",
            payload = new
            {
                absolute = outside,
                traversal = "..\\outside.txt",
                lookalike = "attachments-evil\\secret.txt",
                unrelated = "attachments\\unreferenced.txt"
            }
        });

        try
        {
            await fixture.WriteSessionAsync(
                "sessions",
                "safe.jsonl",
                "safe-thread",
                attachmentLikeRecord);

            var found = await new SessionScanner(TimeSpan.Zero).ScanAsync(CodexPaths.Resolve(fixture.Home), CancellationToken.None);

            Assert.Single(found);
            Assert.Equal(ObjectKind.ActiveSession, found[0].Kind);
            Assert.DoesNotContain(found, x => x.Kind == ObjectKind.Attachment || x.SourcePath.Equals(Path.GetFullPath(attachment), StringComparison.OrdinalIgnoreCase) || x.SourcePath.Equals(Path.GetFullPath(outside), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("missing-final-newline", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"missing-final-newline\"}}")]
    [InlineData("malformed-json", "{not-json}\n")]
    [InlineData("non-object-json", "[]\n")]
    [InlineData("traversal-id", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"..\\\\outside\"}}\n")]
    public async Task ScanAsyncSkipsUnsafeOrIncompleteSessionFiles(string fileName, string content)
    {
        // Accepting incomplete, malformed, or traversal-shaped sessions corrupts the sync boundary.
        await using var fixture = await CodexHomeFixture.CreateAsync();
        await fixture.WriteFileAsync($"sessions\\{fileName}.jsonl", content);

        var found = await new SessionScanner(TimeSpan.Zero).ScanAsync(CodexPaths.Resolve(fixture.Home), CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact]
    public async Task ScanDetailedAsync_MarksKindUncertainWhenCandidateIsIncomplete()
    {
        await using var fixture = await CodexHomeFixture.CreateAsync();
        await fixture.WriteFileAsync("sessions\\possibly-live.jsonl", "{\"type\":\"session_meta\",\"payload\":{\"id\":\"possibly-live\"}}");

        var result = await new SessionScanner(TimeSpan.Zero).ScanDetailedAsync(CodexPaths.Resolve(fixture.Home), CancellationToken.None);

        Assert.Empty(result.Objects);
        Assert.False(result.IsAbsenceConfirmed(ObjectKind.ActiveSession));
        Assert.True(result.IsAbsenceConfirmed(ObjectKind.ArchivedSession));
    }

    [Fact]
    public async Task ScanAsyncSkipsLaterSessionWithDuplicateLogicalId()
    {
        // Accepting both copies would make a logical object resolve nondeterministically.
        await using var fixture = await CodexHomeFixture.CreateAsync();
        var first = await fixture.WriteSessionAsync("sessions", "first.jsonl", "duplicate-thread");
        await fixture.WriteSessionAsync("archived_sessions", "second.jsonl", "duplicate-thread");

        var found = await new SessionScanner(TimeSpan.Zero).ScanAsync(CodexPaths.Resolve(fixture.Home), CancellationToken.None);

        var session = Assert.Single(found);
        Assert.Equal(Path.GetFullPath(first), session.SourcePath);
    }

    [Fact]
    public async Task ScanAsyncSkipsNonStringRecordTypeAndContinuesToValidSession()
    {
        // Calling GetString on a non-string type must not prevent another session from being discovered.
        await using var fixture = await CodexHomeFixture.CreateAsync();
        await fixture.WriteFileAsync("sessions\\00-malformed.jsonl", "{\"type\":1}\n");
        var valid = await fixture.WriteSessionAsync("sessions", "99-valid.jsonl", "valid-after-malformed");

        var found = await new SessionScanner(TimeSpan.Zero).ScanAsync(CodexPaths.Resolve(fixture.Home), CancellationToken.None);

        var session = Assert.Single(found);
        Assert.Equal(Path.GetFullPath(valid), session.SourcePath);
    }

    [Fact]
    public async Task ScanAsyncSkipsFilesInDisallowedNestedDirectories()
    {
        // Scanning machine and temporary state directories would export data outside the session contract.
        await using var fixture = await CodexHomeFixture.CreateAsync();
        var disallowedDirectories = new[] { "LoGs", "CACHE", "tmp", "temp", ".sandbox", ".sandbox-secrets", "machine-id" };
        var skippedPaths = new List<string>();

        foreach (var directory in disallowedDirectories)
        {
            skippedPaths.Add(await fixture.WriteSessionAsync(Path.Combine("sessions", "2026", "07", "28", directory, "nested"), $"{directory}-active.jsonl", $"active-{directory}"));
            skippedPaths.Add(await fixture.WriteSessionAsync(Path.Combine("archived_sessions", "2026", "07", "28", directory, "nested"), $"{directory}-archived.jsonl", $"archived-{directory}"));
        }

        var active = await fixture.WriteSessionAsync(Path.Combine("sessions", "2026", "07", "28"), "active.jsonl", "allowed-active");
        var archived = await fixture.WriteSessionAsync(Path.Combine("archived_sessions", "2026", "07", "28"), "archived.jsonl", "allowed-archived");

        var found = await new SessionScanner(TimeSpan.Zero).ScanAsync(CodexPaths.Resolve(fixture.Home), CancellationToken.None);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, item => item.SourcePath == Path.GetFullPath(active));
        Assert.Contains(found, item => item.SourcePath == Path.GetFullPath(archived));
        Assert.DoesNotContain(found, item => skippedPaths.Contains(item.SourcePath, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveUsesConfiguredHomeAndReturnsCanonicalChildPaths()
    {
        // Losing explicit-home precedence could scan the caller's real Codex profile.
        await using var fixture = await CodexHomeFixture.CreateAsync();
        var relativeHome = Path.GetRelativePath(Directory.GetCurrentDirectory(), fixture.Home);

        var paths = CodexPaths.Resolve(relativeHome);

        Assert.Equal(Path.GetFullPath(fixture.Home), paths.Home);
        Assert.Equal(Path.Combine(Path.GetFullPath(fixture.Home), "sessions"), paths.Sessions);
        Assert.Equal(Path.Combine(Path.GetFullPath(fixture.Home), "archived_sessions"), paths.ArchivedSessions);
        Assert.Equal(Path.Combine(Path.GetFullPath(fixture.Home), "attachments"), paths.Attachments);
    }

    [Fact]
    public void ResolveRejectsHomeWithinTheSyncRepository()
    {
        // Allowing a repository-contained home risks recursively syncing project files.
        var home = Path.Combine(Directory.GetCurrentDirectory(), $"codex-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);

        try
        {
            Assert.Throws<ArgumentException>(() => CodexPaths.Resolve(home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private sealed class CodexHomeFixture : IAsyncDisposable
    {
        private CodexHomeFixture(string home) => Home = home;

        public string Home { get; }

        public static Task<CodexHomeFixture> CreateAsync()
        {
            var home = Path.Combine(Path.GetTempPath(), $"codex-history-sync-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(home);
            return Task.FromResult(new CodexHomeFixture(home));
        }

        public async Task<string> WriteSessionAsync(string directory, string fileName, string id, string? additionalRecord = null)
        {
            var content = $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\"}}}}" + Environment.NewLine;
            if (additionalRecord is not null) content += additionalRecord + Environment.NewLine;
            return await WriteFileAsync(Path.Combine(directory, fileName), content);
        }

        public async Task<string> WriteFileAsync(string relativePath, string content)
        {
            var path = Path.Combine(Home, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
