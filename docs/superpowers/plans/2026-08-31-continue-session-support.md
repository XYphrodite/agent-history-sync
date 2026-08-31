# Continue Session Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Continue as a fourth first-class agent alongside Codex, Grok CLI, and Claude Code, symmetric across sync, the local session manager and viewer, and cross-agent copy.

**Design:** [2026-08-31-continue-session-support-design.md](../specs/2026-08-31-continue-session-support-design.md)

**Architecture:** Continue stores one JSON object per session in `%USERPROFILE%\.continue\sessions\<uuid>.json` plus a shared index `sessions.json`. Model it as a sync package carrying both (`ContinueSessionPackage`), a catalog source, and a reader/writer pair. The shared index is the one genuinely new mechanism: every import merges into it rather than replacing it.

**Tech Stack:** .NET 10, xUnit, Spectre.Console, Windows Task Scheduler.

## Global Constraints

- Synchronize **only** `~/.continue/sessions/*.json` and `sessions.json`. Never `config.yaml`, `config.ts`, `dev_data/`, `index/`, `types/`, or `package.json`.
- Keep the `CodexHistorySync` namespaces, the `%LOCALAPPDATA%\CodexHistorySync` store, and the encryption envelope unchanged.
- Preserve existing security invariants: reparse-point rejection, canonical containment, owned staging directories, two-observation stability reads, publish-time hash re-verification.
- Never replace `sessions.json`; merge into it atomically, after a backup, and refuse when it does not parse as an array (C5).
- Every new failure message must be registered in the reason sets in `LocalSessionOperations` (`ActiveCopyFailures`, `ChangedCopyFailures`, `DestinationCopyFailures`, `IncompatibleCopyFailures`, `UnreadableCopyFailures`) or it degrades to `Unspecified`.
- `ObjectKind.ContinueSession` is appended last, never renumbered (C3).

---

## Task 1: Continue paths and package format

- [x] `src/CodexHistorySync.Core/Continue/ContinuePaths.cs` — `sealed record ContinuePaths(string Home, string Sessions)` with `TryResolve(string? configuredHome = null)`: configured home, then `CONTINUE_GLOBAL_DIR`, then `%USERPROFILE%\.continue`; require `sessions/` to exist; return `null` otherwise. Mirror `ClaudePaths.TryResolve`, including the exception filter.
- [x] `SessionFilePath(string sessionId)` and `IndexFilePath` validating components via `PathSafety.ValidateFileComponent`; reject `sessions.json` as a session id.
- [x] Index entries stay `JsonObject` rather than getting a typed record: the merge has to write back exactly what it read, and a typed shape would quietly normalise `dateCreated`, drop members, and change the bytes of an untouched file. `ContinueSessionIndex` owns parsing, lookup, merge, removal, and serialization.
- [x] `src/CodexHistorySync.Core/Continue/ContinueSessionPackage.cs` — `SchemaVersion = 1`, `LogicalIdPrefix = "co-"`, UUID-only session ids.
  - `BuildFromFile(ContinuePaths, string sessionId)` → DTO `{ v, id, entry, session }`; `session` newline-normalized to `\n`.
  - `Parse`, `HashPackage`, `ToLogicalId`, `SessionIdFromLogicalId`, `IsContinueLogicalId`.
  - Require the `sessionId` inside the session JSON to match the file name; reject on mismatch.
  - Synthesize the entry from the session file when the index has none, dating it from the file write time (C2).
- [x] Tests `tests/CodexHistorySync.Core.Tests/Continue/ContinueSessionPackageTests.cs`: round-trip, id/file-name mismatch, missing index entry, CRLF normalization, non-UUID name, `sessions.json` rejected as a session.

## Task 2: Sync scanner

- [x] `src/CodexHistorySync.Core/Continue/ContinueSessionScanner.cs` modelled on `ClaudeSessionScanner`: enumerate `sessions/*.json` one level deep with `AttributesToSkip = ReparsePoint`, skip `sessions.json`, two-observation stability, `MaxDegreeOfParallelism = Clamp(ProcessorCount, 2, 8)`.
- [x] Liveness per C4: write recency alone, `DefaultActivityWindow = 30 s`, no process probe.
- [x] Read the index once per scan and hand each candidate its own entry; a missing entry is synthesized, not an error.
- [x] Tests `ContinueSessionScannerTests`: stable file, file mutating between observations, recent file deferred, `sessions.json` never scanned as a session, missing sessions root, unreadable file, session with no index entry.

