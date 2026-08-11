# Cross-Agent Session Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `agent-sync --manage`, a local two-panel console manager that safely lists, copies, and deletes Codex and Grok sessions, including cross-agent conversion into each destination's native format.

**Architecture:** Conversion crosses an agent-neutral `PortableConversation` boundary: native readers extract only metadata and ordered user/assistant text, and native writers create a fresh destination session through validated staging and atomic publication. A terminal-independent application/controller owns selection, focus, refresh, copy, delete, and safe errors; a Spectre.Console view only renders state and maps keys to commands. Local catalog and operations reuse existing path, scanner, active-process, compatibility-probe, and owned-deletion protections without touching synchronization state or GitHub.

**Tech Stack:** .NET 10, C# 14, xUnit, Spectre.Console 0.57.2, `System.Text.Json`, existing Codex/Grok scanners and compatibility probe.

## Global Constraints

- Ship as version `0.4.0` and expose only the explicit entry point `agent-sync --manage`.
- The manager is local-only: do not initialize Git/GitHub services, run synchronization, or mutate baseline, remote index, conflict evidence, or tombstones.
- Show Codex and Grok side by side with `Title` and `Last modified`; support Up/Down, Left/Right, C, Delete, R, Q, and Escape.
- Copy only title, working directory, created/modified timestamps, and ordered user/assistant text; omit tool calls/results, reasoning, patches, telemetry, and system prompts.
- Every copy receives a new UUID; never reuse a source ID or overwrite an existing destination.
- Publish from an owned temporary sibling path only after flush and validation; failures remove only owned staging data and leave source/destination unchanged.
- Active sessions are visible but read-only; revalidate active status and safe containment immediately before copy or delete.
- Delete exactly one local Codex JSONL file or one local Grok session directory after confirmation; a later sync may restore it.
- User-facing errors contain only fixed safe text and safe identifiers, never conversation text, paths, URLs, credentials, or exception messages.
- Malformed sessions stay visible when safely identifiable, but copy/delete are disabled when identity or target safety cannot be established.

---

## File Map

- `src/CodexHistorySync.Core/Conversion/PortableConversation.cs`: portable agent-neutral records and reader/writer contracts.
- `src/CodexHistorySync.Core/Conversion/CodexConversationReader.cs`: safe Codex JSONL-to-portable parsing.
- `src/CodexHistorySync.Core/Conversion/GrokConversationReader.cs`: safe Grok package-to-portable parsing.
- `src/CodexHistorySync.Core/Conversion/CodexConversationWriter.cs`: staged native Codex rollout creation and optional compatibility probing.
- `src/CodexHistorySync.Core/Conversion/GrokConversationWriter.cs`: staged native Grok package creation.
- `src/CodexHistorySync.Core/Management/ManagedSession.cs`: display/action metadata shared by catalog and controller.
- `src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs`: local Codex/Grok enumeration and display metadata.
- `src/CodexHistorySync.Core/Management/LocalSessionOperations.cs`: copy/delete policy, final revalidation, and local-only mutation.
- `src/CodexHistorySync.Cli/Management/SessionManagerState.cs`: focus, selection, viewport, and command state transitions.
- `src/CodexHistorySync.Cli/Management/SessionManagerApplication.cs`: terminal-independent command loop.
- `src/CodexHistorySync.Cli/Management/ISessionManagerView.cs`: view boundary for deterministic tests.
- `src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs`: Spectre.Console rendering, prompts, and key mapping.
- `src/CodexHistorySync.Cli/CliApplication.cs`: route the exact `--manage` argument.
- `src/CodexHistorySync.Cli/SystemCliAdapters.cs`: compose manager dependencies from local paths only.

---

### Task 1: Portable Conversation Model and Native Readers

**Files:**
- Create: `src/CodexHistorySync.Core/Conversion/PortableConversation.cs`
- Create: `src/CodexHistorySync.Core/Conversion/CodexConversationReader.cs`
- Create: `src/CodexHistorySync.Core/Conversion/GrokConversationReader.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Conversion/CodexConversationReaderTests.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Conversion/GrokConversationReaderTests.cs`

**Interfaces:**
- Consumes: Codex rollout JSONL and Grok `chat_history.jsonl`/`summary.json` packages already accepted by the native scanners.
- Produces:

