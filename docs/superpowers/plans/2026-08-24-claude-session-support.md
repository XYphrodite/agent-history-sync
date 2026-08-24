# Claude Session Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Claude Code as a third first-class agent alongside Codex and Grok CLI, symmetric across sync, the local session manager, and cross-agent copy.

**Design:** [2026-08-24-claude-session-support-design.md](../specs/2026-08-24-claude-session-support-design.md)

**Architecture:** Claude stores one JSONL per session under `%USERPROFILE%\.claude\projects\<mangled-cwd>\<uuid>.jsonl`. Model it as a sync package (`ClaudeSessionPackage`, mirroring `GrokSessionPackage`), a catalog source, and a reader/writer pair. `ManagedAgent`, `SessionCatalogSnapshot`, and the TUI stop being two-valued and become agent-indexed.

**Tech Stack:** .NET 10, xUnit, Spectre.Console, Windows Task Scheduler.

## Global Constraints

- Synchronize **only** `~/.claude/projects/**/*.jsonl`. Never `backups/`, `ide/`, `shell-snapshots/`, `session-env/`, `settings*.json`, or credential material.
- Keep the `CodexHistorySync` namespaces, the `%LOCALAPPDATA%\CodexHistorySync` store, and the encryption envelope unchanged.
- Preserve existing security invariants: reparse-point rejection, canonical containment, owned staging directories, two-observation stability reads, publish-time hash re-verification.
- Never reverse the Claude project-directory mangling; carry `cwd` and the literal segment in the package.
- Every new failure message must be registered in the reason sets in `LocalSessionOperations` (`ActiveCopyFailures`, `ChangedCopyFailures`, `DestinationCopyFailures`, `IncompatibleCopyFailures`, `UnreadableCopyFailures`) or it degrades to `Unspecified`.

---

## Task 1: Claude paths and package format

- [x] `src/CodexHistorySync.Core/Claude/ClaudePaths.cs` — `sealed record ClaudePaths(string Home, string Projects)` with `TryResolve(string? configuredHome = null)`: `CLAUDE_CONFIG_DIR`, then `%USERPROFILE%\.claude`; require `projects/` to exist; return `null` otherwise. Mirror `GrokPaths.TryResolve`, including the exception filter.
- [x] `SessionFilePath(string projectSegment, string sessionId)` validating the segment via `PathSafety.ValidateFileComponent`.
- [x] `src/CodexHistorySync.Core/Claude/ClaudeSessionPackage.cs` — `SchemaVersion = 1`, `LogicalIdPrefix = "cl-"`, UUID-only session ids.
  - `BuildFromFile(string sessionFilePath)` → DTO `{ v, id, cwd, project, transcript }`; `transcript` newline-normalized to `\n`.
  - `Parse`, `HashPackage`, `ToLogicalId`, `SessionIdFromLogicalId`, `IsClaudeLogicalId`.
  - `Materialize(PackageInfo, ClaudePaths)` — temp file + `File.Move(overwrite: true)`.
- [x] Require the `sessionId` embedded in the records to match the file name; reject on mismatch. Read `cwd` from the first record that carries it.
- [x] Tests `tests/CodexHistorySync.Core.Tests/Claude/ClaudeSessionPackageTests.cs`: round-trip, id/file-name mismatch, missing `cwd`, CRLF normalization, unsafe `project` segment, non-UUID name.

## Task 2: Sync scanner

- [x] `src/CodexHistorySync.Core/Claude/ClaudeSessionScanner.cs` modelled on `GrokSessionScanner`: enumerate `projects/**/*.jsonl` with `AttributesToSkip = ReparsePoint`, two-observation stability, `MaxDegreeOfParallelism = Clamp(ProcessorCount, 2, 8)`, duplicate logical ids removed and marked uncertain.
- [x] Active exclusion per design D3: running `claude` process + write inside the stability window ⇒ defer.
- [x] Tests `ClaudeSessionScannerTests`: stable file, file mutating between observations, duplicate ids across project directories, missing projects root, unreadable file.

## Task 3: Sync engine, writer, backups

