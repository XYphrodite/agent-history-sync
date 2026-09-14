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
    private readonly string dataRoot;
    private readonly string historyRoot;
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
    private int requestId;

    private McpProcessFixture(string root, Process process, string dataRoot, string historyRoot)
    {
        Root = root;
        this.process = process;
        this.dataRoot = dataRoot;
        this.historyRoot = historyRoot;
        errors = process.StandardError.ReadToEndAsync();
    }

    public string Root { get; }
    public string SessionPath => Path.Combine(historyRoot, "continue", "sessions", SessionId + ".json");
    public string CatalogPath => Path.Combine(dataRoot, "CodexHistorySync", "catalog.db");

    public static async Task<McpProcessFixture> StartAsync(string? publishedExecutable = null, string? profileName = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "chs-mcp-" + Guid.NewGuid().ToString("N"));
        var historyRoot = profileName is null ? root : Path.Combine(root, "snapshot");
        var dataRoot = Path.Combine(root, profileName is null ? "data" : "profile-data");
        Directory.CreateDirectory(Path.Combine(historyRoot, "continue", "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "data", "CodexHistorySync"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "CodexHistorySync"));
        // A joined repository and even a valid sync configuration must be unnecessary.
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "CodexHistorySync", "config.json"), "invalid sync configuration");
        if (profileName is not null)
            await File.WriteAllTextAsync(Path.Combine(root, "data", "CodexHistorySync", "profiles.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, profiles = new[] { new { name = profileName, dataDirectory = dataRoot, sessionsDirectory = historyRoot } }
            }));
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
        if (publishedExecutable is null)
        {
            // Project references copy the CLI assembly, but not its self-contained runtime.
            // Use this test project's runtime and dependency graph for that assembly.
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.ChangeExtension(typeof(McpProcessFixture).Assembly.Location, ".runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(typeof(McpProcessFixture).Assembly.Location, ".deps.json"));
            start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        }
        start.ArgumentList.Add("mcp");
        if (profileName is not null)
        {
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(profileName);
        }
        start.Environment["LOCALAPPDATA"] = Path.Combine(root, "data");
        start.Environment["CODEX_HOME"] = Path.Combine(root, "missing-codex");
        start.Environment["GROK_HOME"] = Path.Combine(root, "missing-grok");
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(root, "missing-claude");
        start.Environment["CONTINUE_GLOBAL_DIR"] = Path.Combine(historyRoot, "continue");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MCP process.");
        var fixture = new McpProcessFixture(root, process, dataRoot, historyRoot);
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
            if (line is null) Assert.Fail("MCP process closed its output: " + await errors.WaitAsync(timeout.Token));
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
