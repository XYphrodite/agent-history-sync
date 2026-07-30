# Task 8 Report: Safe CLI Setup and Manual Operations

## Status

Implementation is complete and locally verified. Independent review is still required; no independent approval is claimed.

## Scope

- Added a testable `CliApplication` for `init`, `join`, `sync`, `pull`, `push`, `status`, `doctor`, `conflicts`, and `resolve`.
- Added exact exit-code handling: success 0, operational failure 1, usage 2, security/compatibility gate 3, unresolved conflicts 4.
- Added hidden interactive passphrase entry, confirmation for initialization, in-memory zeroization, stable diagnostics, safe-token rendering, and credential URL canonicalization.
- Added `DefaultCliServices` with granular setup, persistence, and runtime seams.
- Added a schema-1 public setup manifest authenticated by a domain-separated HKDF-SHA256 key and HMAC-SHA256 with constant-time verification.
- Initialization publishes the manifest and encrypted empty repository index in one remote Git commit before DPAPI/config/state persistence.
- Join reads setup metadata through `gh api`, authenticates the manifest and encrypted index before persistent local writes, runs the compatibility gate and dry-run plan, and requires `--apply` for the first pull.
- Added concrete composition over `GitStorageProvider`, `SyncEngine`, `SessionScanner`, `CodexHistoryWriter`, `BackupStore`, `ConflictStore`, `LocalStateStore`, and `DpapiKeyStore`.
- Added metadata-only status, doctor, and conflict output. Conflict export delegates boundary and reparse validation to `ConflictStore.ResolveAsync`.
- Added `docs/operations.md` with prerequisites, setup/recovery, commands, diagnostics, conflict handling, and exit codes.

## TDD evidence

The first CLI contract run failed compilation because `CodexHistorySync.Cli.CliApplication` and its contracts were absent. After the parser/orchestrator implementation, 21 CLI tests passed.

The real-service tests then failed compilation because the gateway, local repository, runtime, manifest authenticator, and `DefaultCliServices` contracts were absent. After implementation, the combined CLI suite passed. A separate URL-credential test first failed because userinfo reached the gateway/configuration, then passed after canonicalization. Last-successful-revision tests first failed for manual sync and applied join, then passed after successful-revision configuration updates were added.

Final focused command:

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests --filter "CliTests|CliServiceTests" --no-restore
```

Result: 28 passed, 0 failed, 0 skipped.

## Full verification

The ordinary sandbox run produced the seven known environment-sensitive failures: four symlink/reparse tests could not create links and three DPAPI ACL tests were denied by the sandbox token. No production behavior or tests were weakened.

The same full suite with the Windows privileges required by those existing tests passed:

```powershell
$env:PSExecutionPolicyPreference='Bypass'
dotnet test CodexHistorySync.sln --no-restore --verbosity minimal
```

Result: 217 passed, 0 failed, 0 skipped — Core 101, Integration 96, Git 14, Windows 6.

Real `doctor` execution completed and emitted all nine named checks with only PASS/FAIL status. In the uninitialized worktree, Codex paths/version, Git, process-state, and free-disk-space passed; GitHub private visibility, key access, repository schema, and agent installation failed as expected. No path, URL, credential, prompt, key, child-process output, or exception text was emitted.

## Security and Task 7 preservation

- Setup manifest authentication uses a distinct domain label and constant-time MAC comparison.
- A wrong passphrase or tampered index produces a gate failure and no key/config/state persistence.
- The remote initialization commit is the irreversible boundary; a later local-cache failure is recoverable through `join`.
- Credential-bearing input URLs are converted to canonical credential-free GitHub URLs before transport or persistence.
- CLI failures never echo raw dependency diagnostics.
- Manual operations delegate to the unchanged Task 7 engine and therefore retain its authenticated-index, CAS, staging, conflict, backup, atomic mutation, and restart-recovery invariants.

## Review status

No independent review result is claimed. The controller should review the Task 8 implementation and report commit.