- [x] Append `ClaudeSession` to `ObjectKind` in `Model/SyncModels.cs` with the doc comment naming the projects-only policy.
- [x] `PathSafety.EnsureOutsideCodex` and `EnsureSessionDestination` accept `ClaudePaths?`: destination must be exactly one level under `Projects` and named `<uuid>.jsonl`.
- [x] `SyncEngine`: scan Claude in parallel with Codex/Grok; merge objects, `UncertainKinds`, `DuplicateIds`; `ResolveClaudeDestination`; stage with `.claudepkg` ([SyncEngine.cs:1117](../../../src/CodexHistorySync.Core/Sync/SyncEngine.cs#L1117)).
- [x] Extend the live-session deferral heuristic that matches the literal `"Grok session"` ([SyncEngine.cs:243](../../../src/CodexHistorySync.Core/Sync/SyncEngine.cs#L243)) to cover Claude.
- [x] `CodexHistoryWriter.ImportClaudePackageAsync` mirroring `ImportGrokPackageAsync`, including the post-materialization hash re-check and the logical-id/package-id equality check.
- [x] `HistoryMutationBatch.ValidateJournal` accepts `ClaudeSession` in its journal-kind allow-list, otherwise conflict resolution rejects every Claude mutation.
- [x] `BackupStore` adds the Claude projects root to its protected roots ([BackupStore.cs:156](../../../src/CodexHistorySync.Core/Sync/BackupStore.cs#L156)).
- [x] Tests: `SyncFailureTests` deferral case, plus a Claude session in the integration round-trip.

## Task 4: Conversation reader and writer

- [ ] Add `Claude` to `ConversationAgent`.
- [ ] `Conversion/ClaudeConversationReader.cs` — read `type: user|assistant`; take `message.content[]` blocks of type `text` only; skip `thinking`, `tool_use`, `tool_result`, and `attachment` records; skip the `ai-title`, `last-prompt`, `queue-operation`, `atis-latch`, and `file-history-snapshot` bookkeeping records; title per design D7 — last `ai-title`, else `summary`, else the first non-technical user turn; timestamps from the records with file-time fallback.
- [ ] Extend `ConversationTechnicalText.Wrappers` with `<ide_opened_file>`, `<ide_selection>`, `<local-command-stdout>`, `<command-name>`, `<command-message>`.
- [ ] `Conversion/ClaudeConversationWriter.cs` — emit one record per portable turn with `parentUuid` chaining, fresh `uuid`, `sessionId`, `cwd`, ISO-8601 `timestamp`, `type`, `message`, `version`; publish through the owned-staging + `IConversationPublicationSeal` machinery used by `GrokConversationWriter`.
- [ ] Tests: `ClaudeConversationReaderTests`, `ClaudeConversationWriterTests`, and a Claude leg in `CrossAgentCompatibilityTests`.

## Task 5: Session catalog and operations

- [ ] Add `Claude` to `ManagedAgent`. Give `SessionCatalogSnapshot` an agent-indexed accessor `For(ManagedAgent)` while keeping `Codex`/`Grok` properties so existing call sites compile.
- [ ] `Management/ClaudeSessionCatalogSource.cs` mirroring `GrokSessionCatalogSource`: bounded prefix read (`64 KiB`, 64 records), title per design D7 — newest `ai-title` inside the window, else `summary`, else first non-technical user text — `MaximumTitleLength = 80`, duplicates demoted to `CanRead = false`.
- [ ] `LocalSessionOperations`: add `CopyAsync(ManagedSession source, ManagedAgent target, CancellationToken)`; keep the two-argument overload resolving the target only when exactly one other agent is configured. Register the new Claude failure strings in the reason sets.
- [ ] `WindowsManagedSessionActiveState.ReadClaudeActiveIds()` implementing D3.
- [ ] Tests: `LocalSessionCatalogTests` and `LocalSessionOperationsTests` gain Claude cases — copy to each target, refusal on an active session, unreadable duplicate.

## Task 6: Session manager TUI

- [ ] `SessionManagerState`: replace the `codexSelectedIndex`/`grokSelectedIndex` and `codexViewportOffset`/`grokViewportOffset` field pairs with per-agent maps; `FocusLeft`/`FocusRight` cycle the configured agents; filtering applies per agent.
- [ ] `SpectreSessionManagerView`: one column per configured agent; prompt for the copy target when more than one destination exists.
- [ ] Agents with no resolvable home are omitted from the layout entirely (a machine without Claude keeps today's two-panel view).
- [ ] Tests: `SessionManagerStateTests`, `SessionManagerApplicationTests`, `SpectreSessionManagerViewTests` for three panels and the two-agent fallback.

## Task 7: CLI, configuration, docs

- [ ] `--claude-home` option and persisted `claudeHome` setting alongside the Grok home handling in `SystemCliAdapters` / `DefaultCliServices`.
- [ ] `status` and `doctor` report the resolved Claude home, session count, and uncertainty.
- [ ] `README.md` (English and Russian sections) gains Claude in the agent table and the intro line.
- [ ] `docs/operations.md` — a Claude sessions section and the upgrade-before-push gate; `docs/security.md` — only `projects/**/*.jsonl` leaves the machine.
- [ ] Version bump and release notes stating the gate from design D4.

## Verification

- [ ] `dotnet build` clean, no new warnings.
- [ ] `dotnet test` green across all four test projects.
- [ ] Manual: `agent-sync manage` shows three panels; copy Claude→Codex and Claude→Grok; `push` then `pull` on a second machine materializes the Claude session at the correct project path.
