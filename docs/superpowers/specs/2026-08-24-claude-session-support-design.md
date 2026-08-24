# Claude Session Support Design

**Status:** proposed
**Date:** 2026-08-24

## Problem

`agent-sync` supports two agents — Codex and Grok CLI — symmetrically across three
layers: encrypted cross-machine sync, the local session manager, and cross-agent
conversation copy. Claude Code sessions are invisible to all three.

## Observed Claude Code layout

Verified on disk, `agent-sync` host machine:

| Item | Value |
|---|---|
| Home | `%USERPROFILE%\.claude` (override: `CLAUDE_CONFIG_DIR`) |
| Sessions | `<home>\projects\<mangled-cwd>\<session-uuid>.jsonl` |
| Unit | One flat JSONL file per session (Grok uses a directory package; Codex uses `sessions/YYYY/MM/DD/rollout-*.jsonl`) |
| Other subtrees | `backups/`, `ide/`, `session-env/`, `shell-snapshots/`, `sessions/` — out of scope |

Every record is a JSON object. Conversation records carry `sessionId`, `cwd`,
`timestamp`, `uuid`, `parentUuid`, `version`, `gitBranch`, and
`message: { role, content[] }`.

Record `type` values observed: `user`, `assistant`, `attachment`,
`queue-operation`, `summary`, `file-history-snapshot`.
Content block `type` values observed: `text`, `thinking`, `tool_use`, `tool_result`.

## Decisions

### D1 — The project directory name is lossy; never reverse it

`c:\Repos\Reborn` becomes `c--Repos-Reborn`: both `:` and `\` collapse to `-`.
The mapping is not injective, so a round-trip through the directory name would
corrupt destinations.

The package therefore carries **both** the authoritative `cwd` — read from the
JSONL records themselves — and the literal source directory segment (`project`).
Materialization writes into `<Projects>\<project>\<id>.jsonl` using the stored
segment, validated with `PathSafety.ValidateFileComponent`. This is stricter than
Grok, which reconstructs its segment with `Uri.EscapeDataString` because Grok's
encoding *is* reversible.

A session whose records contain no `cwd` is not syncable; the scanner reports it
as uncertain rather than guessing.

### D2 — Logical id namespace `cl-`

Codex logical ids are the bare session id (`SessionScanner.IsSafeLogicalId`,
[SessionScanner.cs:261](../../../src/CodexHistorySync.Core/Codex/SessionScanner.cs#L261));
Grok prefixes `g-`. Claude uses `cl-<lowercase-uuid>`, keeping the three
namespaces disjoint inside a single repository index.

### D3 — Active-session detection without an active-session file

Grok exposes `~/.grok/active_sessions.json`; Claude exposes nothing equivalent.
A Claude session counts as live when a `claude` process is running **and** the
JSONL was written inside the stability window. Such sessions are deferred exactly
like a locked Grok session — published on a later run rather than failing the run.

The existing two-observation stability read still guards the general case: the
current session's file grows between observations and is skipped.

### D4 — Upgrade gate before the first Claude push

`ObjectKind` is persisted in the encrypted index as an integer, and an undefined
value fails the **entire** index, not just the offending entry
([SyncEngine.cs:1053](../../../src/CodexHistorySync.Core/Sync/SyncEngine.cs#L1053)):

```
Repository index contains an invalid object kind.
```

So the first push carrying Claude objects breaks `pull` on any machine still
running an older `agent-sync`. Mitigations:

- `ClaudeSession` is appended as the **last** enum member, leaving existing
  integer values untouched.
- The release notes and operations doc require upgrading every machine before the
  first Claude push.

This hazard is inherent to the already-released strict reader; it cannot be fixed
retroactively for existing installs.

### D5 — Copy stops being a binary toggle

`LocalSessionOperations.CopyAsync` currently infers the destination from the
source — Codex→Grok, Grok→Codex
([LocalSessionOperations.cs:116](../../../src/CodexHistorySync.Core/Management/LocalSessionOperations.cs#L116)).
With three agents the destination becomes an explicit parameter, and the TUI
prompts when more than one destination is configured. The two-argument overload
survives for the case where exactly one other agent is available.

### D6 — Scope of synchronized data

Only `projects/**/*.jsonl`. This mirrors the Grok policy of syncing the
conversation package without terminal logs, locks, or SQLite, and keeps
machine-specific and credential-bearing state off the wire.

## Layer impact

| Layer | Change |
|---|---|
| Model | `ObjectKind.ClaudeSession` (appended) |
| Paths | `ClaudePaths`; `PathSafety.EnsureOutsideCodex` / `EnsureSessionDestination` accept it |
| Sync | `ClaudeSessionScanner`, `SyncEngine` merge + destination resolution, `.claudepkg` staging, `CodexHistoryWriter.ImportClaudePackageAsync`, `BackupStore` roots |
| Conversion | `ConversationAgent.Claude`, reader, writer, technical-wrapper list |
| Management | `ManagedAgent.Claude`, agent-indexed `SessionCatalogSnapshot`, `ClaudeSessionCatalogSource`, explicit copy target, `WindowsManagedSessionActiveState.ReadClaudeActiveIds` |
| CLI | `--claude-home` / `claudeHome`, `status`, `doctor`, three-panel TUI |

## Non-goals

- Reversing the project-directory mangling.
- Synchronizing anything outside `projects/`.
- Preserving `thinking`, `tool_use`, or `tool_result` blocks across a cross-agent
  copy: the portable model is text turns only.
