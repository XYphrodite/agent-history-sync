# Fast Metadata-Only Session Catalog Design

## Goal

Make `agent-sync --manage` reach its first complete frame quickly even with large Codex and Grok histories. Catalog construction must not normalize or hash entire conversations. Codex and Grok discovery run concurrently, while copy and delete retain their existing strict final validation.

## Problem

`LocalSessionCatalog` currently reuses `SessionScanner` and `GrokSessionScanner`. Those scanners are designed for synchronization: they observe stability, read complete native histories, normalize them, derive logical IDs, and compute content hashes. The catalog then enumerates the same stores again and reads bounded metadata for display.

On the current machine this causes the manager to process roughly 1.58 GB of Codex JSONL and 155 MB of Grok JSONL before rendering. None of the normalized bytes or hashes is displayed, and local operations already perform fresh strict validation at their final action boundaries.

## Architecture

`LocalSessionCatalog.ScanAsync` starts Codex and Grok catalog scans together and awaits both before returning one complete `SessionCatalogSnapshot`. The UI continues to render only complete snapshots; it does not progressively insert rows.

The catalog no longer invokes `SessionScanner` or `GrokSessionScanner`. Those types and their stability, normalization, and hashing behavior remain unchanged for synchronization.

Each native store is enumerated once per refresh. Per-candidate metadata work uses bounded concurrency, with a default maximum of eight simultaneous I/O operations. The implementation must not create an unbounded task for every session.

### Codex

The Codex branch:

1. Reads at most the existing 64 KiB tail budget from `session_index.jsonl` once and preserves the current last-entry-wins title semantics.
2. Enumerates allowed active and archived JSONL candidates once, retaining the existing containment, excluded-directory, inaccessible-path, and reparse-point rules.
3. Reads at most the first 64 KiB of each candidate once to extract its safe session ID, fallback title, working directory, and timestamps.
4. Uses the official index title before native metadata and user-message fallbacks.
5. Groups safely identified candidates by session ID. A duplicate ID never silently selects one path; every conflicting row is non-actionable.

### Grok

The Grok branch:

1. Enumerates native UUID session directories once under the configured sessions root, retaining containment and reparse-point rules.
2. Reads `summary.json` within the existing 64 KiB metadata budget.
3. Uses the current official-title priority: `generated_title`, string `session_summary`, `info.title`, root `title`.
4. Reads at most the first 64 KiB of `chat_history.jsonl` only when an official title is absent and a user-message fallback is required.
5. Uses the UUID directory name as the session ID and marks duplicate IDs at different native paths non-actionable.

## Activity, readability, and errors

Codex and Grok activity checks run once per available agent and can execute concurrently with independent catalog work. A non-cancellation activity-query failure remains fail-closed and marks that agent active. Requested cancellation propagates through both scan branches.

Catalog readability means that a row has a safely identified native target and enough bounded native structure for an attempted operation. It no longer means that the entire conversation was normalized and hashed during listing.

When a safe ID and target are known but bounded metadata is malformed, incomplete, duplicated, or inaccessible, the row remains visible and is marked unreadable. A failure isolated to one candidate does not fail the whole catalog. Unsafe identities and unsafe targets are omitted. Ordering, official titles, last-modified fallback, active markers, unreadable markers, selection behavior, and safe user-facing errors remain unchanged.

## Action boundary

Copy and delete behavior does not become optimistic. `LocalSessionOperations` and the native readers, writers, path policies, active checks, exact-tree checks, content hashes, and final pre-action revalidation remain unchanged. Full conversation parsing and hashing happen only after the user selects an operation and immediately before the relevant action boundary.

The synchronization engine continues using the full native scanners and their stability windows. This change must not introduce a fast or weakened mode into synchronization code.

## Refresh and caching

The initial display and `R` refresh use the same metadata-only path. No persistent catalog cache is introduced. Each refresh observes the current filesystem and produces a new immutable snapshot.

## Testing

Automated tests must prove:

- Codex and Grok scan branches overlap rather than run sequentially;
- manager catalog construction does not invoke either full synchronization scanner;
- candidate reads honor the 64 KiB bound and do not depend on valid content beyond that bound;
- each native candidate set is enumerated once per refresh;
- concurrency is bounded;
- title priorities, technical-wrapper filtering, timestamps, ordering, active state, unreadable state, and ID fallback remain correct;
- malformed, incomplete, inaccessible, reparse-point, and duplicate candidates remain safe;
- ordinary activity-query failures fail closed and requested cancellation propagates;
- copy and delete retain fresh strict final validation.

The full solution suite must pass. A non-brittle local measurement against the real Codex and Grok homes records time to the completed snapshot and verifies that manager startup no longer reads or hashes complete history payloads. Wall-clock timing is diagnostic evidence rather than a unit-test threshold.

## Success criteria

- The first UI frame is still a complete two-column snapshot.
- Codex and Grok discovery run concurrently.
- No full history normalization or SHA-256 occurs during catalog construction.
- Catalog I/O is bounded to the title index and at most 64 KiB of required metadata per candidate file.
- Full synchronization semantics and operation-time safety checks are unchanged.
