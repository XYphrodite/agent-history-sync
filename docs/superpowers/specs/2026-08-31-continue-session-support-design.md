# Continue Session Support — Design

**Status:** implemented
**Date:** 2026-08-31
**Plan:** [2026-08-31-continue-session-support.md](../plans/2026-08-31-continue-session-support.md)

## Problem

`agent-sync` knows three agents: Codex, Grok CLI, and Claude Code. Continue
(the VS Code extension, `continue.continue`) keeps chat sessions of the same
kind and is not synchronized, does not appear in the session manager or viewer,
and cannot take part in cross-agent copy.

Adding it means the same three things Claude support meant: sync through the
encrypted repository, a panel of its own in the local manager and viewer, and
both directions of cross-agent transfer.

## Observed Continue layout

Measured on this machine against `continue.continue-2.0.0-win32-x64`, and read
back from the extension's own `out/extension.js` rather than assumed.

```
%USERPROFILE%\.continue\
  sessions\
    sessions.json                 <- shared index, an array
    <uuid>.json                   <- one session, a single JSON object
  config.yaml  config.ts          <- assistant configuration, may hold API keys
  dev_data\                       <- telemetry and prompt logs
  index\                          <- embeddings, sqlite, lancedb
```

An index entry:

```json
{
  "sessionId": "9490954d-d7dd-4cbe-984c-6172d60bf3dc",
  "title": "hi",
  "dateCreated": "1788134536812",
  "workspaceDirectory": "file:///c%3A/Repos/Reborn",
  "messageCount": 7
}
```

A session file: `{ sessionId, title, workspaceDirectory, history[], mode,
chatModelTitle }`, where each `history` entry is
`{ message: { role, content }, contextItems, editorState?, appliedRules?,
promptLogs?, isGatheringContext? }`.

Roles observed: `user`, `assistant`, `thinking`. A `user` message carries
`content` as an array of `{ type: "text", text }` parts; an `assistant` message
carries `content` as a plain string. An empty `assistant` entry precedes a
`thinking` entry.

What the extension does with the store:

| Operation | Behaviour |
|---|---|
| save | writes the session file, then updates the matching index entry **in place** (keeping its original `dateCreated`) or appends a new one with `String(Date.now())`, and rewrites the index as `JSON.stringify(list, undefined, 2)` |
| list | parses the array, drops legacy `session_id` entries, **reverses** it, optionally filters on an exact lowercased `workspaceDirectory` |
| delete | removes the session file and its index entry |
| load | requires the session file to exist |
| malformed index | throws `It looks like there is a JSON formatting error in your sessions.json file … Please fix this before creating a new session.` |

## Decisions

### C1 — The unit of synchronization is a session file plus its index entry

Claude, Codex, and Grok sessions are self-contained on disk. A Continue session
is not: restore only `<uuid>.json` and the session exists but is invisible,
because the UI lists `sessions.json`. Restore only the entry and `load()`
throws.

So one object carries both. The package DTO is
`{ v, id, entry, session }` — schema version, session id, the index entry, and
the session file's text.

### C2 — The index entry travels rather than being recomputed

`dateCreated` is stamped by whichever machine first saves the session
(`String(Date.now())`). Recomputing it on import would date every transferred
session to the moment it arrived, and sessions would reorder themselves
differently on each machine. Carrying the entry keeps creation time, title,
workspace, and message count identical everywhere.

When a session file exists with no index entry — Continue tolerates this, its
`list()` simply does not show the session — the entry is synthesized from the
session file with `dateCreated` taken from the file's write time, so the
package is always complete.

### C3 — Logical id namespace `co-`

Codex uses the bare session id, Grok prefixes `g-`, Claude `cl-`. Continue uses
`co-<lowercase-uuid>`, keeping all four namespaces disjoint inside one index.

This inherits the Claude upgrade gate verbatim (see D4 in the Claude design):
`ObjectKind` is an integer in the encrypted index, and a build that does not
know `ContinueSession` rejects the **whole** index rather than one object,
which breaks `pull` for every agent on that machine. **Every machine sharing
the repository must be upgraded before the first push carrying a Continue
session.**

### C4 — Liveness has no process to probe