```csharp
public enum ConversationAgent { Codex, Grok }
public enum ConversationRole { User, Assistant }
public sealed record PortableTurn(ConversationRole Role, string Text);
public sealed record PortableConversation(
    ConversationAgent SourceAgent,
    string SourceSessionId,
    string Title,
    string? WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    IReadOnlyList<PortableTurn> Turns);
public interface IConversationReader
{
    Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Add focused Codex reader tests**

Create a fixture with `session_meta`, user/assistant messages, a system message, reasoning, a tool call, and a tool result. Assert the exact metadata and that the resulting turns are only `[User("question"), Assistant("answer")]` in source order. Add failures for no `session_meta`, duplicate/conflicting IDs, invalid UTF-8/JSON, empty text, and a path that is not an allowed JSONL file.

```csharp
var result = await new CodexConversationReader().ReadAsync(path, TestContext.Current.CancellationToken);
Assert.Equal(ConversationAgent.Codex, result.SourceAgent);
Assert.Equal("original-id", result.SourceSessionId);
Assert.Collection(result.Turns,
    turn => Assert.Equal(new PortableTurn(ConversationRole.User, "question"), turn),
    turn => Assert.Equal(new PortableTurn(ConversationRole.Assistant, "answer"), turn));
```

- [ ] **Step 2: Run the Codex tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter FullyQualifiedName~CodexConversationReaderTests`

Expected: FAIL because `PortableConversation` and `CodexConversationReader` do not exist.

- [ ] **Step 3: Implement the portable types and Codex reader**

Parse line by line with strict UTF-8 and `JsonDocument`; accept the established Codex message shapes from current fixtures, require one safe stable session ID, normalize absent title to the first non-empty user-text preview (bounded to 80 characters), preserve timestamp offsets, and never retain skipped payloads or exception text. Reject zero-turn input with `InvalidDataException`.

- [ ] **Step 4: Run the Codex reader tests and verify GREEN**

Run the Step 2 command.

Expected: PASS.

- [ ] **Step 5: Add focused Grok reader tests**

Create a complete native package containing metadata, ordered text, tool/reasoning/system records, and assert identical portable semantics. Add failures for mismatched directory/metadata IDs, missing required files, unsafe IDs, malformed JSON, and an empty readable conversation.

```csharp
var result = await new GrokConversationReader().ReadAsync(sessionDirectory, TestContext.Current.CancellationToken);
Assert.Equal(ConversationAgent.Grok, result.SourceAgent);
Assert.Equal(expectedCreated, result.CreatedAt);
Assert.Equal(new[] { ConversationRole.User, ConversationRole.Assistant }, result.Turns.Select(x => x.Role));
```

- [ ] **Step 6: Run the Grok tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter FullyQualifiedName~GrokConversationReaderTests`

Expected: FAIL because `GrokConversationReader` does not exist.

- [ ] **Step 7: Implement the Grok reader and verify both readers**

Use `GrokSessionPackage` validation before extracting metadata and text. Run:

`dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ConversationReaderTests"`

Expected: PASS for both reader classes.

- [ ] **Step 8: Commit the portable boundary**

```powershell
git add src/CodexHistorySync.Core/Conversion tests/CodexHistorySync.Core.Tests/Conversion
git commit -m "feat: read native sessions into portable conversations"
```

---

### Task 2: Atomic Native Conversation Writers

**Files:**
- Create: `src/CodexHistorySync.Core/Conversion/IConversationWriter.cs`
- Create: `src/CodexHistorySync.Core/Conversion/CodexConversationWriter.cs`
- Create: `src/CodexHistorySync.Core/Conversion/GrokConversationWriter.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Conversion/CodexConversationWriterTests.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Conversion/GrokConversationWriterTests.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/CrossAgentCompatibilityTests.cs`

**Interfaces:**
- Consumes: `PortableConversation`, resolved destination roots, a `Func<Guid>` ID generator, and for Codex an executable discovery result plus `CodexCompatibilityProbe`.
- Produces:

