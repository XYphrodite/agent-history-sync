using System.Text;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Tests.Claude;

public sealed class ClaudeMemoryImportTests : IDisposable
{
    private const string Project = "c--Repos-Demo";
    private const string Name = "tailnet-machines";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-claude-memory-import-{Guid.NewGuid():N}");

    [Fact]
    public async Task AnImportWritesTheMemoryFileUnderTheStoredProject()
    {
        var fixture = CreateFixture();
        var package = SourcePackage("# Tailnet\n\nssh home\n");

        var result = await fixture.ImportAsync(package);

        Assert.Equal(ImportApplyResult.Applied, result);
        var destination = fixture.Paths.MemoryFilePath(Project, Name);
        Assert.True(File.Exists(destination));
        Assert.Equal("# Tailnet\n\nssh home\n", File.ReadAllText(destination, new UTF8Encoding(false)));
    }

    [Fact]
    public async Task AnImportedFileRehashesToTheObjectThatWasAuthenticated()
    {
        var fixture = CreateFixture();
        var package = SourcePackage("one\r\ntwo\n");

        await fixture.ImportAsync(package);

        var rebuilt = ClaudeMemoryPackage.BuildFromFile(fixture.Paths.MemoryFilePath(Project, Name));
        Assert.Equal(ClaudeMemoryPackage.HashPackage(package), ClaudeMemoryPackage.HashPackage(rebuilt));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private byte[] SourcePackage(string body)
    {
        var sourceHome = Path.Combine(root, "source", ".claude");
        var sourceProjects = Path.Combine(sourceHome, "projects");
        var directory = Path.Combine(sourceProjects, Project, "memory");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Name + ".md");
        File.WriteAllText(file, body, new UTF8Encoding(false));
        return ClaudeMemoryPackage.BuildFromFile(file);
    }

    private ImportFixture CreateFixture()
    {
        var codexHome = Path.Combine(root, "codex");
        Directory.CreateDirectory(codexHome);
        var codexPaths = CodexPaths.Resolve(codexHome);
        var claudeHome = Path.Combine(root, "target", ".claude");
        var claudeProjects = Path.Combine(claudeHome, "projects");
        Directory.CreateDirectory(claudeProjects);
        var claudePaths = new ClaudePaths(claudeHome, claudeProjects);
        var local = Path.Combine(root, "local");
        var backups = new BackupStore("repo", local, codexPaths, claudePaths: claudePaths);
        var writer = new CodexHistoryWriter(codexPaths, backups, new StoppedDetector(),
            claudePaths: claudePaths);
        return new ImportFixture(claudePaths, writer);
    }

    private sealed record ImportFixture(ClaudePaths Paths, CodexHistoryWriter Writer)
    {
        public async Task<ImportApplyResult> ImportAsync(byte[] package)
        {
            var destination = Paths.MemoryFilePath(Project, Name);
            var incoming = new LocalObject(
                new LogicalObjectId(ClaudeMemoryPackage.ToLogicalId(Project, Name)),
                ObjectKind.ClaudeMemory,
                destination,
                ClaudeMemoryPackage.HashPackage(package),
                package.LongLength,
                DateTimeOffset.UtcNow);
            using var stream = new MemoryStream(package);
            return await Writer.ImportAsync(incoming, stream, "operation-1", ExpectedHistoryState.Absent,
                CancellationToken.None);
        }
    }

    private sealed class StoppedDetector : ICodexProcessDetector
    {
        public bool IsRunning() => false;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
