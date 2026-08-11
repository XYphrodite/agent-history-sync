# Flicker-Free Session Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run `agent-sync --manage` in one Spectre live display on an alternate terminal screen, without a full-screen clear on every command.

**Architecture:** Add a library-neutral display-session callback to `ISessionManagerView` and run the existing controller loop inside it. The Spectre view owns one `LiveDisplay`, builds one renderable frame per state, and enters/restores the alternate screen and cursor in a `try/finally` boundary.

**Tech Stack:** .NET 10, C#, Spectre.Console 0.57.2, xUnit.

## Global Constraints

- Preserve the existing panel layout, key mappings, viewport behavior, operation semantics, and safety checks.
- Emit no `ESC[2J` full-screen clear during manager navigation.
- Enter alternate screen and hide the cursor once per run; restore both on normal exit, cancellation, and exception.
- Render messages and confirmation prompts inside the live target, never through concurrent console writes.
- Keep dynamic values as `Text`; do not parse them as Spectre markup.

---

### Task 1: Add the display-session lifecycle boundary

**Files:**
- Modify: `src/CodexHistorySync.Cli/Management/ISessionManagerView.cs`
- Modify: `src/CodexHistorySync.Cli/Management/SessionManagerApplication.cs`
- Modify: `src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs`
- Test: `tests/CodexHistorySync.IntegrationTests/SessionManagerApplicationTests.cs`

**Interfaces:**
- Produces: `Task RunDisplayAsync(Func<CancellationToken, Task> interaction, CancellationToken cancellationToken)` on `ISessionManagerView`.
- Preserves: `Render`, `ReadCommand`, `ConfirmLocalDelete`, and `ShowMessage`.

- [ ] **Step 1: Write the failing controller lifecycle test**

Extend `ScriptedView` with a public `DisplaySessions` counter and a `RunDisplayAsync` method that increments it and invokes the supplied callback. Add a test that runs an application scripted with `Q` and asserts:

```csharp
Assert.Equal(1, view.DisplaySessions);
Assert.Single(view.RenderedStates);
```

The test compiles before the interface change because a concrete class may expose an extra method, but fails with `DisplaySessions == 0` because `SessionManagerApplication` does not use it.

- [ ] **Step 2: Run the lifecycle test and verify RED**

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SessionManagerApplicationTests.RunAsyncUsesOneDisplaySession"
```

Expected: FAIL with expected 1, actual 0.

- [ ] **Step 3: Implement the lifecycle wrapper**

Add the exact method to `ISessionManagerView`. Refactor the application entry point to:

```csharp
public Task RunAsync(CancellationToken cancellationToken) =>
    view.RunDisplayAsync(RunLoopAsync, cancellationToken);

private async Task RunLoopAsync(CancellationToken cancellationToken)
{
    // Existing catalog load and command loop, unchanged.
}
```

For this task, `SpectreSessionManagerView.RunDisplayAsync` only validates the delegate and invokes it. Test views do the same while recording their counter. Do not add terminal control codes yet.

- [ ] **Step 4: Run controller tests and verify GREEN**

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SessionManagerApplicationTests"
```

- [ ] **Step 5: Commit Task 1**

```powershell
git add -- src/CodexHistorySync.Cli/Management/ISessionManagerView.cs src/CodexHistorySync.Cli/Management/SessionManagerApplication.cs src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs tests/CodexHistorySync.IntegrationTests/SessionManagerApplicationTests.cs
git commit -m "refactor: add manager display lifecycle"
```

---

### Task 2: Use one live display and alternate screen

**Files:**
- Modify: `src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs`
- Test: `tests/CodexHistorySync.IntegrationTests/SpectreSessionManagerViewTests.cs`

**Interfaces:**
- Consumes: `RunDisplayAsync` from Task 1 and Spectre `LiveDisplayContext.UpdateTarget`/`Refresh`.
- Produces: one live target per manager run with guaranteed terminal restoration.

- [ ] **Step 1: Write failing alternate-screen and no-clear tests**

Create an ANSI-enabled interactive test console and run a composed application with Down, Right, and Q. Assert literal control-sequence behavior:

```csharp
Assert.Equal(1, Count(rendered, "\u001b[?1049h"));
Assert.Equal(1, Count(rendered, "\u001b[?1049l"));
Assert.Equal(1, Count(rendered, "\u001b[?25l"));
Assert.Equal(1, Count(rendered, "\u001b[?25h"));
Assert.DoesNotContain("\u001b[2J", rendered, StringComparison.Ordinal);
```

Add a second test whose input throws `OperationCanceledException`; assert that the application propagates it and the leave/show sequences still occur exactly once. These tests fail because the current view has no alternate-screen lifecycle and still emits `ESC[2J`.

