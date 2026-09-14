using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Core.Tests.Viewing;

public sealed class SessionTraceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "agent-sync-trace-tests-" + Guid.NewGuid().ToString("N"));
    private CodexPaths Paths => new(root, Path.Combine(root, "sessions"), Path.Combine(root, "archived_sessions"), Path.Combine(root, "attachments"));

    [Fact]
    public async Task ReaderPreservesOrderedToolsAndCorrelatesResultsWithoutDuplicatingEventMessages()
    {
        var session = await WriteAsync("parent");
        await AppendAsync(session, "response_item", new { type = "message", role = "user", content = new[] {
            new { type = "input_text", text = "<environment_context>context</environment_context>" },
            new { type = "input_text", text = "Find the bug" } } });
        await AppendAsync(session, "response_item", new { type = "function_call", name = "exec_command", call_id = "c1", arguments = "{\"cmd\":\"rg bug\"}" });
        await AppendAsync(session, "response_item", new { type = "function_call_output", call_id = "c1", output = "source.cs:99 BUG" });
        await AppendAsync(session, "event_msg", new { type = "agent_message", message = "Done" });
        await AppendAsync(session, "response_item", new { type = "reasoning", summary = "not visible" });
        await AppendAsync(session, "response_item", new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Done" } } });

        var trace = await new SessionTraceReader().ReadAsync(session, CancellationToken.None);
        Assert.Equal(new[] { TraceEntryKind.User, TraceEntryKind.ToolCall, TraceEntryKind.ToolResult, TraceEntryKind.Assistant }, trace.Entries.Select(entry => entry.Kind));
        Assert.Equal("Find the bug", trace.Entries[0].Text);
        Assert.Equal("exec_command · result", trace.Entries[2].Label);
        Assert.Equal("c1", trace.Entries[2].CallId);
        Assert.Empty(trace.Warnings);
    }

    [Fact]
    public async Task ReaderReportsIncompleteTailAndKeepsCompleteMessages()
    {
        var session = await WriteAsync("live");
        await AppendAsync(session, "response_item", new { type = "custom_tool_call", name = "apply_patch", call_id = "p1", input = "*** Begin Patch" });
        await File.AppendAllTextAsync(session.NativePath, "{\"type\":");
        var trace = await new SessionTraceReader().ReadAsync(session, CancellationToken.None);
        Assert.Single(trace.Entries);
        Assert.Single(trace.Warnings);
        Assert.Contains("incomplete", trace.Warnings[0]);
    }

    [Fact]
    public async Task ReaderRejectsReplacedIdentity()
    {
        var session = await WriteAsync("original");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SessionTraceReader().ReadAsync(
            session with { SessionId = "another-session" }, CancellationToken.None));
    }

    [Fact]
    public async Task ReaderKeepsEarlierMessagesWhenLiveTailEndsInsideUtf8Character()
    {
        var session = await WriteAsync("live-utf8");
        await AppendAsync(session, "response_item", new { type = "message", role = "assistant", content = "Complete answer" });
        await File.AppendAllTextAsync(session.NativePath, "{\"type\":\"response_item\",\"payload\":{\"text\":\"");
        await using (var stream = new FileStream(session.NativePath, FileMode.Append))
            await stream.WriteAsync(new byte[] { 0xD0 });
        var trace = await new SessionTraceReader().ReadAsync(session, CancellationToken.None);
        Assert.Equal("Complete answer", Assert.Single(trace.Entries).Text);
        Assert.NotEmpty(trace.Warnings);
    }

    [Fact]
    public async Task FamilyUsesExplicitParentageAndIncludesArchivedNestedWorkers()
    {
        var parent = await WriteAsync("parent");
        await WriteAsync("child", "parent", nickname: "Averroes");
        await WriteAsync("grandchild", "child", archived: true);
        await WriteAsync("unrelated", "other-parent");
        await WriteAsync("parented-normal-chat", "parent", marked: false);
        var tree = await new CodexSessionFamilyReader(Paths).ReadAsync(parent, CancellationToken.None);
        var child = Assert.Single(tree.Children);
        Assert.Equal("Averroes · explorer", child.Session.Title);
        Assert.Equal("grandchild", Assert.Single(child.Children).Session.SessionId);
        Assert.Equal(3, tree.DescendantsAndSelf().Count());
    }

    [Fact]
    public async Task FamilyUnderstandsSourceOnlyParentAndExcludesDuplicateIdentities()
    {
        var parent = await WriteAsync("parent");
        var child = await WriteAsync("child", "parent", sourceOnly: true);
        var duplicate = await WriteAsync("duplicate", "parent");
        Directory.CreateDirectory(Paths.ArchivedSessions);
        File.Copy(duplicate.NativePath, Path.Combine(Paths.ArchivedSessions, "duplicate.jsonl"));
        var family = await new CodexSessionFamilyReader(Paths).ReadAsync(parent, CancellationToken.None);
        Assert.Equal(child.SessionId, Assert.Single(family.Children).Session.SessionId);
    }

    [Fact]
    public async Task FamilyRefreshFindsNewWorkersAndDoesNotAttachOrphanCycles()
    {
        var parent = await WriteAsync("parent");
        await WriteAsync("cycle-a", "cycle-b");
        await WriteAsync("cycle-b", "cycle-a");
        await WriteAsync("self", "self");
        var reader = new CodexSessionFamilyReader(Paths);
        Assert.Empty((await reader.ReadAsync(parent, CancellationToken.None)).Children);
        await WriteAsync("new-child", "parent");
        Assert.Single((await reader.ReadAsync(parent, CancellationToken.None)).Children);
    }

    [Fact]
    public async Task FamilyRejectsConflictingParentsAndIdsDuplicatedByOrdinaryChats()
    {
        var parent = await WriteAsync("parent");
        var conflicting = await WriteAsync("conflicting", "parent");
        await File.WriteAllTextAsync(conflicting.NativePath, JsonSerializer.Serialize(new
        {
            type = "session_meta", payload = new { id = "conflicting", thread_source = "subagent", parent_thread_id = "parent",
                source = new { subagent = new { thread_spawn = new { parent_thread_id = "another" } } } }
        }) + "\n");
        await WriteAsync("duplicate", "parent");
        await WriteAsync("duplicate", archived: true);
        Assert.Empty((await new CodexSessionFamilyReader(Paths).ReadAsync(parent, CancellationToken.None)).Children);
    }

    [Fact]
    public async Task VeryBroadSearchIsBounded()
    {
        var session = await WriteAsync("parent");
        var trace = new SessionTrace(session, [new TraceEntry(0, TraceEntryKind.ToolResult, "Tool", new string('x', 20000))], []);
        Assert.Equal(SessionTraceSearch.MaximumMatches, SessionTraceSearch.Find([trace], "x").Count);
    }

    [Fact]
    public async Task SearchFindsLiteralRepeatedMatchesInToolResultsWithOffsets()
    {
        var session = await WriteAsync("parent");
        var trace = new SessionTrace(session, [new TraceEntry(5, TraceEntryKind.ToolResult, "rg", "a.[b] a.[B]")], []);
        var matches = SessionTraceSearch.Find([trace], ".[b]");
        Assert.Equal(new[] { 1, 7 }, matches.Select(match => match.Offset));
        Assert.All(matches, match => Assert.Equal(5, match.EntryIndex));
        Assert.Empty(SessionTraceSearch.Find([trace], "   "));
    }

    [Fact]
    public async Task ExportFamilyCreatesNavigableMarkdownWithToolsAndNeverOverwritesPreviousExport()
    {
        var parent = await WriteAsync("parent");
        var child = await WriteAsync("child", "parent");
        await AppendAsync(child, "response_item", new { type = "function_call_output", call_id = "c1", output = "tool output\n```\ntricky fence" });
        var reader = new SessionTraceReader();
        var exporter = new SessionTraceExporter(reader);
        var family = await new CodexSessionFamilyReader(Paths).ReadAsync(parent, CancellationToken.None);
        var exports = Path.Combine(root, "exports");
        var index = await exporter.ExportFamilyAsync(family, exports, CancellationToken.None);
        Assert.Contains("(codex-child.md)", await File.ReadAllTextAsync(index));
        var content = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(index)!, "codex-child.md"));
        Assert.Contains("````text\ntool output", content.Replace("\r\n", "\n"));
        var another = await exporter.ExportFamilyAsync(family, exports, CancellationToken.None);
        Assert.NotEqual(index, another);
        Assert.True(File.Exists(index));
    }

    [Fact]
    public async Task FailedFamilyExportDoesNotLeaveACompleteLookingBundle()
    {
        var parent = await WriteAsync("parent");
        var missing = new SessionThread(parent with { SessionId = "missing", NativePath = Path.Combine(root, "missing.jsonl") }, []);
        var exports = Path.Combine(root, "exports");
        await Assert.ThrowsAsync<FileNotFoundException>(() => new SessionTraceExporter(new SessionTraceReader())
            .ExportFamilyAsync(new SessionThread(parent, [missing]), exports, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(exports));
    }

    [Fact]
    public async Task CancelledReadAndExportDoNotComplete()
    {
        var session = await WriteAsync("parent");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SessionTraceReader().ReadAsync(session, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexSessionFamilyReader(Paths).ReadAsync(session, cts.Token));
    }

    private async Task<ManagedSession> WriteAsync(string id, string? parent = null, bool archived = false, bool marked = true, string? nickname = null, bool sourceOnly = false)
    {
        var directory = archived ? Paths.ArchivedSessions : Paths.Sessions;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".jsonl");
        var metadata = new Dictionary<string, object?> { ["id"] = id, ["title"] = nickname ?? id };
        if (parent is not null)
        {
            if (marked)
            {
                metadata["source"] = new { subagent = new { thread_spawn = new { parent_thread_id = parent, agent_nickname = nickname, agent_role = "explorer" } } };
                if (!sourceOnly) metadata["thread_source"] = "subagent";
            }
            if (!sourceOnly) metadata["parent_thread_id"] = parent;
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { type = "session_meta", payload = metadata }) + "\n");
        return new ManagedSession(ManagedAgent.Codex, id, path, id, DateTimeOffset.UtcNow, false, true);
    }
    private static Task AppendAsync(ManagedSession session, string type, object payload) => File.AppendAllTextAsync(session.NativePath,
        JsonSerializer.Serialize(new { type, timestamp = "2026-09-14T12:00:00Z", payload }) + "\n");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