```csharp
public sealed record ConversationWriteResult(string SessionId, string NativePath);
public interface IConversationWriter
{
    Task<ConversationWriteResult> WriteAsync(PortableConversation conversation, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing Grok writer safety tests**

Assert creation of a new UUID directory under the working-directory-derived Grok location, minimal `chat_history.jsonl` plus `summary.json`, preserved portable fields/order, retry after an occupied generated ID, no overwrite, and cleanup of `.agent-sync-*` staging on injected validation or move failure.

```csharp
var result = await writer.WriteAsync(conversation, cancellationToken);
Assert.NotEqual(conversation.SourceSessionId, result.SessionId);
Assert.True(File.Exists(Path.Combine(result.NativePath, "chat_history.jsonl")));
Assert.True(File.Exists(Path.Combine(result.NativePath, "summary.json")));
```

- [ ] **Step 2: Run Grok writer tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter FullyQualifiedName~GrokConversationWriterTests`

Expected: FAIL because the writer contract and implementation do not exist.

- [ ] **Step 3: Implement the Grok writer with staged validation**

Generate JSONL using `Utf8JsonWriter`, flush files, rebuild/validate through `GrokSessionPackage`, reread through `GrokConversationReader`, compare every portable field/turn, then rename the owned sibling staging directory to the non-existing final UUID directory. Retry ID collisions up to 10 times and otherwise fail with a fixed `IOException` message.

- [ ] **Step 4: Run Grok writer tests and verify GREEN**

Run the Step 2 command.

Expected: PASS and no staging path remains in each failure case.

- [ ] **Step 5: Write failing Codex writer and probe tests**

Assert a new UUID rollout JSONL with valid `session_meta` and text message records; timestamp-based Codex directory placement; occupied-ID retry; no overwrite; reader round-trip; cleanup on validation/probe/publish failure. Cover all executable states: valid configured/discovered executable requires a successful probe, probe failure refuses publication, invalid explicit configuration fails hard, and genuine `AutomaticDiscoveryAbsent` skips the probe.

- [ ] **Step 6: Run Codex writer tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter FullyQualifiedName~CodexConversationWriterTests`

Expected: FAIL because `CodexConversationWriter` does not exist.

- [ ] **Step 7: Implement the Codex writer and compatibility gate**

Write to an owned sibling temporary JSONL, force-flush, validate with `CodexConversationReader`, and call `CodexCompatibilityProbe.ProbeAsync(executablePath, stagedPath, cancellationToken)` whenever the existing locator reports `Configured` or `Discovered`. Publish with an atomic rename only after validation; permit the no-probe path only for the locator's explicit `AutomaticDiscoveryAbsent` state.

- [ ] **Step 8: Run writer and real compatibility tests**

Run:

```powershell
dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ConversationWriterTests"
dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter FullyQualifiedName~CrossAgentCompatibilityTests
```

Expected: PASS. The integration fixture proves Codex→Grok and Grok→Codex output is accepted by the destination reader, and runs the disposable Codex probe when a valid executable is available.

- [ ] **Step 9: Commit atomic native writing**

```powershell
git add src/CodexHistorySync.Core/Conversion tests/CodexHistorySync.Core.Tests/Conversion tests/CodexHistorySync.IntegrationTests/CrossAgentCompatibilityTests.cs
git commit -m "feat: write converted sessions atomically"
```

---

### Task 3: Local Catalog, Copy, and Delete Policy

**Files:**
- Create: `src/CodexHistorySync.Core/Management/ManagedSession.cs`
- Create: `src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs`
- Create: `src/CodexHistorySync.Core/Management/LocalSessionOperations.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionOperationsTests.cs`

**Interfaces:**
- Consumes: native roots/scanners, active-session detectors, Task 1 readers, Task 2 writers, and existing safe-path/owned-deletion helpers moved to Core if necessary without widening their deletion authority.
- Produces:

```csharp
public enum ManagedAgent { Codex, Grok }
public sealed record ManagedSession(
    ManagedAgent Agent,
    string SessionId,
    string NativePath,
    string Title,
    DateTimeOffset LastModifiedAt,
    bool IsActive,
    bool CanRead);
public sealed record SessionCatalogSnapshot(
    IReadOnlyList<ManagedSession> Codex,
    IReadOnlyList<ManagedSession> Grok);
