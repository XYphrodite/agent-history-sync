# Single-Line Session Titles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every manager session occupy one terminal row, derive useful Grok titles from native `text` blocks, and keep the selected row visible in a constrained terminal.

**Architecture:** Normalize whitespace at the catalog boundary so all locally discovered titles are stable single-line values. Retain a second normalization guard in the Spectre view for states supplied by other callers, and keep the existing viewport/navigation model unchanged.

**Tech Stack:** C# 14, .NET 10, Spectre.Console 0.57.2, xUnit.

## Global Constraints

- Every session occupies exactly one terminal row.
- Replace every run of whitespace, including line breaks and tabs, with one ordinary space and trim the result.
- Empty normalized titles retain the existing session-ID fallback.
- Grok user records may identify the role through either `role` or `type`.
- Grok content blocks of type `text` and `input_text` are accepted; arbitrary block types remain ignored.
- Existing 80-character catalog bound, column-width truncation, navigation, selection styling, timestamps, safe non-markup rendering, and malformed-record behavior remain unchanged.
- Use strict RED→GREEN TDD for every production change.

---

### Task 1: Normalize catalog titles and recognize native Grok text blocks

**Files:**
- Modify: `src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs:365-406,471-475`
- Test: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs`

**Interfaces:**
- Consumes: existing `ReadGrokUserPreviewAsync`, `ReadTextContent`, `Preview`, and `DisplayTitle` private helpers.
- Produces: `ManagedSession.Title` values that are trimmed, single-line, bounded to 80 characters, and fall back to `SessionId` when normalization is empty.

- [ ] **Step 1: Add failing native-Grok and whitespace-normalization tests**

Add tests that create real fixture files and call `LocalSessionCatalog.ScanAsync`:

```csharp
[Fact]
public async Task ScanAsyncReadsNativeGrokTextBlockAndNormalizesWhitespace()
{
    await using var fixture = new CatalogFixture();
    const string id = "53000000-0000-0000-0000-000000000003";
    var session = fixture.GrokPaths.SessionDirectory(fixture.WorkingDirectory, id);
    Directory.CreateDirectory(session);
    await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"),
        JsonSerializer.Serialize(new
        {
            type = "user",
            content = new[] { new { type = "text", text = "  First\r\n\t  Grok   question  " } }
        }) + "\n", new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(session, "summary.json"),
        JsonSerializer.Serialize(new
        {
            info = new { id, cwd = fixture.WorkingDirectory, title = (string?)null,
                updated_at = "2026-08-09T13:00:00Z" }
        }), new UTF8Encoding(false));

    var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

    Assert.Equal("First Grok question", Assert.Single(snapshot.Grok).Title);
}

[Fact]
public async Task ScanAsyncNormalizesExplicitTitleAndFallsBackWhenItIsOnlyWhitespace()
{
    await using var fixture = new CatalogFixture();
    await fixture.WriteCodexAsync("normalized", "  Multi\r\n\t line   title  ", "question",
        "2026-08-09T12:00:00Z");
    await fixture.WriteCodexAsync("fallback", " \r\n\t ", "", "2026-08-09T11:00:00Z");

    var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

    Assert.Equal("Multi line title", snapshot.Codex[0].Title);
    Assert.Equal("fallback", snapshot.Codex[1].Title);
}
```

- [ ] **Step 2: Run focused tests and verify RED**

Run:

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncReadsNativeGrokTextBlockAndNormalizesWhitespace|FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncNormalizesExplicitTitleAndFallsBackWhenItIsOnlyWhitespace"
```

Expected: the native Grok title remains its UUID and explicit multi-line title is not collapsed.

- [ ] **Step 3: Implement a single catalog normalization path**

Add a private helper that scans Unicode whitespace without regex allocation:

```csharp
private static string? NormalizeTitle(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var builder = new StringBuilder(value.Length);
    var pendingSpace = false;
    foreach (var character in value.Trim())
    {
        if (char.IsWhiteSpace(character))
        {
            pendingSpace = builder.Length > 0;
            continue;
        }
        if (pendingSpace) builder.Append(' ');
        builder.Append(character);
        pendingSpace = false;
    }
    return builder.Length == 0 ? null : builder.ToString();
}
```