- [ ] **Step 2: Run the lifecycle view tests and verify RED**

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SpectreSessionManagerViewTests.Composed_loop_uses_one_live_alternate_screen|FullyQualifiedName~SpectreSessionManagerViewTests.Display_session_restores_terminal_after_cancellation"
```

- [ ] **Step 3: Implement the Spectre live session**

Use constants:

```csharp
private const string EnterDisplay = "\u001b[?1049h\u001b[?25l";
private const string LeaveDisplay = "\u001b[?25h\u001b[?1049l";
```

`RunDisplayAsync` writes `EnterDisplay`, starts exactly one live display, stores its callback context only for the callback duration, and restores state in `finally`:

```csharp
console.Write(new ControlCode(EnterDisplay));
try
{
    await console.Live(new Text(string.Empty))
        .AutoClear(true)
        .Overflow(VerticalOverflow.Crop)
        .StartAsync(async context =>
        {
            liveContext = context;
            await interaction(cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
}
finally
{
    liveContext = null;
    console.Write(new ControlCode(LeaveDisplay));
}
```

Extract `BuildFrame(SessionManagerState)` returning one `Rows` renderable containing Columns, a fixed message row, and footer. `Render` retains the latest state, calls `UpdateTarget(frame)` and `Refresh` when live, and writes the frame directly only when invoked outside a display session by focused layout tests. Remove `ESC[2J ESC[H` entirely.

- [ ] **Step 4: Run all Spectre view tests and verify GREEN**

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SpectreSessionManagerViewTests"
```

- [ ] **Step 5: Commit Task 2**

```powershell
git add -- src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs tests/CodexHistorySync.IntegrationTests/SpectreSessionManagerViewTests.cs
git commit -m "feat: render manager in one live screen"
```

---

### Task 3: Integrate messages and confirmation into live frames

**Files:**
- Modify: `src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs`
- Test: `tests/CodexHistorySync.IntegrationTests/SpectreSessionManagerViewTests.cs`

**Interfaces:**
- Consumes: retained `lastState`, `liveContext`, and `BuildFrame` from Task 2.
- Produces: message and confirmation updates without direct writes during a live display.

- [ ] **Step 1: Write failing live-message tests**

Update the composed refusal test to run inside the live session and assert the refusal text appears after the copy command without depending on the removed clear sequence. Add a confirmation test using Delete, Y, and Q that asserts:

```csharp
Assert.Contains("Local only: delete", rendered);
Assert.Contains("Sync may restore it", rendered);
Assert.DoesNotContain("\u001b[2J", rendered, StringComparison.Ordinal);
```

Add an initial catalog-failure test that verifies `Session refresh failed.` is written after leaving the alternate screen, so it remains visible on the restored terminal.

- [ ] **Step 2: Run the message tests and verify RED**

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SpectreSessionManagerViewTests.Composed_loop_keeps_refusal_visible|FullyQualifiedName~SpectreSessionManagerViewTests.Live_confirmation_uses_frame|FullyQualifiedName~SpectreSessionManagerViewTests.Initial_failure_survives_alternate_screen_exit"
```

- [ ] **Step 3: Implement frame-owned messages**

`ShowMessage` stores a `PendingMessage`; it must not call `console.Write` while `liveContext` is present. `BuildFrame` consumes the pending message into a styled `Text` row while preserving it for the current rendered frame.

`ConfirmLocalDelete` requires `lastState`, sets a confirmation message, refreshes that state, reads the key, and clears/refreshes the prompt in `finally`. If `ShowMessage` occurs before any state was rendered, retain it as an exit message; after `LeaveDisplay` has restored the normal terminal, write that message once with `WriteMessage`.

- [ ] **Step 4: Run combined controller and view tests and verify GREEN**

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SessionManagerApplicationTests|FullyQualifiedName~SpectreSessionManagerViewTests"
```

- [ ] **Step 5: Commit Task 3**

```powershell
git add -- src/CodexHistorySync.Cli/Management/SpectreSessionManagerView.cs tests/CodexHistorySync.IntegrationTests/SpectreSessionManagerViewTests.cs
git commit -m "fix: keep manager prompts inside live frame"
```

---

### Task 4: Review, verify, publish, and smoke-test

**Files:**
- Modify only files listed above if review finds a demonstrated defect.

**Interfaces:**
- Consumes: completed live display behavior.
- Produces: merge-ready verification evidence and a runnable Windows artifact.

- [ ] **Step 1: Request independent code review**

Review the complete implementation diff against `docs/superpowers/specs/2026-08-11-flicker-free-session-manager-design.md`, focusing on terminal restoration, nested Spectre writes, cancellation, message lifetime, and test realism. Resolve every Critical or Important finding through a new RED-to-GREEN cycle.

- [ ] **Step 2: Run focused tests**

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SessionManagerApplicationTests|FullyQualifiedName~SpectreSessionManagerViewTests|FullyQualifiedName~CliTests"
```

- [ ] **Step 3: Run the full solution**

```powershell
dotnet test CodexHistorySync.sln -c Release
```

Expected: zero failures in Core, Windows, Git, and Integration projects.

- [ ] **Step 4: Publish and verify the artifact**

```powershell
dotnet publish src\CodexHistorySync.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts\agent-sync-win-x64
.\artifacts\agent-sync-win-x64\agent-sync.exe --help
```

Expected: one `agent-sync.exe`, no PDB, and help includes `[--manage]`.

- [ ] **Step 5: Perform Windows interactive smoke**

Launch `agent-sync.exe --manage`, navigate repeatedly in both panels, cancel one delete prompt, and exit with Q. Verify no visible full-screen flash occurs and that the PowerShell contents from before launch return after exit. Do not confirm copy or deletion during this smoke.
