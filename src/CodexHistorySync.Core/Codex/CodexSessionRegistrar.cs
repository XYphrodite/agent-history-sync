using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexHistorySync.Core.Codex;

internal interface ICodexSessionRegistration : IAsyncDisposable
{
    string ModelProvider { get; }
    Task RegisterAsync(string sessionId, string expectedPath, CancellationToken cancellationToken);
}

/// <summary>
/// Lets Codex index a published rollout through its own API. Listing JSONL files alone does
/// not populate an already initialized profile's database, which the IDE uses for its list.
/// No SQL, thread/resume, model turn, or sync-repository operation is performed here.
/// </summary>
public sealed class CodexSessionRegistrar : ICodexSessionRegistration
{
    private readonly CodexAppServerConnection connection;

    private CodexSessionRegistrar(CodexAppServerConnection connection, string modelProvider)
    {
        this.connection = connection;
        ModelProvider = modelProvider;
    }

    public string ModelProvider { get; }

    public static async Task<CodexSessionRegistrar> StartAsync(
        string executable, string codexHome, CancellationToken cancellationToken)
    {
        var connection = await CodexAppServerConnection.StartAsync(executable, codexHome, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var configuration = await connection.RequestAsync("config/read", new { includeLayers = false }, cancellationToken)
                .ConfigureAwait(false);
            var config = configuration.RootElement.GetProperty("result").GetProperty("config");
            var provider = config.TryGetProperty("model_provider", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            return new CodexSessionRegistrar(connection, string.IsNullOrWhiteSpace(provider) ? "openai" : provider);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RegisterAsync(string sessionId, string expectedPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var canonicalPath = Path.GetFullPath(expectedPath);
        if (!File.Exists(canonicalPath)) throw new FileNotFoundException("The imported Codex session is missing.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // This reads only the selected thread and repairs its native state-db entry without
            // resuming it, starting tools, or sending any conversation to a model.
            using var read = await connection.RequestAsync("thread/read", new { threadId = sessionId, includeTurns = false }, deadline.Token)
                .ConfigureAwait(false);
            var thread = read.RootElement.GetProperty("result").GetProperty("thread");
            if (!Matches(thread, sessionId, canonicalPath))
                throw new InvalidDataException("Codex returned a different imported session.");

            // Some versions persist read-repair asynchronously. Always verify the same database
            // and provider filters as an interactive client, with pagination and a bounded retry.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                string? cursor = null;
                var cursors = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    using var list = await connection.RequestAsync("thread/list", new
                    {
                        useStateDbOnly = true, modelProviders = new[] { ModelProvider },
                        sourceKinds = new[] { "cli", "vscode" }, archived = false,
                        sortKey = "updated_at", limit = 100, cursor
                    }, deadline.Token).ConfigureAwait(false);
                    var result = list.RootElement.GetProperty("result");
                    if (result.GetProperty("data").EnumerateArray().Any(item => Matches(item, sessionId, canonicalPath))) return;
                    cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                        ? next.GetString() : null;
                    if (cursor is not null && !cursors.Add(cursor))
                        throw new InvalidDataException("Codex repeated a session-list cursor.");
                } while (cursor is not null);
                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token).ConfigureAwait(false);
            }
            throw new InvalidDataException("The copied session is not visible in the Codex database.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Codex session registration timed out.");
        }
    }

    private static bool Matches(JsonElement thread, string sessionId, string expectedPath) =>
        thread.TryGetProperty("id", out var id) && id.GetString() == sessionId &&
        thread.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String &&
        string.Equals(Path.GetFullPath(path.GetString()!), expectedPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

internal sealed class CodexAppServerConnection : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task stderrDrain;
    private int nextId;

    private CodexAppServerConnection(Process process)
    {
        this.process = process;
        stderrDrain = DrainAsync(process.StandardError);
    }

    public string Version { get; private set; } = "unknown";

    public static async Task<CodexAppServerConnection> StartAsync(
        string executable, string codexHome, CancellationToken cancellationToken, bool experimentalApi = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add("stdio://");
        start.Environment["CODEX_HOME"] = Path.GetFullPath(codexHome);
        var connection = new CodexAppServerConnection(Process.Start(start)
            ?? throw new IOException("Codex app-server did not start."));
        try
        {
            using var initialized = await connection.RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex-history-sync", version = "0.1.0" },
                capabilities = new { experimentalApi }
            }, cancellationToken).ConfigureAwait(false);
            connection.Version = initialized.RootElement.GetProperty("result").GetProperty("userAgent").GetString() ?? "unknown";
            await connection.process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await connection.process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<JsonDocument> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var id = ++nextId;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), timeout.Token)
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            var response = JsonDocument.Parse(line);
            if (!response.RootElement.TryGetProperty("id", out var responseId) ||
                responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var value) || value != id)
            {
                response.Dispose();
                continue;
            }
            if (response.RootElement.TryGetProperty("error", out _))
            {
                response.Dispose();
                // Child error strings can contain session text, credentials, or paths.
                throw new InvalidDataException("Codex rejected the session metadata request.");
            }
            return response;
        }
        throw new IOException("Codex app-server closed before responding.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await stderrDrain.ConfigureAwait(false);
        }
        finally { process.Dispose(); }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0) { }
    }
}

public sealed class CodexSessionRegistrationException(Exception innerException) : IOException(
    "The copy was saved, but Codex could not confirm its visibility. Do not copy it again.", innerException);