Use it in both `Preview` and `DisplayTitle`, applying the existing `MaximumTitleLength` after normalization. Change `ReadGrokUserPreviewAsync` to accept `role` or `type` equal to `user`. Generalize `ReadTextContent` to accept an explicit set of allowed block types, calling it with `text` plus `input_text` for Grok and only `input_text` for Codex.

- [ ] **Step 4: Run focused and full Core tests**

Run the Step 2 command, then:

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release
```

Expected: both new regressions and all Core tests pass.

- [ ] **Step 5: Commit Task 1**

```powershell
git add src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs
git commit -m "fix: normalize discovered session titles"
```

---

### Task 2: Enforce one terminal row at the view boundary

**Files:**
- Modify: `src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs:246-263`
- Test: `tests/CodexHistorySync.IntegrationTests/SpectreSessionManagerViewTests.cs`

**Interfaces:**
- Consumes: `ManagedSession.Title`, `SessionManagerState.SetViewportRows`, and the existing `FormatTitle` column-budget logic.
- Produces: one physical rendered row per visible session even when an external caller supplies a multi-line title.

- [ ] **Step 1: Add failing view regression**

```csharp
[Fact]
public void Render_collapses_multiline_title_and_keeps_selected_row_in_small_viewport()
{
    var sessions = Enumerable.Range(0, 8)
        .Select(index => Session(ManagedAgent.Codex, $"id-{index}",
            index == 7 ? "  Selected\r\n\t session   title  " : $"title-{index}"))
        .ToArray();
    var state = new SessionManagerState(Snapshot(sessions, []));
    for (var index = 0; index < 7; index++)
        state = state.ApplyNavigation(SessionManagerCommand.MoveDown);
    var console = CreateConsole(out var output, 80, 10);

    new SpectreSessionManagerView(console, new FakeInput()).Render(state);

    var rendered = output.ToString();
    Assert.Contains("Selected session title", rendered);
    Assert.DoesNotContain("Selected\r", rendered);
    Assert.DoesNotContain("title-0", rendered);
}
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SpectreSessionManagerViewTests.Render_collapses_multiline_title_and_keeps_selected_row_in_small_viewport"
```

Expected: the normalized single-line title assertion fails because `FormatTitle` currently preserves embedded whitespace.

- [ ] **Step 3: Add defensive normalization in `FormatTitle`**

Extract or add a private view-local `NormalizeWhitespace` helper with the same whitespace contract as Task 1. Call it before measuring/truncating the title. Keep `Text` rendering rather than `Markup`, and do not alter viewport row calculation or table/panel dimensions.

- [ ] **Step 4: Run focused and manager/view suites**

Run the Step 2 command, then:

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SessionManager|FullyQualifiedName~SpectreSessionManagerViewTests"
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit Task 2**

```powershell
git add src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs tests/CodexHistorySync.IntegrationTests/SpectreSessionManagerViewTests.cs
git commit -m "fix: keep manager titles on one row"
```

---

### Task 3: Whole-product verification and delivery

**Files:**
- Verify: `CodexHistorySync.sln`
- Produce: `artifacts/agent-sync-win-x64/agent-sync.exe`

**Interfaces:**
- Consumes: Task 1 catalog titles and Task 2 one-row rendering guard.
- Produces: reviewed commits, a verified single-file Windows executable, and synchronized `origin/main` after local merge.

- [ ] **Step 1: Run the complete Release suite**

```powershell
dotnet test CodexHistorySync.sln -c Release --no-restore
```

Expected: zero failures.

- [ ] **Step 2: Request a scoped code review**

Review the branch diff against its merge base. The merge gate is no Critical or Important findings; any finding must be addressed with its own RED→GREEN regression before continuing.

- [ ] **Step 3: Publish and smoke-test the executable**

```powershell
dotnet publish src\CodexHistorySync.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts\agent-sync-win-x64
.\artifacts\agent-sync-win-x64\agent-sync.exe --help
```

Expected: exactly one `agent-sync.exe`, no PDB, exit code 0, and help includes `[--manage]`.

- [ ] **Step 4: Merge locally and verify the merged result**

After explicit integration choice, fast-forward the isolated branch into `main`, rerun the complete Release suite, remove only this plan's worktree/branch, and rebuild the executable from merged `main`.

- [ ] **Step 5: Retry remote delivery**

```powershell
git push origin main
git status --short --branch
```

Expected: push succeeds and `main` is synchronized with `origin/main`. If TLS fails again, preserve all local commits and report the network failure without force-pushing.
