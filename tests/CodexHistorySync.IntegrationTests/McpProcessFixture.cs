using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli;

namespace CodexHistorySync.IntegrationTests;

internal sealed class McpProcessFixture : IAsyncDisposable
{
    internal const string SessionId = "9490954d-d7dd-4cbe-984c-6172d60bf3dc";
    internal const string UserText = "Remember the unique xylophone handshake. Привет 👋";
    internal const string AssistantText = "Use local history for this task.";
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
    private int requestId;

    private McpProcessFixture(string root, Process process)
    {
        Root = root;
        this.process = process;
        errors = process.StandardError.ReadToEndAsync();
    }

    public string Root { get; }
    public string SessionPath => Path.Combine(Root, "continue", "sessions", SessionId + ".json");
    public string CatalogPath => Path.Combine(Root, "data", "CodexHistorySync", "catalog.db");

    public static async Task<McpProcessFixture> StartAsync(string? publishedExecutable = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "chs-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "continue", "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "data", "CodexHistorySync"));
        // A joined repository and even a valid sync configuration must be unnecessary.
        await File.WriteAllTextAsync(Path.Combine(root, "data", "CodexHistorySync", "config.json"), "invalid sync configuration");
        var start = new ProcessStartInfo(publishedExecutable ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = root
        };
        if (publishedExecutable is null) start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        start.ArgumentList.Add("mcp");
        start.Environment["LOCALAPPDATA"] = Path.Combine(root, "data");
        start.Environment["CODEX_HOME"] = Path.Combine(root, "missing-codex");
        start.Environment["GROK_HOME"] = Path.Combine(root, "missing-grok");
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(root, "missing-claude");
        start.Environment["CONTINUE_GLOBAL_DIR"] = Path.Combine(root, "continue");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MCP process.");
        var fixture = new McpProcessFixture(root, process);
        await fixture.WriteSessionAsync("Local example");
        return fixture;
    }

    public Task WriteSessionAsync(string title, string userText = UserText) => File.WriteAllTextAsync(SessionPath,
        JsonSerializer.Serialize(new
        {
            sessionId = SessionId, title, workspaceDirectory = "",
            history = new[]
            {
                new { message = new { role = "user", content = userText } },
                new { message = new { role = "assistant", content = AssistantText } },
                new { message = new { role = "tool", content = "SECRET_TOOL_OUTPUT" } },
                new { message = new { role = "thinking", content = "SECRET_REASONING" } }
            }
        }));

    public async Task<JsonElement> InitializeAsync()
    {
        var initialized = await RequestAsync("initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "agent-sync-tests", version = "1.0" }
        });
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await process.StandardInput.FlushAsync();
        return initialized;
    }

    public Task<JsonElement> CallAsync(string name, object arguments) => RequestAsync("tools/call", new { name, arguments });

    public async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = ++requestId;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await process.StandardInput.FlushAsync();
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            // Parsing every line also proves stdout contains no startup banners or progress text.
            using var response = JsonDocument.Parse(line);
            if (!response.RootElement.TryGetProperty("id", out var responseId)) continue;
            Assert.Equal(id, responseId.GetInt32());
            Assert.False(response.RootElement.TryGetProperty("error", out var error), error.ToString());
            return response.RootElement.GetProperty("result").Clone();
        }
    }

    public async Task FinishAsync()
    {
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await errors);
        Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync(timeout.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process.Dispose();
        timeout.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
