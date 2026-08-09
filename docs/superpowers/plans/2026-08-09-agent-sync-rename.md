# Agent Sync Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish version 0.3.0 with `agent-sync` as the public CLI and executable while retaining the existing local data store and encrypted formats.

**Architecture:** Keep the existing .NET solution and `CodexHistorySync` namespaces unchanged. Replace only the public command, release asset, installer, documentation, scheduled-task identity, and notification branding; use explicit ownership checks to migrate the old Task Scheduler task safely.

**Tech Stack:** .NET 10, xUnit, PowerShell, GitHub Actions, Windows Task Scheduler.

## Global Constraints

- Public command and release binary: `agent-sync` / `agent-sync.exe`.
- Release version: `0.3.0`.
- Preserve `%LOCALAPPDATA%\CodexHistorySync`, repository format, encryption envelope, and .NET namespaces.
- Remove a legacy `CodexHistorySync` task only when its executable, arguments, user, and trigger match the current installation.
- Do not rename historical design documents or compatibility-test commands that deliberately reference internal project names.

---

### Task 1: Complete CLI and Task Scheduler migration

**Files:**
- Modify: `Directory.Build.props`
- Modify: `src/CodexHistorySync.Cli/CodexHistorySync.Cli.csproj`
- Modify: `src/CodexHistorySync.Cli/CliApplication.cs`
- Modify: `src/CodexHistorySync.Windows/AgentScheduler.cs`
- Test: `tests/CodexHistorySync.IntegrationTests/SecurityBoundaryTests.cs`
- Test: `tests/CodexHistorySync.Windows.Tests/AgentSchedulerTests.cs`

**Interfaces:**
- Consumes: `IAgentTaskStore.GetAsync`, `RegisterAsync`, and `DeleteAsync`.
- Produces: `AgentScheduler.TaskName == "AgentHistorySync"`, `LegacyTaskName == "CodexHistorySync"`, and the `agent-sync` CLI help contract.

- [ ] **Step 1: Add failing migration tests**

Add xUnit coverage showing that `InstallAsync` deletes an owned legacy registration before it creates `AgentHistorySync`, keeps a non-owned legacy registration, and that `UninstallAsync` deletes each owned name. Update the assembly-name assertion to `agent-sync`.

- [ ] **Step 2: Run the focused tests and verify they fail before implementation**

Run: `dotnet test tests/CodexHistorySync.Windows.Tests/CodexHistorySync.Windows.Tests.csproj --filter AgentSchedulerTests && dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj --filter SecurityBoundaryTests`

Expected: failures for absent legacy-task behavior or the old assembly identity.

- [ ] **Step 3: Implement the minimal public rename and migration**

Set both MSBuild version properties to `0.3.0`; set `<AssemblyName>agent-sync</AssemblyName>`; make CLI help and usage print `agent-sync`; register `AgentHistorySync`; use the existing exact-shape ownership comparison before deleting either task name; leave `CodexHistorySync` state paths untouched.

- [ ] **Step 4: Re-run the focused tests**

Run the commands from Step 2.

Expected: PASS.

- [ ] **Step 5: Commit the coherent code and test change**

```powershell
git add Directory.Build.props src/CodexHistorySync.Cli/CodexHistorySync.Cli.csproj src/CodexHistorySync.Cli/CliApplication.cs src/CodexHistorySync.Windows/AgentScheduler.cs tests/CodexHistorySync.IntegrationTests/SecurityBoundaryTests.cs tests/CodexHistorySync.Windows.Tests/AgentSchedulerTests.cs
git commit -m "feat: rename public CLI to agent-sync"
```

### Task 2: Rename release and installer surface

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `scripts/install.ps1`
- Modify: `scripts/publish-release.ps1`
- Test: `tests/CodexHistorySync.IntegrationTests/CliTests.cs` if it asserts generated executable or usage text.

**Interfaces:**
- Consumes: release workflow artifact names and the installer’s release-asset lookup.
- Produces: a release containing `agent-sync.exe` and `agent-sync.exe.sha256`; an installer that installs that executable while retaining the legacy data directory.

- [ ] **Step 1: Add a failing installer/release contract check**

Add or update a text-level test that expects `agent-sync` in user-facing command or asset names without changing tests that cover internal state paths.

- [ ] **Step 2: Run the focused CLI test**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj --filter CliTests`

Expected: FAIL while user-facing references still say `codex-sync`.

- [ ] **Step 3: Update the release pipeline and scripts**

Rename all artifact, checksum, download, copy, and PATH messages to `agent-sync.exe`. Keep `%LOCALAPPDATA%\Programs\CodexHistorySync` unless an explicit installer migration is introduced separately; this prevents an accidental second installation directory.

- [ ] **Step 4: Verify the scripts and focused test**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install.ps1 -?` and the test command from Step 2.

Expected: help and test output use `agent-sync`; no release asset lookup retains `codex-sync.exe`.

- [ ] **Step 5: Commit the release-surface change**

```powershell
git add .github/workflows/release.yml scripts/install.ps1 scripts/publish-release.ps1 tests/CodexHistorySync.IntegrationTests/CliTests.cs
git commit -m "build: publish agent-sync release assets"
```

### Task 3: Update public documentation and verify the complete rename

**Files:**
- Modify: `README.md`
- Modify: `docs/operations.md`
- Modify: `docs/security.md`
- Modify: user-facing notifications in `src/CodexHistorySync.Windows/WindowsNotifier.cs`
- Modify: user-facing application text in `src/CodexHistorySync.Cli/SystemCliAdapters.cs`

**Interfaces:**
- Consumes: the public command and task names produced by Tasks 1–2.
- Produces: consistent command examples, task/notification branding, and an explicit compatibility note for the retained data directory.

- [ ] **Step 1: Inventory stale user-facing names**

Run: `rg -n -i "codex-sync|Codex History Sync" README.md docs scripts .github src/CodexHistorySync.Cli src/CodexHistorySync.Windows`

Expected: classify each match as a public reference to replace or an explicit compatibility boundary to retain.

- [ ] **Step 2: Update user-facing prose and examples**

Replace command examples with `agent-sync`, executable and release references with `agent-sync.exe`, Task Scheduler prose with `AgentHistorySync`, and notification titles with `Agent History Sync`. State that `%LOCALAPPDATA%\CodexHistorySync` is deliberately retained for existing installations.

- [ ] **Step 3: Build and run all tests**

Run: `dotnet test CodexHistorySync.sln -c Release`

Expected: PASS with no warnings treated as errors.

- [ ] **Step 4: Perform the final stale-reference scan**

Run the inventory command from Step 1 and inspect every remaining match. Only namespaces, data-path compatibility references, historical documents, and the explicitly named legacy scheduled-task constant may remain.

- [ ] **Step 5: Commit the documentation and branding change**

```powershell
git add README.md docs/operations.md docs/security.md src/CodexHistorySync.Windows/WindowsNotifier.cs src/CodexHistorySync.Cli/SystemCliAdapters.cs
git commit -m "docs: document agent-sync migration"
```
