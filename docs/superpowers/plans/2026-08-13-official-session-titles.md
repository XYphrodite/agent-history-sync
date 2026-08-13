# Official Session Titles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Display official Codex and Grok session names in `agent-sync --manage`, using filtered history only as a fallback and UUID only when no meaningful name exists.

**Architecture:** Extend `LocalSessionCatalog` with one bounded Codex index read per scan, enrich Grok metadata from official summary fields, and centralize technical-wrapper rejection in fallback preview extraction. Manager state and Spectre rendering continue consuming normalized `ManagedSession.Title` values unchanged.

**Tech Stack:** C# 14, .NET 10, `System.Text.Json`, xUnit.

## Global Constraints

- Codex priority: index `thread_name`, metadata `title`, metadata `thread_name`, first meaningful user message, session ID.
- Grok priority: `generated_title`, string `session_summary`, `info.title`, root `title`, first meaningful user message, session ID.
- Later duplicate Codex index entries win.
- Read at most 64 KiB from the tail of `session_index.jsonl`, discard the initial partial line, select the last 64 complete records, and parse those records in original order.
- Malformed individual index lines are ignored; only safe IDs and string names are accepted.
- Skip fallback messages beginning case-insensitively with an approved technical wrapper after whitespace normalization, but preserve ordinary text containing tags later.
- Existing cancellation, path/reparse safety, timestamp/sort logic, unreadable-session behavior, Unicode normalization, 80-character bound, and safe `Text` rendering remain unchanged.
- No native history, summary, or index file is modified.
- Every production change follows strict RED-GREEN TDD.

---

### Task 1: Bounded Codex official-title index

**Files:**
- Modify: `src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs:46-105,275-325,455-483`
- Test: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs`

**Interfaces:**
- Consumes: `CodexPaths.Home`, safe Codex IDs, the 64 KiB/64-record constants, and title normalization.
- Produces: one `IReadOnlyDictionary<string,string>` per Codex scan whose matching name has first title priority.

- [ ] **Step 1: Add failing official-title and duplicate tests**

Add a fixture helper that writes `Path.Combine(CodexHome, "session_index.jsonl")`. Add these public-behavior cases:

```csharp
[Fact]
public async Task ScanAsyncUsesCodexIndexThreadNameBeforeMetadataAndHistory()
{
    await using var fixture = new CatalogFixture();
    await fixture.WriteCodexAsync("indexed", "Metadata title",
        "<environment_context>technical</environment_context>", "2026-08-09T12:00:00Z");
    await fixture.WriteCodexIndexAsync(
        new { id = "indexed", thread_name = "Official Codex name" });

    var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Codex);

    Assert.Equal("Official Codex name", session.Title);
}

[Fact]
public async Task ScanAsyncUsesLastCodexIndexEntryForDuplicateId()
{
    await using var fixture = new CatalogFixture();
    await fixture.WriteCodexAsync("duplicate", null, "fallback", "2026-08-09T12:00:00Z");
    await fixture.WriteCodexIndexAsync(
        new { id = "duplicate", thread_name = "Old name" },
        new { id = "duplicate", thread_name = "Newest name" });

    var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Codex);

    Assert.Equal("Newest name", session.Title);
}
```

- [ ] **Step 2: Add a failing bounded-tail/malformed-line test**

Create an index larger than 64 KiB with a valid old record outside the retained tail, an initial partial retained line, more than 64 short complete records, a malformed complete line, and a final valid duplicate. Assert the final name wins, the old record is not loaded, and no exception escapes.

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncUsesCodexIndex|FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncReadsBoundedCodexIndexTail"
```

Expected: metadata/history wins because the index is not read.

- [ ] **Step 4: Implement the bounded index reader**

Add `ReadCodexTitleIndexAsync(CodexPaths, CancellationToken)`. Open with `FileShare.ReadWrite | FileShare.Delete`; seek to `max(0, Length - MaximumMetadataBytes)`; read at most 64 KiB; when starting after byte zero, discard through the first newline. Split only complete lines, select the last `MaximumMetadataRecords` lines, then parse them independently in original order. Ignore malformed lines, require `IsSafeCodexSessionId(id)`, normalize nonempty string `thread_name`, and assign into an ordinal-ignore-case dictionary so later entries replace earlier ones. Propagate requested cancellation; missing file and expected I/O/access/decoding/argument failures return an empty map.

Read the map once in `ScanCodexAsync`. Inject the matching official title into the existing metadata title selection while leaving identity and timestamps unchanged.

- [ ] **Step 5: Run focused and full Core tests**

Run Step 3, then:

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release
```

Expected: all pass.

- [ ] **Step 6: Commit**

```powershell
git add src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs
git commit -m "fix: read official Codex session titles"
```

---

### Task 2: Official Grok summary titles

**Files:**
- Modify: `src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs:332-365`
- Test: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs`

**Interfaces:**
- Consumes: existing bounded complete `summary.json` parse.
- Produces: exact official Grok priority before legacy/history fields.

- [ ] **Step 1: Add failing Grok priority tests**

Extend the fixture writer to set root `generated_title`, root `session_summary`, `info.title`, and root `title`. Test:

