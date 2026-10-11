using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class SessionOperationsLogTests
{
    [Fact]
    public void RecordCopy_AppendsOneLinePerOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "session-ops-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SessionOperationsLog(root);
            var source = new ManagedSession(ManagedAgent.Hermes, "20261007_165214_f7b5ce",
                "/home/gamer/.hermes/state.db", "Friendly greeting #2",
                DateTimeOffset.UtcNow, false, true);

            log.RecordCopy(source, "MiMo", "FAIL Active sessions cannot be copied.");
            log.RecordCopy(source, "MiMo", "OK ses_abc");
            log.RecordDelete(source, "OK");

            var path = Path.Combine(root, "CodexHistorySync", "logs", SessionOperationsLog.FileName);
            Assert.True(File.Exists(path));
            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);
            Assert.Contains("copy hermes/20261007_165214_f7b5ce \"Friendly greeting #2\" -> MiMo FAIL Active sessions cannot be copied.", lines[0], StringComparison.Ordinal);
            Assert.Contains("copy hermes/20261007_165214_f7b5ce \"Friendly greeting #2\" -> MiMo OK ses_abc", lines[1], StringComparison.Ordinal);
            Assert.Contains("delete hermes/20261007_165214_f7b5ce \"Friendly greeting #2\" OK", lines[2], StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Record_SanitizesControlCharactersAndNewlines()
    {
        var root = Path.Combine(Path.GetTempPath(), "session-ops-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SessionOperationsLog(root);
            var source = new ManagedSession(ManagedAgent.Mimo, "ses_1", @"C:\temp\session",
                "title\nwith\nnewlines", DateTimeOffset.UtcNow, false, true);

            log.RecordRefusal("copy", source, "FAIL Copy failed: oops\r\nevil");

            var line = File.ReadAllLines(log.PathFor).Single();
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", line, StringComparison.Ordinal);
            Assert.Contains("title with newlines", line, StringComparison.Ordinal);
            Assert.Contains("FAIL Copy failed: oops evil", line, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
