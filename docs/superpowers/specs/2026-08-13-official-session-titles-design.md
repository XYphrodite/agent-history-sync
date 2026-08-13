# Official Session Titles Design

Date: 2026-08-13

## Problem

The manager currently derives many titles from the first user-shaped history record. Real histories begin with injected context such as `<environment_context>`, `<recommended_plugins>`, `<user_info>`, or `<system-reminder>`, so the table displays transport/context text instead of the names shown by Codex and Grok. UUID fallback rows also appear even though Grok `summary.json` contains a generated title.

## Source priority

### Codex

For each catalog scan, read `%CODEX_HOME%\session_index.jsonl` once and build an `id -> thread_name` map. The displayed title priority is:

1. `session_index.jsonl.thread_name` for the matching session ID;
2. `session_meta.payload.title`;
3. `session_meta.payload.thread_name`;
4. the first content-bearing, non-technical user message;
5. the session ID.

Later duplicate index entries for the same ID replace earlier ones, matching the append-oriented index format.

### Grok

The displayed title priority is:

1. `summary.json.generated_title`;
2. `summary.json.session_summary` when it is a string;
3. `summary.json.info.title`;
4. root `summary.json.title`;
5. the first content-bearing, non-technical user message;
6. the session ID.

## Technical-message filtering

Fallback history extraction skips a normalized message when its first non-whitespace content is a technical XML-like wrapper. The recognized opening tags are:

- `<environment_context>`
- `<recommended_plugins>`
- `<user_info>`
- `<system-reminder>`
- `<permissions instructions>`
- `<skills_instructions>`
- `<apps_instructions>`
- `<plugins_instructions>`

Matching is case-insensitive. A normal user request containing angle brackets later in its text is not filtered. The reader continues to the next user message instead of immediately falling back to the ID.

## Bounded and safe reading

- `session_index.jsonl` is untrusted local input and must not be loaded without a bound.
- Read at most the existing metadata byte budget from the tail of the index, because the newest append entries are authoritative. Discard an initial partial line when the bounded tail starts inside a record.
- Parse at most the existing metadata-record budget and ignore malformed individual index lines rather than failing the whole catalog.
- Accept only safe session IDs and string `thread_name` values.
- Existing per-session bounded reads, cancellation propagation, reparse/path protections, Unicode whitespace normalization, 80-character title bound, and safe Spectre `Text` rendering remain unchanged.
- No session files or native indexes are modified.

## Timestamp behavior

Title-source changes do not alter sorting or modification timestamps. Existing timestamp and filesystem fallback logic stays intact, including current unreadable-session behavior.

## Testing

Add fixture-backed regressions for:

- Codex index `thread_name` overriding technical first-user content;
- a later duplicate Codex index entry winning;
- bounded-tail index parsing and malformed-line tolerance;
- Grok `generated_title`, then string `session_summary`, overriding technical history;
- fallback scanning past each supported technical wrapper to the first real request;
- ordinary user text containing a later `<tag>` remaining visible;
- absent official/fallback titles still using the session ID;
- existing catalog, manager, and complete Release suites remaining green.

## Delivery

Implement from current `main` in an isolated worktree with strict RED-GREEN TDD, task-scoped review, whole-branch review, merged-result verification, single-file publish smoke, and push to `origin/main` only after all merge gates pass.
