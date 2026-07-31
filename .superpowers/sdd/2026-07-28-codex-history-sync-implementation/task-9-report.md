# Task 9 Report: Windows Process Detection, Notifications, and Logon Agent

## Status

Implementation is complete and locally verified. Independent review is still required; no independent approval is claimed.

## Scope

- Added configurable Windows process detection for the configured Codex executable plus the documented `codex`, `Codex`, and `ChatGPT` first-party fallback names. Configured executable comparisons use canonical absolute paths, readable same-name binaries at other paths are rejected, trusted fallback roots are configurable, and access-denied inspection fails closed as active.
- `WaitForExitAsync` waits for every matching process, uses process exit events when handles are available, polls conservatively when inspection is denied, and re-enumerates until all candidates are gone.
- Added an exact per-user Task Scheduler registration named `CodexHistorySync`. The Task Scheduler COM API installs one current-user logon trigger and one absolute executable action with arguments `agent run`, at limited privilege and with duplicate instances ignored. XML inspection avoids localized output; install, uninstall, and doctor refuse foreign or inexact same-name definitions.
- Added the automatic worker state machine. A stopped logon runs bidirectional synchronization. Active Codex runs only `SyncMode.Push` followed by authenticated read-only status preview. An exit event begins a two-second continuously rechecked quiet window; a restart resets the window before bidirectional synchronization.
- Added one-per-epoch pending-restart and unresolved-conflict notifications, a repeated-failure notification, and a recovery notification. Automatic failures back off from 30 seconds exponentially to a 30-minute cap. Cancellation is a normal shutdown and starts no later cycle.
- Added structured JSON-line logs under `%LOCALAPPDATA%\CodexHistorySync\logs`. The logging API accepts only operation IDs, enum values, object counts, elapsed milliseconds, sanitized revision tokens, and fixed error codes. Logs rotate at 10 MiB using bounded atomic renames and retain the current file plus four rotations.
- Wired `codex-sync agent run`, `agent install`, and `agent uninstall`; Ctrl+C supplies cancellation. Default composition shares the Task 7/8 sync services and process detector, preserving the OS-wide repository lock, authenticated preview, CAS, staging, rollback, backup, and guarded-import invariants.
- Replaced the doctor marker-file placeholder with an exact Task Scheduler ownership query. Doctor continues to print only named PASS/FAIL results.
- Documented agent lifecycle, process fallback configuration, ownership checks, backoff, notifications, and redacted log retention in `docs/operations.md`.

## TDD evidence

The first focused run failed compilation for the absent process catalog, scheduler ownership, worker, clock, notifier, logger, and CLI-agent contracts. The initial implementation turned that batch green at 23/23.

The doctor scheduler seam then failed compilation because the six-argument runtime constructor did not exist; after injection and continuous quiet-window polling, the worker set passed 11/11. A self-review regression for unresolved conflict notifications failed with `expected 1, actual 0` after the notification block was deliberately absent, then passed after the one-per-epoch behavior was implemented. Task Scheduler XML ownership tests first failed compilation because the parser contract was absent, then passed 2/2 after exact action and explicit principal/logon-user validation.

Recovery self-review found that the no-configured-executable branch accepted known names without consulting configured trusted roots. A focused regression failed with `expected false, actual true`; the detector now requires a readable candidate to be inside a trusted root whenever roots are configured, while access-denied inspection remains fail-closed as active.

Final focused command:

```powershell
dotnet test --filter "CodexProcessDetectorTests|AgentSchedulerTests|AgentWorkerTests" --no-restore --verbosity minimal
```

Result: 29 passed, 0 failed, 0 skipped — Windows 17 and Integration 12.

## Full verification

```powershell
dotnet build CodexHistorySync.sln --no-restore --verbosity minimal
$env:PSExecutionPolicyPreference='Bypass'
dotnet test CodexHistorySync.sln --no-restore --verbosity minimal
```

Result: build 0 warnings/errors; full elevated suite 268 passed, 0 failed, 0 skipped — Core 107, Integration 124, Git 14, Windows 23.

The ordinary sandbox run produced the seven previously documented environment-sensitive failures: four symbolic-link tests could not create links and three DPAPI ACL tests were denied by the sandbox token. One Git fixture also returned exit 1 because PowerShell execution policy blocked its test script; its isolated run passed with the repository's documented `PSExecutionPolicyPreference=Bypass`. No production behavior or tests were weakened.

## Self-review and remaining validation

Requirement-by-requirement self-review confirmed active cycles call only push plus authenticated read-only preview, process exit waits cover all current candidates, quiet-window re-enumeration precedes imports, task deletion is fail-closed on inexact ownership, and diagnostic surfaces cannot accept exception messages, paths, URLs, credentials, keys, or plaintext.

Automated tests do not create or delete a real user Task Scheduler entry and do not display a real desktop notification, because those actions would mutate the developer's Windows session. The Task Scheduler COM shape/XML parser and notifier inputs are covered through deterministic seams. Independent review remains pending.
