using System.Text;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Tests.Claude;

public sealed class ClaudeMemoryPackageTests
{
    private const string Project = "c--Repos-Demo";
    private const string Name = "tailnet-machines";

    [Fact]
    public void BuildParseRoundTrip_PreservesProjectNameAndBody()
    {
        Run(root =>
        {
            var file = WriteMemory(root, Project, Name, "# Tailnet\n\nssh home reaches the Xeon.\n");

            var body = ClaudeMemoryPackage.BuildFromFile(file);
            var info = ClaudeMemoryPackage.Parse(body, ClaudeMemoryPackage.ToLogicalId(Project, Name));

            Assert.Equal(Project, info.Project);
            Assert.Equal(Name, info.Name);
            Assert.Equal("# Tailnet\n\nssh home reaches the Xeon.\n", info.Body);
        });
    }

    [Fact]
    public void ToLogicalId_StaysDisjointFromClaudeSessionNamespace()
    {
        var logicalId = ClaudeMemoryPackage.ToLogicalId(Project, Name);

        Assert.True(ClaudeMemoryPackage.TryParseLogicalId(logicalId, out var project, out var name));
        Assert.Equal(Project, project);
        Assert.Equal(Name, name);
        Assert.True(ClaudeMemoryPackage.IsClaudeMemoryLogicalId(logicalId));
        Assert.False(ClaudeSessionPackage.IsClaudeLogicalId(logicalId));
        Assert.False(ClaudeMemoryPackage.IsClaudeMemoryLogicalId("cl-85f91418-f594-48c5-92a9-f1edc7634a7f"));
        Assert.False(ClaudeMemoryPackage.IsClaudeMemoryLogicalId(Name));
    }

    [Fact]
    public void ToLogicalId_DistinguishesTheSameNameInTwoProjects()
    {
        Assert.NotEqual(
            ClaudeMemoryPackage.ToLogicalId("c--Repos-One", Name),
            ClaudeMemoryPackage.ToLogicalId("c--Repos-Two", Name));
    }

    [Fact]
    public void BuildFromFile_NormalizesCarriageReturnsAndIsAFixedPoint()
    {
        Run(root =>
        {
            var file = WriteMemory(root, Project, Name, "# Title\r\n\r\nbody\r");

            var first = ClaudeMemoryPackage.BuildFromFile(file);
            var info = ClaudeMemoryPackage.Parse(first, ClaudeMemoryPackage.ToLogicalId(Project, Name));
            Assert.DoesNotContain('\r', info.Body);

            var paths = CreateHome(root);
            ClaudeMemoryPackage.Materialize(info, paths);
            var second = ClaudeMemoryPackage.BuildFromFile(paths.MemoryFilePath(Project, Name));

            Assert.Equal(first, second);
            Assert.Equal(ClaudeMemoryPackage.HashPackage(first), ClaudeMemoryPackage.HashPackage(second));
        });
    }

    [Fact]
    public void Build_TwiceOnTheSameBody_IsByteIdentical()
    {
        // Truncation that is not a fixed point is how defect 4 left imported sessions in
        // permanent conflict. Memory has no limiter; this still has to hold for newline
        // normalization and JSON wrapping.
        var first = ClaudeMemoryPackage.Build(Project, Name, "one\r\ntwo\n");
        var parsed = ClaudeMemoryPackage.Parse(first, ClaudeMemoryPackage.ToLogicalId(Project, Name));
        var second = ClaudeMemoryPackage.Build(parsed.Project, parsed.Name, parsed.Body);

        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildFromFile_RejectsAnEmptyFile()
    {
        Run(root =>
        {
            var file = WriteMemory(root, Project, Name, "   \n");

            Assert.Throws<InvalidDataException>(() => ClaudeMemoryPackage.BuildFromFile(file));
        });
    }

    [Fact]
    public void BuildFromFile_RejectsAFileOutsideMemory()
    {
        Run(root =>
        {
            var directory = Path.Combine(root, "source", Project);
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, Name + ".md");
            File.WriteAllText(file, "body\n", new UTF8Encoding(false));

            Assert.Throws<InvalidDataException>(() => ClaudeMemoryPackage.BuildFromFile(file));
        });
    }

    [Fact]
    public void Parse_RejectsUnsafeProjectSegment()
    {
        Assert.False(ClaudeMemoryPackage.TryParseLogicalId("cm-2e2e.tailnet-machines", out _, out _));
        Assert.Throws<InvalidDataException>(() =>
            ClaudeMemoryPackage.Parse(Encoding.UTF8.GetBytes("# x\n"), "cm-2e2e.tailnet-machines"));
    }

    [Fact]
    public void Materialize_WritesTheStoredProjectSegmentVerbatim()
    {
        Run(root =>
        {
            var paths = CreateHome(root);
            var source = WriteMemory(root, Project, Name, "# Index\n");
            var info = ClaudeMemoryPackage.Parse(ClaudeMemoryPackage.BuildFromFile(source),
                ClaudeMemoryPackage.ToLogicalId(Project, Name));

            ClaudeMemoryPackage.Materialize(info, paths);

            var destination = Path.Combine(paths.Projects, Project, "memory", Name + ".md");
            Assert.True(File.Exists(destination));
            Assert.Equal(info.Body, File.ReadAllText(destination, new UTF8Encoding(false)));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
        });
    }

    [Fact]
    public void EnsureSessionDestination_AcceptsAMemoryFileAndRejectsATranscript()
    {
        Run(root =>
        {
            var paths = CreateHome(root);
            var memory = paths.MemoryFilePath(Project, Name);
            Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
            var session = paths.SessionFilePath(Project, "85f91418-f594-48c5-92a9-f1edc7634a7f");

            Assert.Equal(Path.GetFullPath(memory),
                PathSafety.EnsureSessionDestination(memory, ObjectKind.ClaudeMemory, CodexPathsFor(root),
                    nameof(memory), claudePaths: paths));
            Assert.Throws<ArgumentException>(() =>
                PathSafety.EnsureSessionDestination(session, ObjectKind.ClaudeMemory, CodexPathsFor(root),
                    nameof(session), claudePaths: paths));
            Assert.Throws<ArgumentException>(() =>
                PathSafety.EnsureSessionDestination(memory, ObjectKind.ClaudeSession, CodexPathsFor(root),
                    nameof(memory), claudePaths: paths));
        });
    }

    private static string WriteMemory(string root, string project, string name, string body)
    {
        var directory = Path.Combine(root, "source", project, "memory");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name + ".md");
        File.WriteAllText(file, body, new UTF8Encoding(false));
        return file;
    }

    private static ClaudePaths CreateHome(string root)
    {
        var home = Path.Combine(root, ".claude");
        var projects = Path.Combine(home, "projects");
        Directory.CreateDirectory(projects);
        return new ClaudePaths(home, projects);
    }

    private static CodexHistorySync.Core.Codex.CodexPaths CodexPathsFor(string root)
    {
        var home = Path.Combine(root, ".codex");
        Directory.CreateDirectory(home);
        return CodexHistorySync.Core.Codex.CodexPaths.Resolve(home);
    }

    private static void Run(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "chs-clmem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { body(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
