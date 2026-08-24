using System.Text;
using CodexHistorySync.Core.Claude;

namespace CodexHistorySync.Core.Tests.Claude;

public sealed class ClaudeSessionPackageTests
{
    private const string SessionId = "85f91418-f594-48c5-92a9-f1edc7634a7f";
    private const string Cwd = @"C:\Repos\Demo";
    private const string Project = "c--Repos-Demo";

    [Fact]
    public void BuildParseRoundTrip_PreservesIdCwdProjectAndTranscript()
    {
        Run(root =>
        {
            var file = WriteSession(root, Project, SessionId,
                "{\"type\":\"queue-operation\",\"operation\":\"enqueue\",\"sessionId\":\"" + SessionId + "\"}\n" +
                "{\"type\":\"user\",\"cwd\":\"" + Cwd.Replace(@"\", @"\\") + "\",\"sessionId\":\"" + SessionId +
                "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"hello from claude\"}]}}\n" +
                "{\"type\":\"ai-title\",\"aiTitle\":\"Demo session\",\"sessionId\":\"" + SessionId + "\"}\n");

            var info = ClaudeSessionPackage.Parse(ClaudeSessionPackage.BuildFromFile(file));

            Assert.Equal(SessionId, info.SessionId);
            Assert.Equal(Cwd, info.Cwd);
            Assert.Equal(Project, info.Project);
            var transcript = Encoding.UTF8.GetString(info.Transcript);
            Assert.Contains("hello from claude", transcript, StringComparison.Ordinal);
            Assert.Contains("Demo session", transcript, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ToLogicalId_StaysDisjointFromCodexAndGrokNamespaces()
    {
        var logicalId = ClaudeSessionPackage.ToLogicalId(SessionId);

        Assert.Equal("cl-" + SessionId, logicalId);
        Assert.True(ClaudeSessionPackage.IsClaudeLogicalId(logicalId));
        Assert.Equal(SessionId, ClaudeSessionPackage.SessionIdFromLogicalId(logicalId));
        Assert.False(ClaudeSessionPackage.IsClaudeLogicalId("g-" + SessionId));
        Assert.False(ClaudeSessionPackage.IsClaudeLogicalId(SessionId));
    }

    [Fact]
    public void BuildFromFile_NormalizesCarriageReturns()
    {
        Run(root =>
        {
            var file = WriteSession(root, Project, SessionId,
                "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\",\"sessionId\":\"" + SessionId + "\"}\r\n");

            var info = ClaudeSessionPackage.Parse(ClaudeSessionPackage.BuildFromFile(file));

            Assert.DoesNotContain('\r', Encoding.UTF8.GetString(info.Transcript));
        });
    }

    [Fact]
    public void BuildFromFile_RejectsSessionIdDisagreeingWithFileName()
    {
        Run(root =>
        {
            var file = WriteSession(root, Project, SessionId,
                "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\",\"sessionId\":\"00000000-0000-0000-0000-000000000000\"}\n");

            Assert.Throws<InvalidDataException>(() => ClaudeSessionPackage.BuildFromFile(file));
        });
    }

    [Fact]
    public void BuildFromFile_RejectsSessionWithoutCwd()
    {
        Run(root =>
        {
            var file = WriteSession(root, Project, SessionId,
                "{\"type\":\"ai-title\",\"aiTitle\":\"No cwd anywhere\",\"sessionId\":\"" + SessionId + "\"}\n");

            Assert.Throws<InvalidDataException>(() => ClaudeSessionPackage.BuildFromFile(file));
        });
    }

    [Fact]
    public void BuildFromFile_RejectsNonUuidFileName()
    {
        Run(root =>
        {
            var file = WriteSession(root, Project, "not-a-uuid",
                "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\"}\n");

            Assert.Throws<InvalidDataException>(() => ClaudeSessionPackage.BuildFromFile(file));
        });
    }

    [Fact]
    public void Parse_RejectsUnsafeProjectSegment()
    {
        var package = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"id\":\"" + SessionId + "\",\"cwd\":\"C:\\\\Repos\\\\Demo\"," +
            "\"project\":\"..\",\"transcript\":\"{}\\n\"}");

        Assert.Throws<InvalidDataException>(() => ClaudeSessionPackage.Parse(package));
    }

    [Fact]
    public void Materialize_WritesTheStoredProjectSegmentVerbatim()
    {
        Run(root =>
        {
            var home = Path.Combine(root, ".claude");
            Directory.CreateDirectory(Path.Combine(home, "projects"));
            var paths = ClaudePaths.TryResolve(home);
            Assert.NotNull(paths);

            var source = WriteSession(root, Project, SessionId,
                "{\"type\":\"user\",\"cwd\":\"C:\\\\Repos\\\\Demo\",\"sessionId\":\"" + SessionId + "\"}\n");
            var info = ClaudeSessionPackage.Parse(ClaudeSessionPackage.BuildFromFile(source));

            ClaudeSessionPackage.Materialize(info, paths);

            var destination = Path.Combine(home, "projects", Project, SessionId + ".jsonl");
            Assert.True(File.Exists(destination));
            Assert.Equal(info.Transcript, File.ReadAllBytes(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
        });
    }

    private static string WriteSession(string root, string project, string sessionId, string transcript)
    {
        var directory = Path.Combine(root, "source", project);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, sessionId + ".jsonl");
        File.WriteAllText(file, transcript, new UTF8Encoding(false));
        return file;
    }

    private static void Run(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "chs-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { body(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