public interface ILocalSessionCatalog
{
    Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken);
}
public interface ILocalSessionOperations
{
    Task<string> CopyAsync(ManagedSession source, CancellationToken cancellationToken);
    Task DeleteAsync(ManagedSession source, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing catalog tests**

Cover ordering by descending last-modified time; title extraction/fallback; active entries remaining visible; malformed-but-safely-identifiable entries as `CanRead == false`; absent roots producing empty columns; and no selection of reparse-point or out-of-root candidates.

- [ ] **Step 2: Run catalog tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter FullyQualifiedName~LocalSessionCatalogTests`

Expected: FAIL because management types do not exist.

- [ ] **Step 3: Implement catalog scanning**

Reuse native scanner rules for containment/stability, supplement them with active-state discovery so active sessions are visible, and parse only bounded display metadata. Catch per-session IO/JSON failures and emit safe unreadable entries only when agent, ID, and native target are safely known.

- [ ] **Step 4: Write failing operations tests**

Cover both copy directions; dispatch to the opposite writer; refusal for active/unreadable sources; immediate active recheck; exact-target containment; reparse-point refusal; Codex single-file deletion; Grok single-directory deletion; confirmation-independent service behavior; and byte-for-byte proof that local sync state, tombstones, repository manifests, and conflict directories remain unchanged.

```csharp
await operations.DeleteAsync(session, cancellationToken);
Assert.False(File.Exists(codexJsonl));
Assert.Equal(beforeState, await File.ReadAllBytesAsync(statePath, cancellationToken));
```

- [ ] **Step 5: Run operations tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter FullyQualifiedName~LocalSessionOperationsTests`

Expected: FAIL because `LocalSessionOperations` does not exist.

- [ ] **Step 6: Implement copy/delete policy and final revalidation**

On every action, resolve full paths again, require containment under the selected agent root, reject reparse points on the target and ancestors below that root, rediscover active state, and reject identity/path changes. Copy by reader→opposite writer. Delete with `File.Delete` for one Codex JSONL or an owned-tree deleter constrained to one Grok session directory; never delete a parent and never instantiate sync/storage services.

- [ ] **Step 7: Run all management Core tests**

Run: `dotnet test tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Management"`

Expected: PASS.

- [ ] **Step 8: Commit local management operations**

```powershell
git add src/CodexHistorySync.Core/Management tests/CodexHistorySync.Core.Tests/Management
git commit -m "feat: manage local Codex and Grok sessions"
```

---

### Task 4: Terminal-Independent Session Manager Controller

**Files:**
- Create: `src/CodexHistorySync.Cli/Management/SessionManagerCommand.cs`
- Create: `src/CodexHistorySync.Cli/Management/SessionManagerState.cs`
- Create: `src/CodexHistorySync.Cli/Management/ISessionManagerView.cs`
- Create: `src/CodexHistorySync.Cli/Management/SessionManagerApplication.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/SessionManagerApplicationTests.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/SessionManagerStateTests.cs`

**Interfaces:**
- Consumes: `ILocalSessionCatalog`, `ILocalSessionOperations`, `SessionCatalogSnapshot`.
- Produces:

```csharp
public enum SessionManagerCommand
{
    MoveUp, MoveDown, FocusLeft, FocusRight, Copy, Delete, Refresh, Exit
}
public interface ISessionManagerView
{
    void Render(SessionManagerState state);
    SessionManagerCommand ReadCommand(CancellationToken cancellationToken);
    bool ConfirmLocalDelete(ManagedSession session);
    void ShowMessage(string message, bool isError);
}
```

- [ ] **Step 1: Write failing pure state tests**

Assert focus changes, clamped selection, preserved selection by `(Agent, SessionId)` across refresh, fallback after deletion, independent left/right selection, viewport scrolling, empty lists, and resize to heights from 1 through 100 without negative indexes.

- [ ] **Step 2: Run state tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SessionManagerStateTests`

Expected: FAIL because `SessionManagerState` does not exist.

- [ ] **Step 3: Implement immutable/testable state transitions**

Store `FocusedAgent`, one selected index per panel, one viewport offset per panel, current snapshot, and viewport row count. Expose `ApplyNavigation(command)`, `ReplaceSnapshot(snapshot)`, `SetViewportRows(rows)`, and `SelectedSession`; every method clamps indexes and offsets before returning.

- [ ] **Step 4: Write failing command-loop tests**

Script a fake view through refresh, navigation, copy, confirmed/unconfirmed delete, active refusal, malformed refusal, operation exception, and exit. Assert both panels refresh after successful copy/delete; failed actions keep the loop open and stable; delete prompt says local deletion may be restored by sync; and safe messages contain neither injected path nor injected exception text.

- [ ] **Step 5: Run application tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SessionManagerApplicationTests`

Expected: FAIL because `SessionManagerApplication` does not exist.

- [ ] **Step 6: Implement the command loop**

Initial scan, render, read one command, execute, and repeat until Exit/cancellation. Refuse copy/delete before invoking operations when `IsActive` or `!CanRead`; still rely on operations for last-moment revalidation. Map exception categories to fixed messages such as `Copy failed for session <safe-id>.` without including `Exception.Message`.

- [ ] **Step 7: Run controller tests**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SessionManager"`

Expected: PASS.

- [ ] **Step 8: Commit the controller**

```powershell
git add src/CodexHistorySync.Cli/Management tests/CodexHistorySync.IntegrationTests/SessionManagerApplicationTests.cs tests/CodexHistorySync.IntegrationTests/SessionManagerStateTests.cs
git commit -m "feat: add session manager command loop"
```

---

### Task 5: Spectre.Console View and `--manage` Routing

**Files:**
- Modify: `src/CodexHistorySync.Cli/CodexHistorySync.Cli.csproj`
- Modify: `src/CodexHistorySync.Cli/CliApplication.cs`
- Modify: `src/CodexHistorySync.Cli/SystemCliAdapters.cs`
- Modify: `src/CodexHistorySync.Cli/Program.cs`
- Create: `src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs`
- Modify: `tests/CodexHistorySync.IntegrationTests/CliTests.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/SpectreSessionManagerViewTests.cs`

**Interfaces:**
- Consumes: Task 4 `ISessionManagerView` and `SessionManagerApplication`; Spectre.Console abstractions injectable through `IAnsiConsole`.
- Produces: exact CLI route `agent-sync --manage` and the two-panel interactive terminal.

- [ ] **Step 1: Add the pinned UI dependency and failing routing tests**

Add:

```xml
<PackageReference Include="Spectre.Console" Version="0.57.2" />
```

Test that only `new[] { "--manage" }` invokes an injected `ISessionManagerRunner`, that `--manage extra` returns usage, and help/usage list `[--manage]` without changing existing command parsing or exit codes.

- [ ] **Step 2: Run routing tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~CliTests&Name~Manage"`

Expected: FAIL because `--manage` is not routed.

- [ ] **Step 3: Add the manager runner seam and local-only composition**

Add `ISessionManagerRunner.RunAsync(CancellationToken)` as an optional `CliApplication` dependency, route the single exact flag before command dispatch, and update `Program`/`SystemCliAdapters` to construct native paths, catalog, readers, writers, operations, controller, and view. Do not call `DefaultCliServices.Build`, storage provider construction, authentication, or any network API on this route; add a construction test with throwing Git/GitHub fakes to prove they remain untouched.

- [ ] **Step 4: Write failing Spectre view tests**

Using an injected `IAnsiConsole`/input fake, assert key mappings, two tables with exact headings `Codex`, `Grok`, `Title`, `Last modified`, distinct focus/selection styling, active/unreadable markers, scrolling to selected rows, empty-list rendering, terminal widths/heights at minimum supported dimensions, and confirmation text containing `local only` plus `sync may restore`.

- [ ] **Step 5: Run view tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SpectreSessionManagerViewTests`

Expected: FAIL because the Spectre view does not exist.

- [ ] **Step 6: Implement the Spectre view**

Render a `Columns` layout containing two bordered panels and tables. Compute visible row count from terminal height, escape all dynamic markup, truncate display text only (never the portable data), map `ConsoleKey` values directly to Task 4 commands, and redraw after each command. Show footer controls: `↑/↓ select  ←/→ panel  C copy  Del delete  R refresh  Q/Esc exit`.

- [ ] **Step 7: Run CLI/view tests and publish smoke**

Run:

```powershell
dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~CliTests|FullyQualifiedName~SessionManager|FullyQualifiedName~SpectreSessionManagerViewTests"
dotnet publish src/CodexHistorySync.Cli/CodexHistorySync.Cli.csproj -c Release -o .artifacts/manage-smoke
```

Expected: PASS; `.artifacts/manage-smoke/agent-sync.exe --help` includes `--manage` and the published output contains no separate PDB.

- [ ] **Step 8: Commit the interactive UI**

```powershell
git add src/CodexHistorySync.Cli tests/CodexHistorySync.IntegrationTests
git commit -m "feat: add interactive cross-agent session manager"
```

---

### Task 6: Version 0.4.0, Documentation, and Full Verification

**Files:**
- Modify: `Directory.Build.props`
- Modify: `README.md`
- Modify: `docs/operations.md`
- Modify: `docs/security.md`
- Modify: `scripts/install.ps1`
- Modify: `scripts/publish-release.ps1`
- Modify: `.github/workflows/release.yml` only if its version/asset assertions require 0.4.0-specific text.
- Test: all existing test projects plus the new tests from Tasks 1–5.

**Interfaces:**
- Consumes: completed `agent-sync --manage` feature.
- Produces: user-facing 0.4.0 documentation and release-ready executable behavior.

- [ ] **Step 1: Add failing release-surface behavior tests**

Add `tests/CodexHistorySync.IntegrationTests/ReleaseSurfaceTests.cs` to execute the built CLI and release PowerShell scripts against controlled arguments. Assert assembly/package version `0.4.0`, `agent-sync --manage` help, successful `install.ps1 -?` / `publish-release.ps1 -?`, and the publisher's observable validation message for an invalid version. Do not assert source or documentation text; stale human-facing prose is covered by the explicit release scan in Step 4.

- [ ] **Step 2: Run release-surface tests and verify RED**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ReleaseSurfaceTests`

Expected: FAIL while the built assembly and script behavior still report 0.3.0.

- [ ] **Step 3: Update version and operational documentation**

Set the shared version to `0.4.0`. Document launch, keys, two-panel columns, copied fields, omitted fields, always-new IDs, active-session refusal, local-only/no-network behavior, exact delete semantics, sync restoration warning, compatibility-probe behavior, and safe failure messages. Update install/publish examples to `v0.4.0` without changing the retained install directory `%LOCALAPPDATA%\Programs\CodexHistorySync` or state directory `%LOCALAPPDATA%\CodexHistorySync`.

- [ ] **Step 4: Run the focused release checks**

Run:

```powershell
dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj -c Release --filter FullyQualifiedName~ReleaseSurfaceTests
rg -n -i "0\.3\.0|v0\.3\.0|codex-sync\.exe" README.md docs scripts .github src tests
```

Expected: tests PASS; the scan has no public release-facing stale hits (historical specs/plans may be explicitly excluded rather than rewritten).

- [ ] **Step 5: Run the complete regression suite**

Run: `dotnet test CodexHistorySync.sln -c Release`

Expected: every Core, Git, Windows, and Integration test passes with zero failures.

- [ ] **Step 6: Perform a disposable manual manager smoke test**

Set temporary `CODEX_HOME` and `GROK_HOME` roots containing one inactive fixture each, start `.artifacts/manage-smoke/agent-sync.exe --manage`, verify navigation/copy/refresh/delete/exit, then inspect that only the intended native session was created/deleted and no repository/state/tombstone files appeared or changed. Remove only those explicitly created disposable roots after verifying their resolved absolute paths are under the chosen temporary directory.

- [ ] **Step 7: Verify repository hygiene and commit**

Run:

```powershell
git diff --check
git status --short
```

Then commit only the intended release/doc/test files:

```powershell
git add Directory.Build.props README.md docs/operations.md docs/security.md scripts/install.ps1 scripts/publish-release.ps1 .github/workflows/release.yml tests/CodexHistorySync.IntegrationTests/ReleaseSurfaceTests.cs
git commit -m "docs: prepare agent-sync 0.4.0"
```

- [ ] **Step 8: Final evidence before handoff**

Run fresh from the committed tree:

```powershell
dotnet test CodexHistorySync.sln -c Release
dotnet publish src/CodexHistorySync.Cli/CodexHistorySync.Cli.csproj -c Release -o .artifacts/release-0.4.0
& .\.artifacts\release-0.4.0\agent-sync.exe --help
git status --short --branch
```

Expected: all tests pass, publish succeeds, help shows `--manage`, the executable is named `agent-sync.exe`, and only known pre-existing untracked files remain.
