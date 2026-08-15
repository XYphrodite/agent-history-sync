# Hide Subagent Sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unconditionally exclude native Codex subagent sessions from `agent-sync --manage` while preserving every ordinary Codex and Grok session.

**Architecture:** Classify Codex sessions while the existing bounded prefix parser processes `session_meta`. Return no catalog candidate when either authoritative subagent marker is present, before index-title projection and rendering; do not add I/O, relationship traversal, or content heuristics.

**Tech Stack:** C# 14, .NET 10, xUnit, System.Text.Json, Spectre.Console CLI.

## Global Constraints

- Subagent sessions are never displayed and there is no opt-in switch.
- Native session files are not deleted or modified.
- Sync scanners, copy/delete operations, and native conversation readers remain unchanged.
- A Codex session is a subagent only when `session_meta.payload.thread_source` equals `subagent` case-insensitively or `session_meta.payload.source.subagent` is an object.
- `parent_thread_id` alone never classifies a session as a subagent.
- Malformed or unsupported marker shapes retain existing readability behavior and do not trigger heuristic filtering.
- Grok discovery remains unchanged because its inspected metadata has no authoritative subagent marker.
- Catalog discovery remains bounded to the existing 64 KiB prefix and adds no new reads, hashes, normalization passes, or stability delays.

---

### Task 1: Filter authoritative Codex subagent metadata

**Files:**
- Modify: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs`
- Modify: `src/CodexHistorySync.Core/Management/CodexSessionCatalogSource.cs`
- Modify: `docs/operations.md`

**Interfaces:**
- Consumes: `CodexSessionCatalogSource.ScanAsync(SessionCatalogReadLimiter, CancellationToken)` and the existing `session_meta.payload` JSON prefix.
- Produces: `CodexSessionCatalogSource` returns no `SessionCatalogCandidate` for authoritative subagent metadata; no public API changes.

- [ ] **Step 1: Add one real-parser regression and its fixture writer**

Add this test near the existing Codex source tests in `LocalSessionCatalogTests.cs`:

```csharp
[Fact]
public async Task CodexSourceExcludesExplicitSubagentsButKeepsParentedUserSession()
{
    // Either authoritative marker must hide a row, while parent_thread_id alone must not.
    await using var fixture = new CatalogFixture();
    await fixture.WriteCodexSourceMetadataAsync(
        "spawned-subagent", "subagent", source: null, parentThreadId: "top-level");
    await fixture.WriteCodexSourceMetadataAsync(
        "guardian-subagent", threadSource: null,
        source: new { subagent = new { other = "guardian" } },
        parentThreadId: "spawned-subagent");
    await fixture.WriteCodexSourceMetadataAsync(
        "parented-user", threadSource: null, source: null, parentThreadId: "top-level");
    await fixture.WriteCodexIndexAsync(
        new { id = "spawned-subagent", thread_name = "Index must not restore this row" },
        new { id = "parented-user", thread_name = "Visible user session" });

    using var limiter = new SessionCatalogReadLimiter(8);
    var rows = await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
        .ScanAsync(limiter, CancellationToken.None);

    var row = Assert.Single(rows);
    Assert.Equal("parented-user", row.SessionId);
    Assert.Equal("Visible user session", row.Title);
}
```

Add this fixture method beside `WriteCodexAsync`:

```csharp
public async Task WriteCodexSourceMetadataAsync(
    string id,
    string? threadSource,
    object? source,
    string? parentThreadId)
{
    var directory = Path.Combine(CodexPaths.Sessions, "2026", "08", "09");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, $"rollout-{id}.jsonl");
    var metadata = new
    {
        type = "session_meta",
        payload = new
        {
            id,
            timestamp = "2026-08-09T08:00:00Z",
            cwd = WorkingDirectory,
            title = $"{id} title",
            thread_source = threadSource,
            source,
            parent_thread_id = parentThreadId
        }
    };
    var message = new
    {
        type = "response_item",
        payload = new
        {
            type = "message",
            role = "user",
            timestamp = "2026-08-09T12:00:00Z",
            content = new[] { new { type = "input_text", text = $"{id} request" } }
        }
    };
    await File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(metadata) + "\n" + JsonSerializer.Serialize(message) + "\n", Utf8);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~CodexSourceExcludesExplicitSubagentsButKeepsParentedUserSession"
