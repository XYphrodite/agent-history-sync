using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Tests.Grok;

public sealed class GrokSessionPackageTests
{
    [Fact]
    public void BuildParseRoundTrip_PreservesIdCwdAndChat()
    {
        var root = Path.Combine(Path.GetTempPath(), "chs-grok-" + Guid.NewGuid().ToString("N"));
        var cwd = @"C:\Repos\Demo";
        var sessionId = "019fd29d-8f07-7eb3-8fcd-cadaf33d2de6";
        var dir = Path.Combine(root, "sessions", GrokPaths.EncodeCwdSegment(cwd), sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "chat_history.jsonl"),
            """
            {"type":"system","content":"huge system prompt"}
            {"type":"user","content":"hello from grok"}
            {"type":"assistant","content":"hi there"}

            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "summary.json"),
            JsonSerializer.Serialize(new { info = new { id = sessionId, cwd } }), new UTF8Encoding(false));

        try
        {
            var package = GrokSessionPackage.BuildFromDirectory(dir);
            var info = GrokSessionPackage.Parse(package);
            Assert.Equal(sessionId, info.SessionId);
            Assert.Equal(Path.GetFullPath(cwd), info.Cwd);
            var chat = Encoding.UTF8.GetString(info.ChatHistory);
            Assert.Contains("hello from grok", chat, StringComparison.Ordinal);
            Assert.DoesNotContain("huge system prompt", chat, StringComparison.Ordinal);
            Assert.Equal(GrokSessionPackage.ToLogicalId(sessionId), "g-" + sessionId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
