using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Windows;

namespace CodexHistorySync.IntegrationTests;

public sealed class CodexSessionRegistrarTests
{
    [Theory]
    [InlineData("pagination")]
    [InlineData("hidden")]
    [InlineData("read-error")]
    [InlineData("wrong-path")]
    public async Task RegistrationUsesConfiguredProviderAndVerifiesExactDatabaseEntry(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codex-register-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        var session = Path.Combine(root, "sessions", "rollout.jsonl");
        await File.WriteAllTextAsync(session, "synthetic fixture");
        var script = Path.Combine(root, "server.ps1");
        await File.WriteAllTextAsync(script, " $scenario = '" + scenario + "'\n" + """
            $readSeen = $false
            while ($line = [Console]::In.ReadLine()) {
                $request = $line | ConvertFrom-Json
                if ($request.method -eq 'initialized') { continue }
                $result = $null
                switch ($request.method) {
                    'initialize' { $result = @{ userAgent = 'test-codex' } }
                    'config/read' { $result = @{ config = @{ model_provider = 'private-backend' } } }
                    'thread/read' {
                        if ($request.params.includeTurns -ne $false -or $request.params.threadId -ne 'test-id') { exit 20 }
                        if ($scenario -eq 'read-error') {
                            [Console]::Out.WriteLine((@{ id=$request.id; error=@{code=-1;message='secret transcript https://token@example.test'} } | ConvertTo-Json -Compress -Depth 10))
                            continue
                        }
                        $readSeen = $true
                        $nativePath = Join-Path $env:CODEX_HOME 'sessions\rollout.jsonl'
                        if ($scenario -eq 'wrong-path') { $nativePath = Join-Path $env:CODEX_HOME 'sessions\other.jsonl' }
                        $result = @{ thread = @{ id='test-id'; path=$nativePath } }
                    }
                    'thread/list' {
                        if (-not $readSeen -or $request.params.useStateDbOnly -ne $true -or $request.params.archived -ne $false -or
                            $request.params.modelProviders[0] -ne 'private-backend' -or $request.params.sourceKinds[0] -ne 'cli') { exit 21 }
                        if ($scenario -eq 'hidden') { $result = @{ data=@(); nextCursor=$null } }
                        elseif (-not $request.params.cursor) { $result = @{ data=@(@{id='unrelated';path='unused'}); nextCursor='page-2' } }
                        else { $result = @{ data=@(@{id='test-id';path=(Join-Path $env:CODEX_HOME 'sessions\rollout.jsonl')}); nextCursor=$null } }
                    }
                    default { exit 22 }
                }
                [Console]::Out.WriteLine((@{id=$request.id; result=$result} | ConvertTo-Json -Compress -Depth 10))
            }
            """);
        var executable = Path.Combine(root, "codex.cmd");
        await File.WriteAllTextAsync(executable, "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0server.ps1\"\r\n");
        try
        {
            await using var registration = await CodexSessionRegistrar.StartAsync(executable, root, CancellationToken.None);
            Assert.Equal("private-backend", registration.ModelProvider);
            if (scenario == "pagination")
                await registration.RegisterAsync("test-id", session, CancellationToken.None);
            else
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => registration.RegisterAsync("test-id", session, CancellationToken.None));
                Assert.DoesNotContain("secret transcript", error.Message);
                Assert.DoesNotContain("token@", error.Message);
            }
            Assert.Equal("synthetic fixture", await File.ReadAllTextAsync(session));
        }
        finally { await DeleteProfileAsync(root); }
    }

    [Fact]
    public async Task CopyAppearsInAlreadyRunningCodexDatabaseWithProviderFilter()
    {
        var executable = new CodexExecutableLocator().ResolveWithSource().ExecutablePath;
        if (executable is null) return; // Real-binary contract check, when Codex is installed.
        var root = Path.Combine(Path.GetTempPath(), $"codex-register-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = CodexPaths.ResolveLayout(root);
        try
        {
            // Keep a server open on an initialized, empty database throughout the copy. The
            // old implementation could pass its fresh-home probe yet fail this exact query.
            await using var client = await CodexAppServerConnection.StartAsync(executable, root, CancellationToken.None, experimentalApi: true);
            using var before = await client.RequestAsync("thread/list", new { useStateDbOnly = true }, CancellationToken.None);
            Assert.Empty(before.RootElement.GetProperty("result").GetProperty("data").EnumerateArray());
            var writer = new CodexConversationWriter(paths,
                new CodexExecutableOption(executable, CodexExecutableAvailability.Discovered), new CodexCompatibilityProbe());
            var source = new PortableConversation(ConversationAgent.Grok, "synthetic-source", "Imported test", root,
                DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1),
                [new PortableTurn(ConversationRole.Assistant, "Opening answer"),
                 new PortableTurn(ConversationRole.User, "Synthetic question"),
                 new PortableTurn(ConversationRole.Assistant, "Первый ответ"),
                 new PortableTurn(ConversationRole.Assistant, "Second answer with\n\ncode and <tags>"),
                 new PortableTurn(ConversationRole.User, "Follow-up question"),
                 new PortableTurn(ConversationRole.Assistant, "Final answer")]);

            var copy = await writer.WriteAsync(source, CancellationToken.None);

            using var after = await client.RequestAsync("thread/list", new
            {
                useStateDbOnly = true, modelProviders = new[] { "openai" }, sourceKinds = new[] { "cli", "vscode" }, archived = false
            }, CancellationToken.None);
            var thread = Assert.Single(after.RootElement.GetProperty("result").GetProperty("data").EnumerateArray());
            Assert.Equal(copy.SessionId, thread.GetProperty("id").GetString());
            Assert.Equal("openai", thread.GetProperty("modelProvider").GetString());
            using var read = await client.RequestAsync("thread/read", new { threadId = copy.SessionId, includeTurns = true }, CancellationToken.None);
            var items = read.RootElement.GetProperty("result").GetProperty("thread").GetProperty("turns")
                .EnumerateArray().SelectMany(turn => turn.GetProperty("items").EnumerateArray()).ToArray();
            var visible = items.Where(item => item.GetProperty("type").GetString() is "userMessage" or "agentMessage")
                .Select(item => item.GetProperty("type").GetString() == "userMessage"
                    ? new PortableTurn(ConversationRole.User, string.Concat(item.GetProperty("content").EnumerateArray().Select(block => block.GetProperty("text").GetString())))
                    : new PortableTurn(ConversationRole.Assistant, item.GetProperty("text").GetString()!)).ToArray();
            Assert.Equal(source.Turns, visible);
            // A nonempty, listed thread is not enough: the old format silently lost every
            // assistant bubble. The production copy gate must reject exactly that format.
            var lines = await File.ReadAllLinesAsync(copy.NativePath);
            var brokenPath = Path.Combine(root, Path.GetFileName(copy.NativePath));
            await File.WriteAllLinesAsync(brokenPath, lines.Where(line =>
            {
                using var record = System.Text.Json.JsonDocument.Parse(line);
                var value = record.RootElement;
                return value.GetProperty("type").GetString() != "event_msg" ||
                    value.GetProperty("payload").GetProperty("type").GetString() != "agent_message";
            }));
            var broken = await new CodexCompatibilityProbe().ProbeAsync(executable, brokenPath, CancellationToken.None);
            Assert.False(broken.IsCompatible);
            // The VS Code client resumes without turns and then requests paginated history.
            // This must preserve answers too, including after Codex migrates the rollout.
            using var resumed = await client.RequestAsync("thread/resume", new
            {
                threadId = copy.SessionId, cwd = root, excludeTurns = true
            }, CancellationToken.None);
            using var page = await client.RequestAsync("thread/turns/list", new
            {
                threadId = copy.SessionId, itemsView = "full", sortDirection = "asc", limit = 100
            }, CancellationToken.None);
            using var paginatedThread = System.Text.Json.JsonDocument.Parse(
                "{\"turns\":" + page.RootElement.GetProperty("result").GetProperty("data").GetRawText() + "}");
            CodexConversationVisibility.EnsureMatches(source, paginatedThread.RootElement);
        }
        finally { await DeleteProfileAsync(root); }
    }

    private static async Task DeleteProfileAsync(string root)
    {
        // Native Codex/Git can briefly retain temporary pack files after process exit.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception error) when (attempt < 49 && error is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }
}