```

Expected: FAIL because all three rows are currently returned and `Assert.Single(rows)` receives three items.

- [ ] **Step 3: Add the minimal metadata classifier**

In the `session_meta` branch of `CodexSessionCatalogSource.ReadMetadataAsync`, reject the session before projecting its ID/title:

```csharp
if (IsSubagent(payload)) return null;
var id = GetString(payload, "id");
```

Add this private helper beside `IsTechnicalPreview`:

```csharp
private static bool IsSubagent(JsonElement payload) =>
    string.Equals(GetString(payload, "thread_source"), "subagent", StringComparison.OrdinalIgnoreCase) ||
    payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object &&
    source.TryGetProperty("subagent", out var subagent) && subagent.ValueKind == JsonValueKind.Object;
```

Do not inspect `parent_thread_id`, titles, UUID shape, paths, or user-message text.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the command from Step 2.

Expected: PASS, one visible row named `Visible user session`.

- [ ] **Step 5: Prove both marker branches are load-bearing**

Temporarily change `IsSubagent` to check only `thread_source`, run the focused test, and confirm it fails because `guardian-subagent` appears. Restore the full helper. Then temporarily remove the `thread_source` clause, run the focused test, and confirm it fails because `spawned-subagent` appears. Restore the full helper and rerun for PASS.

- [ ] **Step 6: Document manager membership**

After the bounded-refresh paragraph in `docs/operations.md`, add:

```markdown
Native Codex sessions explicitly marked as subagents (including spawned workers, reviewers, nested workers, and guardians) are internal implementation records and are excluded from the manager. Their files remain untouched. Grok sessions are not classified heuristically from names, paths, or message text.
```

- [ ] **Step 7: Run focused and complete verification**

Run:

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~CodexSource"
dotnet test CodexHistorySync.sln -c Release
git diff --check
```

Expected: all commands exit 0; the full suite has one more Core test than the 545-test baseline.

- [ ] **Step 8: Commit the implementation**

```powershell
git add -- src/CodexHistorySync.Core/Management/CodexSessionCatalogSource.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs docs/operations.md
git commit -m "fix: hide Codex subagent sessions from manager"
```

---

### Task 2: Review and release-smoke the completed branch

**Files:**
- Verify: all files changed since the design commit
- Produce ignored artifact: `artifacts/agent-sync-win-x64-hide-subagents/agent-sync.exe`

**Interfaces:**
- Consumes: the completed manager catalog behavior from Task 1.
- Produces: reviewed, tested single-file Windows CLI artifact; no source API changes.

- [ ] **Step 1: Review the implementation against the specification**

Review the complete branch diff from design commit `091d46d` through the Task 1 HEAD. Check classification precision, bounded I/O preservation, title-index non-reintroduction, ordinary-session preservation, cancellation/error behavior, unchanged Grok behavior, and regression-test strength. Fix every Critical or Important finding with a new failing test before production changes.

- [ ] **Step 2: Run fresh full verification after review**

```powershell
dotnet test CodexHistorySync.sln -c Release --no-restore
```

Expected: exit 0 with no failed or skipped tests.

- [ ] **Step 3: Publish and inspect the Windows artifact**

```powershell
dotnet publish src\CodexHistorySync.Cli\CodexHistorySync.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o artifacts\agent-sync-win-x64-hide-subagents
Get-ChildItem artifacts\agent-sync-win-x64-hide-subagents
.\artifacts\agent-sync-win-x64-hide-subagents\agent-sync.exe --help
```

Expected: the directory contains exactly one `agent-sync.exe`, no PDB, `--help` exits 0, and its usage contains `[--manage]`.

- [ ] **Step 4: Record final status**

Confirm `git status --short` contains no tracked changes and report the commit SHA, focused/full test counts, review verdict, artifact path, and any remaining concerns. Do not merge or push without the user's explicit choice.
