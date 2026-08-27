# Session Viewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `agent-sync --sessions` opens a console screen with one combined list of every session and the selected session's conversation beside it, with scrolling, in-session search, Markdown export, and deletion.

**Design:** [2026-08-25-session-viewer-design.md](../specs/2026-08-25-session-viewer-design.md)

**Architecture:** The catalog and the per-agent `IConversationReader` implementations already exist and are reused unchanged. Core gains three small pieces — a reader facade, a line-flattening document, and an exporter. The Cli gains a second screen alongside the session manager, built from the same Spectre parts.

**Tech Stack:** .NET 10, xUnit, Spectre.Console.

## Global Constraints

- The viewer never writes to an agent home except through `LocalSessionOperations.DeleteAsync`.
- No second scanner: session rows come from `ILocalSessionCatalog` exactly as `--manage` sees them.
- No conversation read on the render path; the frame must be produced from already-loaded state.
- Export paths are validated as file-name components before they touch the filesystem, like every other path in this codebase.
- `--manage` behaviour, keys, and layout stay exactly as they are.

---

## Task 1: Reading a session's conversation

- [x] `src/CodexHistorySync.Core/Management/SessionContentReader.cs` — `ISessionContentReader` with `Task<PortableConversation> ReadAsync(ManagedSession session, CancellationToken ct)`, plus a default implementation mapping `ManagedAgent` to `CodexConversationReader` / `GrokConversationReader` / `ClaudeConversationReader`.
- [x] Refuse a session whose `CanRead` is false before touching the disk, with a message registered alongside the existing unreadable reasons.
- [x] Tests `SessionContentReaderTests`: one case per agent reading a written fixture, an unknown agent, and a `CanRead: false` refusal.

## Task 2: Flattening a conversation into display lines

- [x] `src/CodexHistorySync.Core/Conversion/ConversationDocument.cs` — `Build(PortableConversation, int width)` returning an indexed `IReadOnlyList<ConversationLine>` where each line carries its text and whether it is a role header, so the view can style it without re-parsing.
- [x] Wrap on word boundaries; never split a word narrower than the pane; preserve blank lines between turns.
- [x] `FindMatches(string query)` returning the line indexes that contain the query, case-insensitive.
- [x] Tests `ConversationDocumentTests`: wrapping at an exact boundary, a word longer than the width, role headers present, blank-line separation, match indexes, and an empty query matching nothing.

## Task 3: Viewer state

- [x] `src/CodexHistorySync.Cli/Management/SessionViewerState.cs` — the flat session list (catalog snapshot flattened and sorted newest-first), the selected row, the loaded document, the content scroll offset, the search query, and the current match.
- [x] `Focus` of either the list or the content, so the arrow keys mean one thing at a time.
- [x] Selection and scroll clamp exactly like `SessionManagerState` does; a snapshot replacement keeps the selected session by id when it survives.
- [x] Tests `SessionViewerStateTests`: ordering across agents, selection movement, focus switching, scroll clamping at both ends, search stepping and wrap-around, and snapshot replacement preserving the selection.

## Task 4: Export

- [x] `src/CodexHistorySync.Core/Management/SessionExporter.cs` — renders `PortableConversation` to Markdown and writes it to `%USERPROFILE%\Documents\agent-sync\<agent>-<session-id>.md`, creating the directory, validating the file-name component, and returning the full path.
- [x] Write through a temporary file and move into place, so an interrupted export never leaves a half-written document.
- [x] Tests `SessionExporterTests`: heading and turn layout, an unsafe session id refused, and an existing file overwritten atomically.

## Task 5: The screen

- [x] `SessionViewerCommand` — `MoveUp`, `MoveDown`, `PageUp`, `PageDown`, `Home`, `End`, `FocusList`, `FocusContent`, `Search`, `NextMatch`, `Export`, `Delete`, `Refresh`, `Exit`.
- [x] `ISessionViewerView` + `SpectreSessionViewerView` — left list with `AGENT`, `SESSION`, `UPDATED`; right pane with the conversation, the active-and-unreadable markers reused from the manager, and a footer of keys.
- [x] `SessionViewerApplication` — the loop: load on selection change, render, read a command, act. Deletion goes through `ILocalSessionOperations.DeleteAsync` behind the existing confirmation.
- [x] A session that fails to load shows why in the pane and leaves the list usable.
- [x] Tests `SessionViewerApplicationTests` and `SpectreSessionViewerViewTests`: a session rendered beside its list, the loading placeholder, an unreadable session, key mapping, export reporting its path, and a confirmed delete removing the row.

## Task 6: CLI wiring and docs

- [x] `--sessions` accepted the same way as `--manage`, with its own runner; usage and `--help` mention it.
- [x] `README.md` (English and Russian) and `docs/operations.md` describe the screen, its keys, and that it is read-only apart from deletion.
- [x] Version bump.

## Verification

- [x] `dotnet build` clean, no new warnings.
- [x] `dotnet test` green across all four test projects.
- [ ] Manual: `agent-sync --sessions` lists every agent's sessions, opens a Claude, a Codex, and a Grok session, searches inside one, exports one, and deletes a disposable one.
