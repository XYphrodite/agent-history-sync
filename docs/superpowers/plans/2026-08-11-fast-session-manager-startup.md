# Fast Session Manager Startup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `agent-sync --manage` build its complete initial catalog without paying a 50-millisecond stability delay or process lookup for every local session.

**Architecture:** Refactor each native scanner into a two-phase batch: capture all first observations, wait once, then validate and read unchanged candidates. Add an agent-level activity query so catalog construction takes one conservative activity snapshot per available agent while copy/delete retain fresh session-boundary checks.

**Tech Stack:** .NET 8, C# async APIs, xUnit, existing Codex/Grok scanners and management catalog.

## Global Constraints

- Preserve complete-before-first-render UI behavior and the 50-millisecond production stability window.
- Preserve normalization, hashing, duplicate-ID, uncertainty, containment, and readability behavior.
- Treat inaccessible observations and activity-query failures conservatively.
- Copy and delete operations continue to perform fresh active-state checks at their action boundaries.

---

### Task 1: Batch Codex stability scanning

**Files:**
- Modify: `src/CodexHistorySync.Core/Codex/SessionScanner.cs`
- Test: `tests/CodexHistorySync.Core.Tests/Codex/SessionScannerTests.cs`

**Interfaces:**
- Consumes: `CodexPaths`, `SessionScanResult`, and existing `SessionScanner(TimeSpan)`.
- Produces: internal `SessionScanner(Func<CancellationToken, Task>)` test seam and one wait per scan.

- [ ] **Step 1: Write the failing shared-window regression test**

Create two valid Codex files, inject a wait delegate that counts calls and appends a valid record to one file, then assert:

```csharp
Assert.Equal(1, waits);
Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(stable));
Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(changing));
Assert.False(result.IsAbsenceConfirmed(ObjectKind.ActiveSession));
```

The production mutation caught is moving the wait back into the candidate loop: the count becomes two and the shared observation boundary disappears.

- [ ] **Step 2: Run the test and verify RED**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SessionScannerTests.ScanDetailedAsyncUsesOneStabilityWindowAndRejectsAChangedCandidate"
```

Expected: compilation fails because the delegate constructor is absent.

- [ ] **Step 3: Implement one Codex observation window**

Add the internal constructor:

```csharp
internal SessionScanner(Func<CancellationToken, Task> waitForStability)
{
    this.waitForStability = waitForStability ?? throw new ArgumentNullException(nameof(waitForStability));
}
```

Enumerate both roots while preserving per-kind missing/error uncertainty. Store `Path`, `ObjectKind`, and the first `FileObservation` for each accessible candidate; await `waitForStability` once if any candidates remain; then compare a fresh observation before executing the existing full read, normalize, hash, ID, and duplicate logic. Catch the same candidate-level exceptions as today.

- [ ] **Step 4: Run all Codex scanner tests and verify GREEN**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SessionScannerTests"
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit Task 1**

```powershell
git add -- src/CodexHistorySync.Core/Codex/SessionScanner.cs tests/CodexHistorySync.Core.Tests/Codex/SessionScannerTests.cs
git commit -m "perf: batch Codex session stability checks"
```

---

### Task 2: Batch Grok stability scanning

**Files:**
- Modify: `src/CodexHistorySync.Core/Grok/GrokSessionScanner.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Grok/GrokSessionScannerTests.cs`

**Interfaces:**
- Consumes: `GrokPaths`, `GrokSessionPackage`, `SessionScanResult`, and existing `GrokSessionScanner(TimeSpan)`.
- Produces: internal `GrokSessionScanner(Func<CancellationToken, Task>)` test seam and one wait per scan.

- [ ] **Step 1: Write the failing Grok shared-window test**

Create two UUID session directories with valid `summary.json` and `chat_history.jsonl`. Inject a delegate that increments `waits` and appends one valid JSON line to the changing chat. Assert these independently derived outcomes:

```csharp
Assert.Equal(1, waits);
Assert.Contains(result.Objects, item => item.SourcePath == Path.GetFullPath(stableChat));
Assert.DoesNotContain(result.Objects, item => item.SourcePath == Path.GetFullPath(changingChat));
Assert.False(result.IsAbsenceConfirmed(ObjectKind.GrokSession));
```

Use UUID literals `10000000-0000-0000-0000-000000000001` and `20000000-0000-0000-0000-000000000002` under a test-owned temporary home.

- [ ] **Step 2: Run the test and verify RED**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~GrokSessionScannerTests.ScanDetailedAsyncUsesOneStabilityWindowAndRejectsAChangedCandidate"
```

Expected: compilation fails because the delegate constructor is absent.

- [ ] **Step 3: Implement one Grok observation window**

Add the same null-checked delegate constructor. After active-ID and path filtering, capture all first observations, await once when the collection is nonempty, and compare each fresh observation before `GrokSessionPackage.BuildFromDirectory`. Preserve active-ID exclusion, duplicate handling, package hashing, and current exception behavior.