Claude's D3 infers a live session from a running `claude` process plus write
recency. Continue has no process of its own: it runs inside the VS Code
extension host, so the only candidate signal is `Code.exe`, which is running
whenever the editor is open and would defer everything forever.

Liveness is therefore write recency alone: a session file written within the
activity window (30 s, the same constant Claude uses) is deferred to a later
run, and the two-observation stability read still rejects a file that changes
while it is being scanned. A session being typed into publishes once it goes
quiet, exactly like an idle Claude session.

This is weaker than D3 and deliberately so — the failure it protects against
(publishing a half-written file) is already caught by the stability read; the
recency window only avoids the pointless churn of publishing a session that is
about to change again.

### C5 — The index is merged, never replaced

Import must not rewrite `sessions.json` from our own view of the world: it is a
shared file, the local machine has sessions the repository has never seen, and
a malformed result disables session creation in Continue entirely.

The merge reproduces what the extension does — replace the entry with a matching
`sessionId` in place, otherwise append — and serializes the way JavaScript does:
two-space indentation, LF, no trailing newline, and no escaping of non-ASCII,
`<`, `>`, or `&`, so an index this import does not change is written back
byte-identical.

The entry for the imported session is replaced outright rather than merged
member by member. Keeping members the incoming entry does not carry sounds
safer and is not: the object hash covers the session together with its entry,
so an entry that differs between two machines makes each see the other's copy
as changed, and one session would be republished back and forth forever. Every
*other* entry is untouched, which is the preservation that matters — most of
them describe sessions the repository has never seen.

Three refusals guard it. An index that does not parse as a JSON array is not
repaired or replaced: the import fails and says so, because overwriting it
would destroy the list of every local session. The write goes through the
existing atomic replace, so a crash cannot leave a half-written index. And the
previous index is backed up first, like any other file the writer touches.

### C6 — Scope of synchronized data

Only `sessions/*.json` and `sessions/sessions.json`.

Never `config.yaml` or `config.ts` — they are assistant configuration and can
carry API keys — and never `dev_data/` (telemetry and prompt logs), `index/`
(embeddings and sqlite), `types/`, or `package.json`.

`CONTINUE_GLOBAL_DIR` overrides the home, the same way `CLAUDE_CONFIG_DIR`,
`GROK_HOME`, and `CODEX_HOME` do; the extension reads that variable itself.

### C7 — Cross-agent conversion drops what the portable model cannot hold

Reading: a `user` message's text parts are joined; an `assistant` message's
string content is taken as is; `thinking` entries and empty content are
dropped, as are `contextItems`, `editorState`, `promptLogs`, and
`appliedRules`. The title is the session's own `title` field.

Writing: a portable conversation becomes a session file with alternating
`user`/`assistant` entries, `mode: "agent"`, and an index entry appended to the
end of the list, which is where the extension's own append puts it and
therefore where the UI shows it first.

A conversation arriving from another agent has no Continue workspace, so
`workspaceDirectory` is written as an empty string in both the session and its
entry. It stays a string, so the extension's workspace filter still compares
safely; the session simply matches no workspace.

## Layer impact

| Layer | Change |
|---|---|
| Model | `ObjectKind.ContinueSession` (appended) |
| Paths | `ContinuePaths`; `PathSafety` accepts the Continue home as a destination root |
| Sync | `ContinueSessionScanner`, `ContinueSessionPackage`, `SyncEngine` merge and destination resolution, `.continuepkg` staging, `CodexHistoryWriter.ImportContinuePackageAsync`, `ContinueSessionIndex` merge, `BackupStore` roots |
| Conversion | `ConversationAgent.Continue`, reader, writer |
| Management | `ManagedAgent.Continue`, `ContinueSessionCatalogSource`, copy target, delete support |
| CLI | `--continue-home` / `continueHome`, `status`, `doctor`, a fourth panel |

## Non-goals

- Synchronizing anything outside `sessions/`.
- Repairing a `sessions.json` that Continue itself considers broken.
- Preserving `thinking`, `contextItems`, `editorState`, or `promptLogs` across a
  cross-agent copy: the portable model is text turns only.
- Reproducing Continue's legacy `session_id` entry format, which its own
  `list()` filters out.
