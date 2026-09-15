using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Kimi;

namespace CodexHistorySync.Core.Tests.Kimi;

/// <summary>Disposable Kimi home with helpers that write synthetic sessions only.</summary>
internal sealed class KimiHomeFixture : IDisposable
{
    public const string MainSessionId = "10000000-0000-0000-0000-000000000001";
    public const string SecondSessionId = "20000000-0000-0000-0000-000000000002";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"codex-history-sync-kimi-{Guid.NewGuid():N}");

    public KimiHomeFixture()
    {
        var sessions = Path.Combine(Home, "sessions");
        Directory.CreateDirectory(sessions);
        Paths = new KimiPaths(Home, sessions);
    }

    public string Home => Path.Combine(root, ".kimi-code");

    public KimiPaths Paths { get; }

    public string WorkDir => Path.Combine(root, "work", "demo");

    /// <summary>Writes one session (state.json + agents/main/wire.jsonl) and returns its directory.</summary>
    public string WriteSession(
        string sessionId = MainSessionId,
        string? workDir = null,
        string title = "synthetic session",
        IReadOnlyList<(string Role, string Text)>? turns = null)
    {
        workDir ??= WorkDir;
        var workDirKey = KimiPaths.ComputeWorkDirKey(workDir);
        var sessionDirectory = Paths.SessionDirectory(workDirKey, KimiPaths.SessionIdPrefix + sessionId);
        Directory.CreateDirectory(Path.Combine(sessionDirectory, "agents", "main"));

        var state = new
        {
            id = KimiPaths.SessionIdPrefix + sessionId,
            version = 2,
            cwd = workDir,
            createdAt = 1789426633555L,
            updatedAt = 1789426974020L,
            archived = false,
            agents = new { main = new { homedir = Path.Combine(sessionDirectory, "agents", "main").Replace('\\', '/'), type = "main" } },
            custom = new { },
            lastPrompt = turns?.LastOrDefault(turn => turn.Role == "user").Text,
            title,
            titleKind = "replaceable",
            isCustomTitle = false
        };
        File.WriteAllText(
            Path.Combine(sessionDirectory, KimiSessionPackage.StateFileName),
            JsonSerializer.Serialize(state), new UTF8Encoding(false));

        var wire = new StringBuilder();
        wire.Append("{\"type\":\"metadata\",\"protocol_version\":\"1.5\",\"created_at\":1789426633663}\n");
        foreach (var (role, text) in turns ?? [("user", "synthetic question"), ("assistant", "synthetic answer")])
        {
            var content = role == "assistant"
                ? $"[{{\"type\":\"think\",\"think\":\"reasoning\"}},{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(text)}}}]"
                : $"[{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(text)}}}]";
            wire.Append("{\"type\":\"agent.message.appended\",\"message\":{\"message\":{\"role\":\"")
                .Append(role).Append("\",\"content\":").Append(content)
                .Append("},\"meta\":{}},\"time\":1789426633780}\n");
        }
        File.WriteAllText(
            Path.Combine(sessionDirectory, "agents", "main", KimiSessionPackage.WireFileName),
            wire.ToString(), new UTF8Encoding(false));
        return sessionDirectory;
    }

    public string WriteIndex(params (string SessionId, string WorkDir)[] sessions)
    {
        var lines = sessions.Select(session =>
        {
            var workDirKey = KimiPaths.ComputeWorkDirKey(session.WorkDir);
            var sessionDir = Paths.SessionDirectory(workDirKey, KimiPaths.SessionIdPrefix + session.SessionId);
            return JsonSerializer.Serialize(new
            {
                sessionId = KimiPaths.SessionIdPrefix + session.SessionId,
                sessionDir = sessionDir.Replace('\\', '/'),
                workDir = session.WorkDir
            });
        });
        var content = string.Join("\n", lines);
        File.WriteAllText(Paths.IndexFilePath, content, new UTF8Encoding(false));
        return Paths.IndexFilePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
