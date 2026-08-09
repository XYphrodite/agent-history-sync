using CodexHistorySync.Core.Codex;

namespace CodexHistorySync.IntegrationTests;

public sealed class CodexCompatibilityProbeTests
{
    [Theory]
    [InlineData("auth.json")]
    [InlineData("AUTH.JSON")]
    [InlineData("state.sqlite")]
    [InlineData("state.sqlite-shm")]
    [InlineData("state.sqlite.jsonl")]
    [InlineData("STATE.SQLITE.JSONL")]
    [InlineData("rollout.txt")]
    public async Task DisallowedSessionFileIsRejectedBeforeCopyOrChildLaunch(string sourceFileName)
    {
        // Removing the file-name gate must launch the child and fail this test.
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"codex-compat-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var sourceSession = Path.Combine(fixtureDirectory, sourceFileName);
        const string sourceContent = "sensitive-session-content";
        await File.WriteAllTextAsync(sourceSession, $"{{\"type\":\"session_meta\",\"payload\":{{\"id\":\"thread-for-test\",\"title\":\"{sourceContent}\"}}}}" + Environment.NewLine);
        var launchMarker = Path.Combine(fixtureDirectory, "child-launched.txt");
        var codex = await FakeCodexAppServer.CreateAsync(fixtureDirectory, launchMarker);

        try
        {
            var result = await new CodexCompatibilityProbe().ProbeAsync(codex, sourceSession, CancellationToken.None);

            Assert.False(result.IsCompatible);
            Assert.Equal("The compatibility session file is not allowed.", result.Diagnostic);
            Assert.DoesNotContain(sourceSession, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sourceContent, result.Diagnostic, StringComparison.Ordinal);
            Assert.False(File.Exists(launchMarker));
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingCodexExecutableReturnsClearDiagnosticWithoutLaunch()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"codex-compat-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var sourceSession = Path.Combine(fixtureDirectory, "rollout.jsonl");
        await File.WriteAllTextAsync(sourceSession,
            "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-for-test\"}}" + Environment.NewLine);
        var missingCodex = Path.Combine(fixtureDirectory, "missing-codex.exe");

        try
        {
            var result = await new CodexCompatibilityProbe().ProbeAsync(missingCodex, sourceSession, CancellationToken.None);

            Assert.False(result.IsCompatible);
            Assert.Equal("unknown", result.CodexVersion);
            Assert.Contains("Codex executable was not found", result.Diagnostic, StringComparison.Ordinal);
            Assert.Contains("CODEX_EXE", result.Diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(missingCodex, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sourceSession, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedJsonlReturnsAnIncompatibleDiagnostic()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"codex-compat-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var sourceSession = Path.Combine(fixtureDirectory, "rollout.jsonl");
        await File.WriteAllTextAsync(sourceSession, "not JSON" + Environment.NewLine);
        // Placeholder path that exists so the probe can reach the JSONL reader (must not be launched).
        var codexPlaceholder = Path.Combine(fixtureDirectory, "unused-codex.exe");
        await File.WriteAllTextAsync(codexPlaceholder, "placeholder");

        try
        {
            var result = await new CodexCompatibilityProbe().ProbeAsync(codexPlaceholder, sourceSession, CancellationToken.None);

            Assert.False(result.IsCompatible);
            Assert.Equal("unknown", result.CodexVersion);
            Assert.Equal("The compatibility session could not be read.", result.Diagnostic);
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistentCleanupFailureReturnsAnIncompatibleDiagnostic()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"codex-compat-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var sourceSession = Path.Combine(fixtureDirectory, "rollout.jsonl");
        await File.WriteAllTextAsync(sourceSession, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-for-test\"}}" + Environment.NewLine);

        try
        {
            var codex = await FakeCodexAppServer.CreateAsync(fixtureDirectory);
            var result = await new CodexCompatibilityProbe(_ => Task.FromResult(false))
                .ProbeAsync(codex, sourceSession, CancellationToken.None);

            Assert.False(result.IsCompatible);
            Assert.Equal("The disposable Codex home could not be deleted.", result.Diagnostic);
            Assert.DoesNotContain(sourceSession, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportedJsonlIsListedWithoutCopyingSqlite()
    {
        // Removing the probe's JSON-RPC handshake, false state-db flag, JSONL import,
        // or thread-ID matching must make this fake app-server reject the request.
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"codex-compat-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var sourceSession = Path.Combine(fixtureDirectory, "rollout.jsonl");
        await File.WriteAllTextAsync(sourceSession, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-for-test\"}}" + Environment.NewLine);

        try
        {
            var codex = await FakeCodexAppServer.CreateAsync(fixtureDirectory);

            var result = await new CodexCompatibilityProbe()
                .ProbeAsync(codex, sourceSession, CancellationToken.None);

            Assert.True(result.IsCompatible, result.Diagnostic);
            Assert.Equal("codex-cli fake", result.CodexVersion);
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LargeChildStderrDoesNotBlockTheCompatibilityProbe()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"codex-compat-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var sourceSession = Path.Combine(fixtureDirectory, "rollout.jsonl");
        await File.WriteAllTextAsync(sourceSession, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-for-test\"}}" + Environment.NewLine);

        try
        {
            var codex = await FakeCodexAppServer.CreateAsync(fixtureDirectory, writeLargeStderr: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await new CodexCompatibilityProbe().ProbeAsync(codex, sourceSession, timeout.Token);

            Assert.True(result.IsCompatible, result.Diagnostic);
        }
        finally
        {
            Directory.Delete(fixtureDirectory, recursive: true);
        }
    }

    private static class FakeCodexAppServer
    {
        public static async Task<string> CreateAsync(string directory, string? launchMarker = null, bool writeLargeStderr = false)
        {
            var scriptPath = Path.Combine(directory, "fake-app-server.ps1");
            var launchLine = launchMarker is null ? string.Empty : $"New-Item -ItemType File -Path '{launchMarker.Replace("'", "''")}' -Force | Out-Null";
            var stderrLine = writeLargeStderr ? "[Console]::Error.Write('x' * 200000)" : string.Empty;
            await File.WriteAllTextAsync(scriptPath, launchLine + Environment.NewLine + stderrLine + Environment.NewLine + """
                $initialize = [Console]::In.ReadLine() | ConvertFrom-Json
                if ($initialize.method -ne 'initialize') { exit 11 }
                [Console]::Out.WriteLine('{"id":1,"result":{"userAgent":"codex-cli fake"}}')

                $initialized = [Console]::In.ReadLine() | ConvertFrom-Json
                if ($initialized.method -ne 'initialized') { exit 12 }

                $threadList = [Console]::In.ReadLine() | ConvertFrom-Json
                if ($threadList.method -ne 'thread/list' -or $threadList.params.useStateDbOnly -ne $false) { exit 13 }
                if (Get-ChildItem -Path $env:CODEX_HOME -Recurse -File -Filter '*.sqlite*') { exit 14 }
                $importedSession = Get-ChildItem -Path (Join-Path $env:CODEX_HOME 'sessions') -Recurse -File -Filter '*.jsonl'
                if ($importedSession.Count -ne 1 -or -not (Select-String -LiteralPath $importedSession.FullName -SimpleMatch -Quiet 'thread-for-test')) { exit 15 }
                $relativeSessionPath = $importedSession.FullName.Substring((Join-Path $env:CODEX_HOME 'sessions').Length).TrimStart('\\')
                if ($relativeSessionPath -notmatch '^\d{4}\\\d{2}\\\d{2}\\[^\\]+\.jsonl$') { exit 16 }
                [Console]::Out.WriteLine('{"id":2,"result":{"data":[{"id":"thread-for-test"}]}}')
                """);

            var launcherPath = Path.Combine(directory, "fake-codex.cmd");
            await File.WriteAllTextAsync(launcherPath, "@echo off" + Environment.NewLine + "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-app-server.ps1\"" + Environment.NewLine);
            return launcherPath;
        }
    }
}
