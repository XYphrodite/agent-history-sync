using CodexHistorySync.Core.Codex;

namespace CodexHistorySync.IntegrationTests;

public sealed class CodexCompatibilityProbeTests
{
    [Fact]
    public async Task MalformedJsonlReturnsAnIncompatibleDiagnostic()
    {
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"codex-compat-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDirectory);
        var sourceSession = Path.Combine(fixtureDirectory, "rollout.jsonl");
        await File.WriteAllTextAsync(sourceSession, "not JSON" + Environment.NewLine);

        try
        {
            var result = await new CodexCompatibilityProbe().ProbeAsync("unused", sourceSession, CancellationToken.None);

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

    private static class FakeCodexAppServer
    {
        public static async Task<string> CreateAsync(string directory)
        {
            var scriptPath = Path.Combine(directory, "fake-app-server.ps1");
            await File.WriteAllTextAsync(scriptPath, """
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
