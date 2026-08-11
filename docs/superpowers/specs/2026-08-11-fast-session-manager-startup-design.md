# Fast Session Manager Startup Design

## Goal

Reduce `agent-sync --manage` startup time for large local histories without weakening the existing stability, duplicate-ID, path-containment, or readability checks. The first UI frame continues to show a complete catalog rather than a partially loaded list.

## Current Problem

The native Codex and Grok scanners wait 50 milliseconds separately for every candidate. With 981 current candidates, the waits alone serialize to about 49 seconds. Catalog construction then checks whether Codex or Grok is running once per displayed session, even though that result is agent-wide for the duration of one scan.

## Design

### Batch stability observation

Each native scanner will:

1. Enumerate and validate candidate paths as it does today.
2. Capture the first size/last-write observation for every accessible candidate.
3. Await its configured stability delay exactly once.
4. Capture a second observation and fully read, normalize, validate, and hash only candidates whose two observations match.

An inaccessible, deleted, replaced, or changed candidate remains unstable and is excluded exactly as before. Duplicate logical IDs and uncertain-kind reporting retain their existing behavior.

The scanner delay remains injectable so focused tests can deterministically prove that a file changed during the shared window is rejected.

### One active-process snapshot per catalog scan

`LocalSessionCatalog` will request the active state once for Codex and once for Grok before constructing individual rows, then reuse those two conservative results for all sessions of the corresponding agent. If an active-state check fails, that agent is treated as active, preserving the current fail-closed behavior.

The active-state interface will expose an agent-level query because the Windows implementation currently detects whether any process for that agent is running; it does not identify individual session IDs. Copy and delete operations will continue to perform fresh checks at their own action boundaries.

### UI behavior

`SessionManagerApplication` remains unchanged: it awaits one complete catalog snapshot before the first render. Refresh uses the same optimized scan path.

## Alternatives Rejected

- Setting the stability delay to zero in manager mode would improve speed but weaken protection against partially written session files.
- Rendering immediately and loading rows in the background would require new UI state, cancellation, selection-stability, and error behavior without addressing unnecessary scanner work.
- Building the catalog only from bounded metadata would avoid hashing but would change the meaning of readability and duplicate detection.

## Testing

- Scanner tests prove that many candidates use one shared stability wait.
- Scanner tests mutate one candidate during that wait and prove it is excluded while unchanged candidates remain available.
- Catalog tests prove one active-state query per available agent regardless of session count.
- Existing scanner, catalog, manager, security, and full solution suites remain green.
- A local timing check with the real session roots records the improvement without making a brittle wall-clock assertion part of the automated suite.

## Success Criteria

- The fixed 50-millisecond cost is paid once per Codex scan and once per Grok scan, not once per session.
- Process detection runs at most once per available agent during one catalog scan.
- The first rendered snapshot remains complete and follows all existing safety rules.