- [ ] **Step 4: Run combined scanner tests and verify GREEN**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~GrokSessionScannerTests|FullyQualifiedName~SessionScannerTests"
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit Task 2**

```powershell
git add -- src/CodexHistorySync.Core/Grok/GrokSessionScanner.cs tests/CodexHistorySync.Core.Tests/Grok/GrokSessionScannerTests.cs
git commit -m "perf: batch Grok session stability checks"
```

---

### Task 3: Snapshot process activity once per agent

**Files:**
- Modify: `src/CodexHistorySync.Core/Management/ManagedSession.cs`
- Modify: `src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs`
- Modify: `src/CodexHistorySync.Cli/SystemCliAdapters.cs`
- Test: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs`
- Test: `tests/CodexHistorySync.Core.Tests/Management/LocalSessionOperationsTests.cs`

**Interfaces:**
- Consumes: `IsActiveAsync(ManagedAgent, string, string, CancellationToken)` for fresh copy/delete checks.
- Produces: `IsAgentActiveAsync(ManagedAgent, CancellationToken)` for catalog snapshots.

- [ ] **Step 1: Write the failing call-count test**

Create two Codex and two Grok sessions. Extend the existing fake with a `TotalQueries` dictionary. Its existing `IsActiveAsync` increments the relevant agent entry. Scan once and assert:

```csharp
Assert.Equal(2, snapshot.Codex.Count);
Assert.Equal(2, snapshot.Grok.Count);
Assert.Equal(1, fixture.ActiveState.TotalQueries[ManagedAgent.Codex]);
Assert.Equal(1, fixture.ActiveState.TotalQueries[ManagedAgent.Grok]);
```

The production mutation caught is returning to per-row process detection. This test compiles against the current interface and fails with two queries per agent.

- [ ] **Step 2: Run the catalog test and verify RED**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~LocalSessionCatalogTests.ScanAsyncChecksActivityOncePerAgent"
```

Expected: FAIL because activity is queried once per row.

- [ ] **Step 3: Implement the agent-level boundary**

Add to `IManagedSessionActiveState`:

```csharp
Task<bool> IsAgentActiveAsync(ManagedAgent agent, CancellationToken cancellationToken);
```

Implement it in `WindowsManagedSessionActiveState` by invoking `codexIsRunning()` or `grokIsRunning()` once. In `LocalSessionCatalog`, query each available agent before constructing its rows, catch non-cancellation exceptions as active, and pass the Boolean into synchronous row construction. The catalog fake's new method increments the same `TotalQueries` entry, so the original RED assertion becomes GREEN without changing its expectation. Update the operations fake only to satisfy the interface. Do not change `LocalSessionOperations`: it must retain `IsActiveAsync` for fresh final checks.

- [ ] **Step 4: Run management and CLI tests and verify GREEN**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~LocalSessionCatalogTests|FullyQualifiedName~LocalSessionOperationsTests"
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~CliTests|FullyQualifiedName~SessionManagerApplicationTests"
```

Expected: all selected tests pass; operation tests still exercise fresh action-boundary checks.

- [ ] **Step 5: Commit Task 3**

```powershell
git add -- src/CodexHistorySync.Core/Management/ManagedSession.cs src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs src/CodexHistorySync.Cli/SystemCliAdapters.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionOperationsTests.cs
git commit -m "perf: snapshot manager process activity"
```

---

### Task 4: Full verification and real-history timing

**Files:**
- Modify only if verification exposes a regression in a file already listed above.

**Interfaces:**
- Consumes: completed behavior from Tasks 1-3.
- Produces: release-suite, publish, and real-profile timing evidence.

- [ ] **Step 1: Run the focused matrix**

```powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SessionScannerTests|FullyQualifiedName~GrokSessionScannerTests|FullyQualifiedName~LocalSessionCatalogTests|FullyQualifiedName~LocalSessionOperationsTests"
```

- [ ] **Step 2: Run the full solution suite**

```powershell
dotnet test CodexHistorySync.sln -c Release
```

Expected: exit code 0 and zero failed tests in all four test projects.

- [ ] **Step 3: Publish the Windows artifact**

```powershell
dotnet publish src\CodexHistorySync.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts\agent-sync-win-x64
```

Expected: one `agent-sync.exe`, no PDB, and help includes `[--manage]`.

- [ ] **Step 4: Record a read-only real-profile timing**

Launch the manager against the default profile, start timing immediately before launch, and exit with `Q` after the first complete frame. Record elapsed time versus the diagnosed 49.1 seconds of serialized waits. Do not copy, delete, refresh, or alter sessions.

- [ ] **Step 5: Inspect final state**

```powershell
git diff --check HEAD~3 HEAD
git status --short --branch
```

Expected: no whitespace errors and no uncommitted implementation files.
