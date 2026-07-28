using System.Diagnostics;
using System.Text.Json;

namespace CodexHistorySync.Core.Codex;

public sealed record CompatibilityResult(bool IsCompatible, string CodexVersion, string Diagnostic);

public sealed class CodexCompatibilityProbe
{
    public async Task<CompatibilityResult> ProbeAsync(string codexExe, string sourceSession, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(codexExe)) return Incompatible("unknown", "A Codex executable path is required.");
        if (!File.Exists(sourceSession)) return Incompatible("unknown", "The compatibility session was not found.");
        var disposableHome = Path.Combine(Path.GetTempPath(), $"codex-history-sync-{Guid.NewGuid():N}");
        Process? process = null;
        var codexVersion = "unknown";
        try
        {
            Directory.CreateDirectory(disposableHome);
            var threadId = await ReadThreadIdAsync(sourceSession, cancellationToken);
            if (threadId is null) return Incompatible("unknown", "The compatibility session has no session_meta thread ID.");
            var destination = Path.Combine(disposableHome, "sessions", DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), DateTime.UtcNow.ToString("dd"));
            Directory.CreateDirectory(destination);
            File.Copy(sourceSession, Path.Combine(destination, Path.GetFileName(sourceSession)));
            if (Directory.EnumerateFiles(disposableHome, "*.sqlite*", SearchOption.AllDirectories).Any()) return Incompatible(codexVersion, "The disposable Codex home unexpectedly contains a SQLite file.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var startInfo = new ProcessStartInfo { FileName = codexExe, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add("stdio://");
            startInfo.Environment["CODEX_HOME"] = disposableHome;
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Codex app-server did not start.");

            await WriteRequestAsync(process.StandardInput, 1, "initialize", new { clientInfo = new { name = "codex-history-sync", version = "0.1.0" } }, timeout.Token);
            using var initialized = await ReadResponseAsync(process.StandardOutput, 1, timeout.Token);
            codexVersion = initialized.RootElement.GetProperty("result").GetProperty("userAgent").GetString() ?? "unknown";
            await WriteNotificationAsync(process.StandardInput, "initialized", timeout.Token);
            await WriteRequestAsync(process.StandardInput, 2, "thread/list", new { useStateDbOnly = false }, timeout.Token);
            using var threadList = await ReadResponseAsync(process.StandardOutput, 2, timeout.Token);
            var listed = threadList.RootElement.GetProperty("result").GetProperty("data").EnumerateArray().Any(thread => thread.TryGetProperty("id", out var id) && id.GetString() == threadId);
            return listed ? new CompatibilityResult(true, codexVersion, "The imported JSONL thread was listed from the disposable Codex home.") : Incompatible(codexVersion, "The imported JSONL thread was not listed by Codex.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Incompatible(codexVersion, "The compatibility probe was cancelled."); }
        catch (OperationCanceledException) { return Incompatible(codexVersion, "The Codex app-server did not respond before the compatibility probe timed out."); }
        catch (JsonException) { return Incompatible(codexVersion, "The compatibility session could not be read."); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return Incompatible(codexVersion, $"The Codex compatibility probe failed: {exception.GetType().Name}."); }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
                finally { process.Dispose(); }
            }
            await DeleteDisposableHomeAsync(disposableHome);
        }
    }

    private static CompatibilityResult Incompatible(string version, string diagnostic) => new(false, version, diagnostic);
    private static async Task DeleteDisposableHomeAsync(string disposableHome)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(disposableHome, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 49)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            catch (IOException) when (attempt < 49)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private static async Task<string?> ReadThreadIdAsync(string session, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(session);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta") continue;
            if (TryGetString(root, "id", out var id)) return id;
            if (root.TryGetProperty("payload", out var payload) && (TryGetString(payload, "id", out id) || TryGetString(payload, "session_id", out id) || TryGetString(payload, "sessionId", out id))) return id;
        }
        return null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = property.GetString());
    }
    private static async Task WriteRequestAsync(StreamWriter writer, int id, string method, object parameters, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task WriteNotificationAsync(StreamWriter writer, string method, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { method }).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(StreamReader reader, int expectedId, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var response = JsonDocument.Parse(line);
            if (!response.RootElement.TryGetProperty("id", out var id) || id.GetInt32() != expectedId) { response.Dispose(); continue; }
            if (response.RootElement.TryGetProperty("error", out _)) { response.Dispose(); throw new InvalidOperationException("Codex app-server rejected the compatibility request."); }
            return response;
        }
        throw new InvalidOperationException("Codex app-server closed its output before responding.");
    }
}
