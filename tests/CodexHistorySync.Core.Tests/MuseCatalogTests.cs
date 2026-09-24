using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Muse;
using CodexHistorySync.Core.Viewing;
using Microsoft.Data.Sqlite;

namespace CodexHistorySync.Core.Tests;

public sealed class MuseCatalogTests
{
    [Fact]
    public async Task MainCatalogSkipsSubagentsAndLoadsThemOnlyForTheirParent()
    {
        using var fixture = new Fixture();
        var parent = fixture.Write("2026/09/25/" + Guid.NewGuid(), "parent");
        var child = fixture.Write(Path.GetRelativePath(fixture.Paths.Sessions, parent) + "/subagent/" + Guid.NewGuid(), "child");
        var nested = fixture.Write(Path.GetRelativePath(fixture.Paths.Sessions, child) + "/subagent/" + Guid.NewGuid(), "nested");
        fixture.Write(Path.GetRelativePath(fixture.Paths.Sessions, parent) + "/tool-outputs/" + Guid.NewGuid(), "not a session");
        using var limiter = new SessionCatalogReadLimiter(8);
        var rows = await fixture.Source.ScanAsync(limiter, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal("parent", row.Title);
        Assert.Equal(1, fixture.Io.Reads);
        var session = new ManagedSession(ManagedAgent.Muse, row.SessionId, row.NativePath, row.Title, row.LastModifiedAt, false, true);
        var tree = await new LocalSessionFamilyReader(null, fixture.Paths).ReadAsync(session, CancellationToken.None);
        Assert.Equal(child, Assert.Single(tree.Children).Session.NativePath);
        Assert.Equal(nested, Assert.Single(tree.Children[0].Children).Session.NativePath);
    }

    [Fact]
    public async Task NativeIndexAvoidsTranscriptReadsAndDoesNotHideUnindexedSessions()
    {
        using var fixture = new Fixture();
        var first = fixture.Write(Guid.NewGuid().ToString(), "from log");
        fixture.Write(Guid.NewGuid().ToString(), "unindexed");
        fixture.Index(Path.GetFileName(first), "fallback", "Official Muse name");
        using var limiter = new SessionCatalogReadLimiter(8);
        var rows = await fixture.Source.ScanAsync(limiter, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        var indexed = Assert.Single(rows, row => row.NativePath == first);
        Assert.Equal("Official Muse name", indexed.Title);
        Assert.Equal(ManagedTitleSource.Official, indexed.TitleSource);
        Assert.Equal(1, fixture.Io.Reads);
        Assert.Contains(rows, row => row.Title == "unindexed");
    }

    [Fact]
    public async Task MissingOrCorruptIndexFallsBackAndCacheInvalidatesOnChange()
    {
        using var fixture = new Fixture();
        var directory = fixture.Write(Guid.NewGuid().ToString(), "first");
        File.WriteAllText(Path.Combine(fixture.Paths.Home, "session-index.db"), "not a database");
        using var limiter = new SessionCatalogReadLimiter(8);
        Assert.Equal("first", Assert.Single(await fixture.Source.ScanAsync(limiter, CancellationToken.None)).Title);
        Assert.Equal("first", Assert.Single(await fixture.Source.ScanAsync(limiter, CancellationToken.None)).Title);
        Assert.Equal(1, fixture.Io.Reads);
        fixture.Write(Path.GetFileName(directory), "changed question");
        Assert.Equal("changed question", Assert.Single(await fixture.Source.ScanAsync(limiter, CancellationToken.None)).Title);
        Assert.Equal(2, fixture.Io.Reads);
        File.Delete(Path.Combine(directory, "session.jsonl"));
        Assert.Empty(await fixture.Source.ScanAsync(limiter, CancellationToken.None));
    }

    [Fact]
    public async Task LargeTranscriptRemainsReadableAndUsesTranscriptTimestamp()
    {
        using var fixture = new Fixture();
        var directory = fixture.Write(Guid.NewGuid().ToString(), "large conversation");
        var file = Path.Combine(directory, "session.jsonl");
        File.AppendAllText(file, new string('x', 100_000));
        var timestamp = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, timestamp);
        using var limiter = new SessionCatalogReadLimiter(8);
        var row = Assert.Single(await fixture.Source.ScanAsync(limiter, CancellationToken.None));
        Assert.True(row.CanRead);
        Assert.Equal(timestamp, row.LastModifiedAt.UtcDateTime);
        Assert.Equal(64 * 1024, fixture.Io.LargestBudget);
    }

    [Fact]
    public async Task DuplicateIdsAreUnreadableAndStaleIndexCannotInventRows()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString();
        fixture.Write("2026/09/24/" + id, "first");
        fixture.Write("2026/09/25/" + id, "second");
        fixture.Index(Guid.NewGuid().ToString(), "deleted session", null);
        using var limiter = new SessionCatalogReadLimiter(8);
        var rows = await fixture.Source.ScanAsync(limiter, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.CanRead));
    }

    [Fact]
    public async Task MissingHomeIsEmptyAndDoesNotCreateDirectories()
    {
        using var fixture = new Fixture();
        var home = Path.Combine(fixture.Paths.Home, "absent");
        Assert.Null(MusePaths.TryResolve(home));
        var source = new MuseSessionCatalogSource(new MusePaths(home, Path.Combine(home, "sessions")), fixture.Io);
        using var limiter = new SessionCatalogReadLimiter(8);
        Assert.Empty(await source.ScanAsync(limiter, CancellationToken.None));
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public void WslUsesActualLinuxHomeAndDoesNotAssumeUbuntuOrWindowsUserName()
    {
        var calls = new List<string[]>();
        var result = MusePaths.RunningWslHomes(arguments =>
        {
            calls.Add(arguments);
            return arguments[0] == "--list" ? "Debian\r\n" : "/home/linux-user\n";
        }).ToArray();
        Assert.Equal([@"\\wsl$\Debian\home\linux-user\.local\share\muse"], result);
        Assert.Equal(["--list", "--running", "--quiet"], calls[0]);
        Assert.Equal(["--distribution", "Debian", "--exec", "printenv", "HOME"], calls[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingWslOrNoRunningDistributionReturnsNoHomes(string? output)
    {
        var calls = 0;
        Assert.Empty(MusePaths.RunningWslHomes(_ => { calls++; return output; }));
        Assert.Equal(1, calls);
    }

    private sealed class Fixture : IDisposable
    {
        public MusePaths Paths { get; }
        public CountingIo Io { get; } = new();
        public MuseSessionCatalogSource Source { get; }
        public Fixture()
        {
            var home = Path.Combine(Path.GetTempPath(), "muse-catalog-" + Guid.NewGuid().ToString("N"));
            Paths = new MusePaths(home, Path.Combine(home, "sessions"));
            Directory.CreateDirectory(Paths.Sessions);
            Source = new MuseSessionCatalogSource(Paths, Io);
        }
        public string Write(string relative, string title)
        {
            var directory = Path.GetFullPath(Path.Combine(Paths.Sessions, relative));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "session.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new { role = "user", text = title }) + "\n");
            return directory;
        }
        public void Index(string id, string title, string? name)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Paths.Home, "session-index.db"), Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE sessions(session_id TEXT, title TEXT, session_name TEXT); INSERT INTO sessions VALUES($id,$title,$name)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        public void Dispose() => Directory.Delete(Paths.Home, true);
    }

    private sealed class CountingIo : ISessionCatalogIo
    {
        private readonly SystemSessionCatalogIo inner = new();
        public int Reads;
        public int LargestBudget;
        public IReadOnlyList<string> EnumerateFiles(string root, string pattern) => throw new InvalidOperationException("Recursive enumeration is forbidden for Muse metadata.");
        public IReadOnlyList<string> EnumerateDirectories(string root) => inner.EnumerateDirectories(root);
        public bool FileExists(string path) => inner.FileExists(path);
        public DateTimeOffset LastWriteTime(string path) => inner.LastWriteTime(path);
        public Task<BoundedTextRead> ReadPrefixAsync(string path, int maximumBytes, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads); LargestBudget = Math.Max(LargestBudget, maximumBytes);
            return inner.ReadPrefixAsync(path, maximumBytes, ct);
        }
        public Task<BoundedTextRead> ReadTailAsync(string path, int maximumBytes, CancellationToken ct) => inner.ReadTailAsync(path, maximumBytes, ct);
    }
}