```csharp
[Fact]
public async Task ScanAsyncUsesGrokGeneratedTitleBeforeOtherSources()
{
    await using var fixture = new CatalogFixture();
    const string id = "54000000-0000-0000-0000-000000000004";
    await fixture.WriteGrokSummaryAsync(id, "Official Grok name", "Summary name",
        "Legacy info", "Legacy root", "<user_info>technical</user_info>");

    var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Grok);

    Assert.Equal("Official Grok name", session.Title);
}

[Fact]
public async Task ScanAsyncUsesStringGrokSessionSummaryWhenGeneratedTitleIsAbsent()
{
    await using var fixture = new CatalogFixture();
    const string id = "55000000-0000-0000-0000-000000000005";
    await fixture.WriteGrokSummaryAsync(id, null, "Official summary",
        "Legacy info", "Legacy root", "fallback");

    var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Grok);

    Assert.Equal("Official summary", session.Title);
}
```

Add a non-string `session_summary` case proving it is ignored and `info.title` is next.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncUsesGrok"
```

Expected: legacy info/history wins.

- [ ] **Step 3: Implement exact Grok priority**

Use:

```csharp
var title = GetString(root, "generated_title")
            ?? GetString(root, "session_summary")
            ?? GetString(info, "title")
            ?? GetString(root, "title");
```

Read history only for null/whitespace. Do not accept object/array summaries or alter timestamps.

- [ ] **Step 4: Run focused and full Core tests**

Run Step 2, then the full Core command from Task 1. Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs
git commit -m "fix: read official Grok session titles"
```

---

### Task 3: Skip injected technical fallback messages

**Files:**
- Modify: `src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs:275-450`
- Test: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs`

**Interfaces:**
- Consumes: normalized Codex/Grok user text and the existing 64-record bound.
- Produces: first meaningful fallback after approved wrapper filtering.

- [ ] **Step 1: Add failing wrapper theories**

Use an xUnit theory over these exact opening tags:

```csharp
[InlineData("<environment_context>")]
[InlineData("<recommended_plugins>")]
[InlineData("<user_info>")]
[InlineData("<system-reminder>")]
[InlineData("<permissions instructions>")]
[InlineData("<skills_instructions>")]
[InlineData("<apps_instructions>")]
[InlineData("<plugins_instructions>")]
```

For both Codex and Grok, write a first user record beginning with whitespace plus the wrapper and a second user record `Real user request`. Assert the catalog title is `Real user request`.

- [ ] **Step 2: Add false-positive and ID-last-resort tests**

Assert `Explain this later <environment_context> tag` remains visible because the tag is not at the normalized start. Also create sessions containing only technical wrappers and no official/legacy title; assert the safe session ID.

- [ ] **Step 3: Run focused tests and verify RED**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncSkipsTechnical|FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncKeepsOrdinaryTextContainingTag|FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncUsesIdWhenOnlyTechnical"
```

Expected: current readers return the first technical record.

- [ ] **Step 4: Implement shared wrapper detection**

Define the eight opening tags once. Add `IsTechnicalPreview(string?)`, operating on normalized preview text with `StartsWith(tag, StringComparison.OrdinalIgnoreCase)`. In both history loops, continue when supported user text is null or technical. Codex must continue until a meaningful preview is found; Grok must not return immediately for a null/technical preview. Preserve the existing 64-record and malformed-record contracts.

- [ ] **Step 5: Run focused, Core, and manager tests**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncSkipsTechnical|FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncKeepsOrdinaryTextContainingTag|FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncUsesIdWhenOnlyTechnical"
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SessionManager|FullyQualifiedName~SpectreSessionManagerViewTests"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```powershell
git add src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs
git commit -m "fix: skip technical session title messages"
```

---

### Task 4: Whole-product verification and delivery

**Files:**
- Verify: `CodexHistorySync.sln`
- Produce: `artifacts/agent-sync-win-x64-official-titles/agent-sync.exe`

**Interfaces:**
- Consumes: reviewed Tasks 1-3.
- Produces: merge-gated branch, verified EXE, merged `main`, and synchronized `origin/main`.

- [ ] **Step 1: Run complete Release suite**

```powershell
dotnet test CodexHistorySync.sln -c Release
```

Expected: zero failures across all four test projects.

- [ ] **Step 2: Run whole-branch review**

Generate a merge-base-to-HEAD package. Review exact priority, bounded-tail correctness, malformed/cancellation behavior, filter false positives, test mutation strength, and scope. No Critical or Important finding may remain.

- [ ] **Step 3: Publish and smoke-test**

```powershell
dotnet publish src\CodexHistorySync.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts\agent-sync-win-x64-official-titles
.\artifacts\agent-sync-win-x64-official-titles\agent-sync.exe --help
```

Expected: one EXE, no PDB, help contains `[--manage]`, exit 0.

- [ ] **Step 4: Merge and deliver after explicit integration choice**

Fast-forward into `main`, rerun the complete suite on merged `main`, remove only this worktree/branch, rebuild from merged `main`, then:

```powershell
git push origin main
git status --short --branch
```

Expected: `main` equals `origin/main`. Never force-push; preserve local commits on network failure.