## Task 3: Sync engine, writer, index merge, backups

- [x] `ObjectKind.ContinueSession` appended; `SyncEngine` scans Continue alongside the others, resolves destinations, and stages `.continuepkg`.
- [x] `src/CodexHistorySync.Core/Continue/ContinueSessionIndex.cs` — pure merge: parse an array, replace by `sessionId` in place or append, leave every other entry untouched, serialize as JSON.stringify does (two spaces, LF, no trailing newline, no escaping of non-ASCII). Replacing rather than field-merging the session's own entry is a correction to C5 made during implementation: a preserved local field would make two machines hash the same session differently and republish it forever.
- [x] `CodexHistoryWriter.ImportContinuePackageAsync` — back up the session file and the index, write the session atomically, then merge the index atomically. Refuse when the index does not parse as an array.
- [x] `BackupStore` accepts the Continue home as a root; `PathSafety` accepts Continue destinations.
- [x] Tests: import into an empty home, import beside entries the target already had, import replacing its own entry and rehashing to the authenticated object, refusal on a malformed index leaving both files untouched, the index backed up before it changes.

## Task 4: Conversation reader and writer

- [x] `ConversationAgent.Continue`; `src/CodexHistorySync.Core/Conversion/ContinueConversationReader.cs` per C7: join `user` text parts, take `assistant` strings, drop `thinking`, empty content, and bookkeeping members; title from the session's `title`.
- [x] `ContinueConversationWriter.cs` — alternating entries in Continue's own role shapes, `mode: "agent"`, index entry appended. `workspaceDirectory` is encoded back into a `file:///c%3A/…` URI when the conversation carries a directory, and is an empty string only when it does not.
- [x] `ConversationTechnicalText` needed no new entries: Continue keeps context in `contextItems` and `editorState` rather than prepending wrapper text to the user turn, and the reader drops those members outright.
- [x] Tests: read the real observed shape, drop `thinking` and empty assistant placeholders, take the creation time from the index, decode the workspace URI, round-trip through the writer, refuse a session whose history is missing, empty, or not an array.

## Task 5: Session catalog and operations

- [x] `ManagedAgent.Continue`; `SessionCatalogSnapshot.For` and `ManagedAgents.All`/`Destinations` extended.
- [x] `src/CodexHistorySync.Core/Management/ContinueSessionCatalogSource.cs` — title from the index when present, else from the session file; `LastModifiedAt` from the file write time; bounded read.
- [x] `LocalSessionOperations` gains Continue as source and destination; delete removes the session file **and** its index entry.
- [x] Tests: listing, title fallback, copy in both directions, delete removing the entry.

## Task 6: Session manager and viewer

- [x] Fourth panel in `--manage`; the viewer list gains Continue rows through `ConfiguredAgents`, which it already iterated.
- [x] Decide the layout for four panels on an 80-column terminal before writing the view.
- [x] Tests in `SessionManagerStateTests` (fourth panel, focus, selection) and `SpectreSessionManagerViewTests` (band arithmetic across widths, four panels drawn with titles still readable at 80 columns). The viewer state needed none of its own: it reads the same `ConfiguredAgents` the manager does, and `ContinueManagementTests` covers the catalog row it lists.

## Task 7: CLI, configuration, docs

- [x] `status` prints `continue-home=`, `continue-sessions=`, and `continue-uncertain=`; `doctor` gains a `continue-paths` check; the sync runtime takes a `continueHome` seam.
- [x] No `--continue-home` flag: Claude has none either, and the user-facing override is `CONTINUE_GLOBAL_DIR`, which the extension itself reads. A flag for one agent only would be asymmetric.
- [x] README (both languages) and `docs/operations.md`: a Continue section, the scope table, and the upgrade gate note extended to Continue.
- [x] `docs/compatibility.md` left alone: it documents the Codex compatibility probe and never enumerates object kinds, so the gate wording lives in `operations.md` where the Claude gate already was.

## Verification

- [x] Full suite green, `TreatWarningsAsErrors` clean.
- [x] Read-only acceptance pass against the real `~/.continue` on this machine: index rewrite byte-identical, the session packages and reparses, the catalog lists it readable, and the reader yields 12 turns from 14 history entries with `cwd=c:ReposReborn` decoded from the URI.
- [x] A copy from Continue to Claude and back, verified by reading the written session with the destination agent's own reader (`ContinueManagementTests`). Not exercised through the interactive viewer, which needs a terminal.
- [x] `sessions.json` byte-identical after an import that changes nothing.
