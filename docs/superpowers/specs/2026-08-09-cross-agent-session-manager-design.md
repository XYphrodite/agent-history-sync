# Cross-Agent Session Manager Design

## Goal

Add a local interactive console manager, launched with `agent-sync --manage`, that lists Codex and Grok CLI sessions side by side and can copy a readable conversation into the other agent's native session format.

This feature is planned for version 0.4.0. It does not change the encrypted repository format or the behavior of existing synchronization commands.

## User interface

The manager uses Spectre.Console to render a full-screen terminal interface with two panels: Codex on the left and Grok on the right. Each panel contains `Title` and `Last modified` columns.

Keyboard controls:

- `Up` / `Down`: move the selected row within the focused panel.
- `Left` / `Right`: move focus between Codex and Grok.
- `C`: copy the selected session into the opposite agent.
- `Delete`: confirm and delete the selected session from the local filesystem only.
- `R`: rescan both local session stores.
- `Q` or `Escape`: exit the manager.

The focused panel and selected row are visually distinct. After copying or deleting, both panels are rescanned. If a list is longer than the available terminal height, selection movement scrolls the visible window.

## Local-only behavior

The manager never contacts GitHub and does not invoke synchronization. Copy and delete operations do not modify the local sync baseline, remote index, conflict evidence, or tombstones.

Deleting removes only the local native session. A later `agent-sync sync` may restore it from the encrypted repository. The confirmation dialog states this explicitly.

Active sessions are read-only. Copy or delete is refused when the selected session is active, with an instruction to close the corresponding agent and refresh the list.

## Portable conversation model

Conversion passes through an internal `PortableConversation` model containing:

- source agent and source session ID;
- title;
- working directory;
- created and last-modified timestamps;
- ordered user and assistant text messages.

Tool calls, tool results, reasoning blocks, patches, telemetry, system prompts, and other agent-specific events are omitted. Only user and assistant text enters the converted conversation; raw sensitive payloads are never copied into output or logs.

## Codex to Grok conversion

The Codex reader accepts inactive active-session or archived-session JSONL, extracts session metadata and ordered user/assistant text, and produces `PortableConversation`.

The Grok writer creates a new UUID session under the destination directory derived from the original working directory. It writes a minimal native `chat_history.jsonl` plus `summary.json`. It never reuses or overwrites the source ID or an existing Grok session directory.

## Grok to Codex conversion

The Grok reader validates the native session package, extracts metadata and ordered user/assistant text, and produces `PortableConversation`.

The Codex writer creates a new UUID and a minimal native rollout JSONL containing valid `session_meta` and user/assistant message records. It never overwrites an existing Codex session. When `codex.exe` is available through valid automatic discovery or configuration, the staged JSONL must pass the existing disposable compatibility probe before publication. When Codex is genuinely absent, the conversion can still be written for later discovery, consistent with the existing Grok-only behavior.

## Atomicity and safety

Every copy is written under an owned temporary sibling path, fully flushed and validated, and then atomically moved into its final location. A failed validation or publish removes only the owned temporary data and leaves source and destination sessions unchanged.

All paths use the existing normalization, containment, reparse-point, and ownership protections. Malformed or unsupported sessions remain visible but unavailable for copy/delete where their identity or target cannot be established safely. Errors are shown inside the manager without terminating the TUI.

Deletion requires confirmation and revalidates the selected path and active status immediately before removal. It removes exactly one Codex JSONL file or one Grok session directory. It does not delete parent directories or unrelated session artifacts.

## Components

- `SessionManagerApplication`: coordinates scanning, selection, commands, confirmations, refresh, and error presentation.
- `SessionManagerView`: Spectre.Console rendering and key-to-command mapping; contains no filesystem conversion logic.
- `PortableConversation`: agent-neutral metadata and ordered text turns.
- `CodexConversationReader` / `CodexConversationWriter`: Codex-native parsing and safe publication.
- `GrokConversationReader` / `GrokConversationWriter`: Grok-native parsing and safe publication.
- `LocalSessionCatalog`: merges native scanner results with display metadata and active/read-only state.

These components use explicit interfaces so conversion and command handling can be tested without an interactive terminal.

## Error handling and privacy

- One failed operation leaves the manager open and the previous selection stable where possible.
- User-facing errors use fixed safe messages and identifiers; conversation text, paths, URLs, credentials, and exception messages are not logged.
- Duplicate destination IDs are retried with a new generated ID; existing sessions are never overwritten.
- Terminal resize and empty lists are handled without invalid selection indexes.

## Verification

Automated coverage includes:

- Codex-to-portable and Grok-to-portable parsing fixtures;
- portable-to-Grok and portable-to-Codex native output fixtures;
- preserved title, working directory, timestamps, message order, roles, and text;
- guaranteed new IDs and refusal to overwrite existing destinations;
- staged-output validation and cleanup on failure;
- disposable Codex compatibility-probe pass/fail behavior;
- local-only deletion with no sync-state or tombstone mutation;
- active-session refusal and last-moment revalidation;
- malformed-session handling and safe error output;
- focus, selection, scrolling, empty-list, refresh, copy, delete, and exit key behavior;
- full existing solution regression suite.
