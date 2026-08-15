# Hide Subagent Sessions from the Manager

## Goal

`agent-sync --manage` must show user-facing Codex and Grok conversations, not internal worker sessions. Codex sessions explicitly identified as subagents are unconditionally excluded from the catalog. No command-line switch exposes them.

## Scope

- Change only manager discovery and display membership.
- Do not delete or modify native session files.
- Do not change synchronization, copy, delete, or native conversation parsing behavior.
- Do not infer Grok subagents from worktree paths, titles, UUIDs, or message text because the inspected Grok metadata has no authoritative subagent marker.

## Classification

A Codex session is a subagent when its `session_meta.payload` contains either:

- `thread_source` equal to `subagent`; or
- an object-valued `source.subagent` member.

The comparison for `thread_source` is case-insensitive to tolerate producer variations. Either marker is sufficient. This covers spawned workers, reviewers, nested workers, and internal guardian sessions.

`parent_thread_id` alone is not a subagent marker. A normal session carrying a parent identifier but neither authoritative marker remains visible.

Malformed or unsupported marker shapes do not cause heuristic filtering. Existing readability and error-handling rules continue to apply.

## Data Flow

`CodexSessionCatalogSource` already reads the bounded prefix containing the native `session_meta` record. During that parse it records whether the session is a subagent. If classified as a subagent, the source returns no catalog candidate for that file. Filtering occurs before title fallback, index-title projection, sorting, and manager rendering, so an index entry cannot reintroduce the row.

No additional file read, hash, normalization pass, stability delay, or relationship graph is added.

## User-visible Behavior

- Subagent UUID rows disappear from the Codex panel after refresh or restart.
- Technical subagent previews such as `The following is the Codex agent history...` disappear with their owning sessions.
- Ordinary Codex sessions retain their existing title and timestamp behavior.
- Grok catalog behavior remains unchanged until Grok exposes an authoritative subagent marker.

## Testing

Tests must prove, using real catalog parsing:

1. A spawned Codex subagent with `thread_source: subagent` is absent.
2. A guardian/nested subagent identified through `source.subagent` is absent even if the other marker is unavailable.
3. An index title cannot reintroduce a subagent.
4. A normal session with `parent_thread_id` but no subagent marker remains present.
5. Existing Codex/Grok catalog and manager tests remain green.

Implementation follows test-driven development: each required behavior is observed failing before production code changes, then focused and full solution tests are run.
