# Desktop session viewer

## Agreed behavior

- `agent-sync --sessions` opens an application window, initially on Windows, using a UI stack that also supports Linux and macOS.
- Keep the terminal viewer source and its tests, but do not expose a second TUI command.
- Codex subagents are hidden until the user enables them for the selected parent conversation. Show children as a nested tree, including nested workers.
- Read user/assistant messages, tool calls, tool results, and completion notifications. Render technical entries collapsed. Do not run transcript commands.
- Search the selected transcript or its whole family, including tool input/output, with navigation to the source entry.
- Export either the selected conversation to Markdown or the parent and its descendants to a new folder of Markdown files with an index.
- Preserve the existing top-level agents, annotations, refresh and explicit local deletion workflows.

## Implementation

Use Avalonia in a `CodexHistorySync.Desktop` library referenced by the existing CLI, preserving single-file publishing. Initialize the desktop lifetime on the entry thread. Core owns trace reading, family discovery, searching, and exporting; desktop owns presentation. The old portable conversation readers and the synchronization scanner keep their existing filtering behavior.

Subagent membership comes from `session_meta.payload.parent_thread_id` or `source.subagent.thread_spawn.parent_thread_id`, with an authoritative subagent marker. IDs, titles, notifications, UUID shapes, or paths alone do not establish membership. Scan bounded metadata in active and archived JSONL roots, reject duplicate identities and out-of-root/reparse targets, and guard cycles. Missing local children cannot be reconstructed from a completion notification.

Load catalogs and transcripts off the UI thread, cancel stale selection/search operations, and virtualize the transcript. Use a bounded initial file snapshot when reading active sessions. Export to new destinations without overwriting prior exports, and make incomplete/missing records visible. Export includes tool input/output but excludes hidden reasoning and system instructions.

The viewer is local. Existing synchronization exclusions remain in place. Cross-platform UI support does not imply that Windows DPAPI or scheduled synchronization now works on other operating systems.

## Validation

Synthetic tests cover exact parentage, nested/archived children, malformed and duplicated metadata, cycles, tool pairing/ordering, incomplete trailing records, cancellation, search offsets, export links and escaping. Run existing Core and CLI viewer tests, desktop headless interaction tests, and a Windows published-window smoke check. Real history is only read for an explicit local verification; it is never copied into test fixtures.
