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

Record `type` values observed, with counts and byte shares from a 0.86 MB session:

| `type` | Records | Bytes | Carries `cwd` | Role |
|---|---|---|---|---|
| `user` | 96 | 445 KB | yes | conversation |
| `assistant` | 168 | 365 KB | yes | conversation |
| `attachment` | 80 | 44 KB | yes | editor and tool context |
| `last-prompt` | 21 | 4 KB | no | most recent user prompt |
| `ai-title` | 23 | 2 KB | no | official session title |
| `queue-operation` | 14 | 2 KB | no | prompt queue bookkeeping |
| `atis-latch` | 22 | 2 KB | no | internal latch |
| `file-history-snapshot` | 7 | 2 KB | no | tracked-file backups |

Content block `type` values observed: `text`, `thinking`, `tool_use`, `tool_result`.

No `summary` record was observed in any live session; Claude records its title as
`ai-title` instead (D7). Only `user`, `assistant`, and `attachment` records carry
`cwd`, which is what D1 reads.

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

The **forward** direction is well defined and is used: a conversation copied out of
another agent has a cwd but no Claude directory yet, so `ClaudePaths.EncodeProjectSegment`
builds one by collapsing `:`, `\` and `/` to `-`. Only the reverse is forbidden. An
exotic path that Claude would mangle differently costs grouping in Claude's own
project list, not correctness: the session's real cwd lives in its records.

### D2 — Logical id namespace `cl-`

Codex logical ids are the bare session id (`SessionScanner.IsSafeLogicalId`,
[SessionScanner.cs:261](../../../src/CodexHistorySync.Core/Codex/SessionScanner.cs#L261));
Grok prefixes `g-`. Claude uses `cl-<lowercase-uuid>`, keeping the three
namespaces disjoint inside a single repository index.

### D3 — Active-session detection without an active-session file

Grok exposes `~/.grok/active_sessions.json`; Claude exposes nothing equivalent.
A Claude session counts as live when a `claude` process is running **and** the
JSONL was written inside an activity window. Such sessions are deferred exactly
like a locked Grok session — published on a later run rather than failing the run.

The activity window is **30 s**, a separate constant from the 50 ms stability
delay. Reusing the stability delay would make the test almost never fire, and
widening the stability delay instead would slow every scan. 30 s also lets an
idle-but-open session stop deferring and finally sync, which a
process-running-only rule never would.

The process list is enumerated once per scan, not once per file. If it cannot be
read, the probe fails closed and reports "running", deferring recent transcripts
rather than publishing a file a live session may still be appending to.

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

### D7 — The official title is the last `ai-title` record

Claude writes `{"type":"ai-title","aiTitle":"…","sessionId":"…"}` and rewrites it
as the conversation develops — 23 such records in the session measured above. The
official title is therefore the **last** one in file order, not the first.

`summary` is kept as a second-position fallback because the earlier survey named
it, but no live session contained one; the first non-technical user turn remains
the last resort, as for Codex and Grok.

Title extraction starts in the bounded prefix read of the catalog source
(`64 KiB`, 64 records). `ai-title` records are small and appear throughout the
file, so on a long session the newest one lands past the prefix. Measured on a
668 KiB session, the first `ai-title` sat at byte 96 446 and the catalog fell
through to the opening turn — which, under an editor integration, is a hidden
primer the user never wrote.

A transcript the prefix did not cover therefore gets a second bounded read of
the trailing `64 KiB`, and the newest `ai-title` in that tail outranks anything
the prefix found: a rename is appended, so the tail carries the current name.
The extra read is skipped whenever the prefix already reached EOF, and a tail
read that fails leaves the prefix title standing rather than demoting the
session to unreadable.

Editor integrations wrap **every** turn in `<user_query>`, so the tag cannot
itself mark a turn technical. It is stripped before the technical-preview check
and before a preview can become a title; the hidden primer turn is matched on
its own opening text instead. The last-resort title is then a turn the user
actually wrote.

### D8 — One session id, two project folders: the newest write is the session

Discovered in production, after D2 shipped. Change directory mid-session and
Claude copies the transcript into the project folder for the new directory and
continues it there; the copy in the old folder freezes at the moment of the
move. A real example on the author's machine:

```
projects/c--Repos-Reborn/05e49a17-....jsonl        950 records, ends 05:55
projects/c--Repos-xmrig-fleet/05e49a17-....jsonl  3610 records, still growing
```

Both carry the same `sessionId`, both open with the identical `bridge-session`
record, and the second contains records with **both** `cwd` values.

D2 makes the logical id the session id alone, so the two files produced one id
twice. The scanner reported that as a duplicate, and a duplicate id is fatal for
the whole scan — `SyncEngine.EnsureScanUsable` throws
`Local history contains duplicate logical object IDs.`, which stopped Codex and
Grok from synchronizing too. One ordinary Claude session disabled the entire
tool.

D2 stands: the id must stay machine-independent, and a project folder is neither
(the mangling is lossy per D1, and the same session lives under different paths
on different machines). What was wrong is treating a relocation as damage.

The rule is now: **transcripts sharing a session id collapse to the one written
most recently**; ties break on the longer transcript, then on path, so every
machine and every run chooses the same file. The frozen copy is an earlier state
of the same conversation, so ignoring it loses nothing, and it is left on disk.

Order matters against D3: the live copy is chosen **before** the activity window
is applied. Otherwise deferring a session that is being written would promote its
frozen copy in its place — publishing an older conversation over a newer one.

The same rule governs the manager and viewer catalog, which previously marked
both copies unreadable and so refused to open or copy an ordinary session.

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
