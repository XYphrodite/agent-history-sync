# Task 8 Report: Safe CLI Setup and Manual Operations

## Review round 3 correction

The third independent review reported one medium compatibility issue. Existing schema-1 manifests can contain the pre-round-2 non-null fingerprint, which previously overrode recomputation and caused an equivalent conflict to be preserved a second time after upgrade.

Conflict deduplication now derives an allowed candidate set from provenance. The current per-side form is always present; the legacy single-metadata form is present only when both side metadata exactly equal the legacy metadata. A stored fingerprint is treated as untrusted and accepted only after fixed-time equality with a recomputed allowed candidate. Malformed and mismatched stored values cannot affect matching, and active/archived cross-kind conflicts cannot match through the lossy legacy form.

The upgrade regression constructs real schema-1 evidence with the old non-null fingerprint, preserves the equivalent conflict after upgrade, verifies that the original record is returned, retires it, and verifies no duplicate remains. Companion cases cover malformed/mismatched stored values and cross-kind non-conflation. Before the implementation, the focused set was 1 passed and 3 failed with new timestamped conflict IDs; after it, the set passed 4/4 and the full `ConflictStoreTests` class passed 21/21.

Final round-3 verification: build 0 warnings/errors; full elevated suite 239 passed, 0 failed, 0 skipped — Core 107, Integration 112, Git 14, Windows 6. Independent re-review remains pending.

## Review round 2 corrections

The second independent review reported five important issues. This correction set addresses all five without claiming that the next re-review has passed:

- Conflict provenance now records authenticated metadata independently for the local and remote envelopes, with a backward-compatible legacy fallback. Resolution uses the selected side's object kind throughout authentication, remote publication, guarded local mutation, baseline persistence, and final validation. Same-ID active/archived keep-local and keep-remote regressions cover both directions.
- `SyncEngine` is synchronously and asynchronously disposable. Disposal is idempotent, serializes with active operations, zeroes the engine-owned master-key copy, and rejects later operations. Every real CLI runtime engine is scoped with `await using`; a real-runtime regression verifies its internal copy is zeroed without modifying the caller's key.
- Preview exposes deterministic conflict identities. Synchronization reports the exact identity-deduplicated persisted conflict set after publication, while status reports the exact union of persisted evidence and the current plan. Evidence listing errors propagate so both operations fail closed.
- Status now exposes the current authenticated remote revision and the separately persisted last-successful revision.
- Successful conflict resolution retires evidence with an atomic same-parent directory rename before best-effort deletion. Rename failure leaves live evidence and fails the operation; deletion failure leaves a hidden retired artifact that is excluded from listings and safely cleaned after restart. Retirement rejects unexpected contents and reparse paths.

TDD regressions were observed RED before their production changes: cross-kind resolution failed local provenance validation; synchronization undercounted a persisted-plus-new conflict union; the runtime disposal seam and six-field status contract did not compile; status undercounted planned-plus-persisted identities; and retirement APIs did not compile. The corresponding focused correction set passed 10/10 after implementation. Atomic retirement tests separately passed 2/2.

Verification after the round-2 correction set:

```powershell
dotnet build CodexHistorySync.sln --no-restore --verbosity minimal
$env:PSExecutionPolicyPreference='Bypass'
dotnet test CodexHistorySync.sln --no-restore --verbosity minimal
```

Result: build 0 warnings/errors; full suite 235 passed, 0 failed, 0 skipped — Core 103, Integration 112, Git 14, Windows 6. Independent re-review remains pending.

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
- Added metadata-only status, doctor, and conflict output. Conflict export delegates boundary and reparse validation to `ConflictStore.ResolveAsync`; keep resolutions run through the repository-locked sync engine.
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
- Manual operations delegate to the Task 7 engine and retain its authenticated-index, CAS, staging, conflict, backup, atomic mutation, and restart-recovery invariants. The round-1 corrections extend that engine with read-only planning and transactional conflict resolution.

## Review status

No independent review result is claimed. The controller should review the Task 8 implementation and report commit.

## Review round 1 corrections

The first independent review reported six important issues. This correction set addresses all six without claiming that the re-review has passed:

- `resolve` now executes a repository-locked, authenticated resolution transaction. Keep-local/keep-remote validate evidence and current local/remote versions, publish the selected remote version with CAS before local mutation, use the guarded writer and durable rollback journal, persist the baseline, and remove evidence only after all required work succeeds. Export-both deliberately retains evidence and returns unresolved status.
- `SyncEngine.PreviewAsync` uses the same authenticated snapshot, baseline, local-version construction, and three-way planner inputs as synchronization while remaining read-only with respect to key/config/state/evidence/history. Join preview and status consume this result, and join apply returns the actual pull result.
- Compatibility probing now creates a controlled synthetic JSONL fixture in a disposable directory instead of requiring an existing live-home session.
- Pending join contexts have an idempotent abort path. Dry-run, gate failure, cancellation, successful apply, and failed apply all remove the context and zero the master-key buffer.
- Initialization checks private visibility and all advertised remote refs before reading passphrases, while publication repeats the emptiness check for race safety.
- Setup pins the branch SHA first and reads both setup files at that exact SHA. Preview rejects a different provider revision, and apply rechecks the branch SHA before any persistent local write.

Regression coverage includes equal-count divergence, keep-local end-to-end resolution, export remaining unresolved, state failure after remote CAS with evidence-retaining retry, fresh-install compatibility, dry-run/abort/apply-failure key cleanup, nonempty initialization without a secret read, actual join-apply conflict exits, and concurrent branch update rejection.

Verification after the correction set:

```powershell
dotnet build CodexHistorySync.sln --no-restore --verbosity minimal
$env:PSExecutionPolicyPreference='Bypass'
dotnet test CodexHistorySync.sln --no-restore --verbosity minimal
```

Result: focused correction suite 38 passed; build 0 warnings/errors; full suite 227 passed, 0 failed, 0 skipped — Core 101, Integration 106, Git 14, Windows 6. Independent re-review remains pending.
