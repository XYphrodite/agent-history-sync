# Session Viewer Design

**Status:** proposed
**Date:** 2026-08-25

## Problem

`agent-sync` can list sessions, copy them between agents, and delete them, but it
cannot show what is *inside* one. Deciding whether a session is worth copying or
safe to delete currently means opening the native file by hand — a Codex rollout,
a Grok package directory, or a multi-megabyte Claude transcript, each in its own
format.

## Scope

A new command, `agent-sync --sessions`, opens a read-oriented console screen with
one combined list of every session from every configured agent and the selected
session's conversation rendered beside it.

In scope: scrolling, in-session text search, export to a file, and deletion.
Copying stays in `--manage`; see D6.

## Decisions

### D1 — One list across agents, not three panels

`--manage` answers "move this session somewhere", so it shows the agents side by
side. `--sessions` answers "what is in my history", where the agent is an attribute
of a row rather than the axis of the layout. The screen is therefore a single list
sorted newest-first with an `AGENT` column, and the reading pane takes the space the
other two panels used to occupy.

The list comes from the existing `LocalSessionCatalog`, so both screens agree on
titles, timestamps, active state, and readability without a second scanner.

### D2 — Content is the portable conversation, not the native file

Each agent already has an `IConversationReader` that turns its native format into
`PortableConversation` — ordered user/assistant text with the technical wrappers
removed. The viewer renders that.

This buys one rendering path for three formats and reuses the filtering that the
copy path already depends on. It also means the viewer deliberately does **not**
show `thinking` blocks, tool calls, or tool results: they are absent from the
portable model. A raw view would need a per-agent renderer and is out of scope.

### D3 — Sessions are read on demand, never during the frame

A Claude transcript on this machine is 3.3 MB and a Grok package is larger still.
Reading on every keypress would stall the UI, and reading everything up front would
stall startup.

The viewer therefore reads the selected session asynchronously, keeps the result in
a small most-recently-used cache, and renders a `loading…` placeholder until the
read lands. A failed read is a row-level state (`unreadable`), not a crash: the list
stays usable and the pane explains itself.

### D4 — Rendering is line-based, so scrolling is O(viewport)

The reader returns turns, not lines. The viewer flattens a conversation once into a
list of display lines — a role header per turn, then its text wrapped to the pane
width — and caches that with the conversation. Scrolling and search then work on an
indexed list rather than re-wrapping text on every frame. Re-wrapping happens only
when the terminal width changes.

### D5 — Search is a filter over the rendered lines

`/` searches the rendered lines of the **open** session, case-insensitive, and moves
the viewport to each match in turn. It does not search across sessions: the list
already has its own title filter in `--manage`, and a full-text search over every
session is a different feature with different cost.

### D6 — Delete is allowed, copy is not

Deletion answers a question the viewer creates: you read a session, decide it is
junk, and remove it. It reuses `LocalSessionOperations.DeleteAsync` unchanged, so it
inherits the existing guards — active sessions and unreadable sessions are refused,
the two-observation stability check still runs, and the confirmation still says that
sync may restore the file.

Copying is deliberately absent. It is the reason `--manage` exists, it needs the
destination prompt that screen already has, and duplicating it here would mean two
places to keep correct.

### D7 — Export writes what is on screen

`E` writes the rendered conversation to a Markdown file: a title heading, the agent
and session id, then each turn under a `**User**` / `**Assistant**` heading. The
destination is `%USERPROFILE%\Documents\agent-sync\<agent>-<session-id>.md`, created
if missing, and the viewer reports the full path.

Markdown rather than the native format because the export is for reading elsewhere;
anyone who wants the native bytes already knows the path, which the viewer shows.

## Layer impact

| Layer | Change |
|---|---|
| Core | `SessionContentReader` — maps `ManagedAgent` to its reader and returns a `PortableConversation` |
| Core | `ConversationDocument` — flattens a conversation into wrapped display lines |
| Core | `SessionExporter` — renders a conversation to Markdown and writes it safely |
| Cli | `SessionViewerState`, `SessionViewerCommand`, `ISessionViewerView`, `SpectreSessionViewerView`, `SessionViewerApplication` |
| Cli | `--sessions` dispatch, usage and help text |

## Non-goals

- Showing `thinking`, tool calls, or tool results (D2).
- Searching across sessions (D5).
- Copying between agents from this screen (D6).
- Editing a session in any way.
