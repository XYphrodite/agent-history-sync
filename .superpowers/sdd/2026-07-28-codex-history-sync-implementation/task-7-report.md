# Task 7 Report: End-to-End Synchronization Engine

## Status

Implementation and review-round-1 fixes are complete and verified. Independent re-review is still required; no independent approval is claimed.

## Commits

- `c121e38` — `feat: synchronize encrypted history end to end`
- The documentation commit containing this report is recorded in the final handoff.

## Files

- `src/CodexHistorySync.Core/Model/SyncModels.cs` — adds the dedicated `RepositoryIndex` envelope kind.
- `src/CodexHistorySync.Core/Sync/SyncEngine.cs` — repository mutex, authenticated canonical index, exact ciphertext validation, three-way orchestration, staged imports, conflict preservation, five-attempt CAS, mode gates, and post-success baseline advancement.
- `tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj` — references the Git provider for the real two-device test.
- `tests/CodexHistorySync.IntegrationTests/TwoDeviceSyncTests.cs` — two disposable Codex homes converging through a bare Git remote without remote plaintext.
- `tests/CodexHistorySync.IntegrationTests/SyncFailureTests.cs` — authentication, corruption, staging, race, process, mode, conflict, mutex, and baseline failure coverage.

## Recovered-work audit

Recovery began from `5c480e0` with no Task 7 report and exactly five intended Task 7 source/test paths dirty: two modified files and three new files. The existing work was preserved and inspected rather than discarded.

The recovered implementation already provided the public API, a CHS1-encrypted repository index, two-device coverage, basic mode handling, a process-local repository semaphore, five CAS attempts, and 23 passing focused tests. Audit identified these correctness/security gaps:

- downloads were imported one at a time before all incoming JSONL had been staged and validated;
- Codex history could mutate before CAS publication succeeded, including after all five rejected attempts;
- the authenticated opaque ID was not checked against `SHA256(ciphertext)`;
- authenticated but non-canonical unsorted indexes were accepted;
- retrying the same publication race duplicated conflict evidence;
- stale-baseline recovery after an already-applied tombstone manufactured a conflict;
- an authenticated remote removal versus a local modification reported but did not preserve the deletion side;
- a tombstone mutation race reported a conflict without preserving the concurrent local version;
- conflict evidence could be published before a later invalid download failed staging;
- the original interruption/Codex-start tests did not prove replacement of an existing history file and baseline was preserved.

The final engine stages and validates every permitted download first, then preserves planned conflicts, stages uploads, resolves CAS publication, applies guarded Codex mutations, and saves baseline state only after the complete successful attempt. Rejected CAS attempts perform no live-history mutation. Conflict fingerprints prevent duplicate evidence for an unchanged conflict while retaining changed conflict versions.

## Test evidence

### Review round 1: recovery and atomicity

The inherited round-one changes were preserved and completed in commit `e5fbb52` (`fix: harden synchronization recovery`). The round includes scanner uncertainty propagation plus a final deletion rescan, explicit authenticated remote absence, import preconditions for edits after scanning, an OS-wide exclusive `.sync.lock`, delayed conflict publication with persisted fingerprint deduplication, and atomic multi-file local mutation recovery.

The local mutation design captures and verifies every prior file before the first live mutation, then writes a durable JSON marker in the existing operation directory. The marker contains only local target metadata, before/after hashes, backup IDs, and mutation status; it contains no prompt plaintext, key, credential, or remote data. Exceptions, cancellation, and state-save failure trigger reverse conditional rollback with `CancellationToken.None`. A new process recovers leftover markers under the repository lock before contacting the provider. Atomic `state.json` is the commit discriminator: if the baseline already represents all applied after-states, restart removes a stale marker without undoing committed history.

TDD evidence:

- The initial batch/restart tests failed compilation because `HistoryMutationBatch` and `HistoryMutationPlan` did not exist. After implementation, state-save failure and interrupted-process recovery passed 2/2.
- `Restart_DoesNotRollbackMutationWhoseBaselineWasAlreadySaved` failed because restart restored old history after an already-successful state save; it passed after baseline-aware recovery and the post-save rollback boundary were added.
- `Restart_RejectsInvalidMutationStatusWithoutDiscardingRecoveryEvidence` failed by deleting a marker with an unknown status and reaching the offline provider; it passed after strict journal validation.
- The multi-file rollback test covers a replacement and a tombstone in one batch and verifies both originals plus the old baseline are restored after injected state-save failure.

Final focused command:

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj --filter "FullyQualifiedName~TwoDeviceSyncTests|FullyQualifiedName~SyncFailureTests" --no-restore --verbosity minimal
```

Result: 44 passed, 0 failed, 0 skipped.

Final full command:

```powershell
$env:PSExecutionPolicyPreference='Bypass'
dotnet test CodexHistorySync.sln --no-restore --verbosity minimal
```

Result: 175 passed, 0 failed, 0 skipped — Core 100, Integration 55, Git 14, Windows 6. `dotnet build CodexHistorySync.sln --no-restore --verbosity minimal` also completed with 0 warnings and 0 errors, and `git diff --cached --check` was clean before commit `e5fbb52`.

### Known initial RED before recovered work

The original Task 7 focused command was:

```powershell
dotnet test tests/CodexHistorySync.IntegrationTests --filter TwoDeviceSyncTests
```

It failed at compilation with `CS0246` because `SyncEngine` was absent. This evidence was supplied with the recovery brief and is retained here because the vanished implementer had already added production code before recovery began.

### Recovered baseline

```powershell
dotnet restore CodexHistorySync.sln
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj --filter "FullyQualifiedName~TwoDeviceSyncTests|FullyQualifiedName~SyncFailureTests" --no-restore --verbosity minimal
```

Result: restore passed; recovered focused suite passed 23/23.

### Audit-driven RED/GREEN cycles

- Exact authentication/canonical staging batch: focused command covering opaque-ID mismatch, unsorted index, invalid-later download, and five rejected CAS attempts failed exactly 4 cases with 11 passing. After the engine corrections, the same batch passed 15/15.
- Conflict retry evidence: `PublicationRetry_PreservesOneRecordForTheSameConflict` failed because two records were published; passed 1/1 after per-call conflict fingerprinting.
- Tombstone stale-baseline recovery: `AppliedTombstone_WithUnadvancedBaseline_IsAcceptedOnRecovery` failed with one manufactured conflict; passed 1/1 after missing baseline objects were projected as explicit local deletions.
- Authenticated remote removal conflict: `RemoteRemovalVersusLocalModification_PreservesBothConflictSides` failed with no preserved record; passed 1/1 after generating an authenticated empty deletion envelope for the absent remote side.
- Tombstone mutation conflict: `TombstoneRace_PreservesConcurrentLocalVersionAsConflict` failed with no preserved record; passed 1/1 after stable rescan and conflict preservation.
- Stage-before-any-mutation ordering: `InvalidDownload_IsRejectedBeforeConflictEvidenceIsPublished` failed because conflict evidence existed after the invalid download error; passed 1/1 after moving preservation after complete download staging.
- Repository mutex characterization: `RepositoryMutex_SerializesEnginesSharingLocalState` passed 1/1 and observed a maximum of one concurrent provider read across two engines sharing the repository state path.

### Final focused verification

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj --filter "FullyQualifiedName~TwoDeviceSyncTests|FullyQualifiedName~SyncFailureTests" --no-restore --verbosity minimal
```

Result: 33 passed, 0 failed, 0 skipped.

### Full verification

The first non-elevated full run exposed seven existing environment-sensitive failures: three symlink tests could not create links under the sandbox token, three DPAPI ACL tests received `UnauthorizedAccessException` from `SetSecurityInfo`, and the Git redaction helper `.ps1` was blocked by machine execution policy. The exact Git test passed 1/1 when given only the process-scoped `PSExecutionPolicyPreference=Bypass`. No production code or tests were weakened for these environment restrictions.

Final command, using the Windows privileges required by the pre-existing ACL/symlink tests:

```powershell
$env:PSExecutionPolicyPreference='Bypass'
dotnet test CodexHistorySync.sln --no-restore --verbosity minimal
```

Result: 161 passed, 0 failed, 0 skipped — Core 97, Git 14, Windows 6, Integration 44.

`git diff --cached --check` was clean before the implementation commit.

## Final security and correctness review

- The provider remains keyless. Index ciphertext is authenticated with exact `(__repository_index__, RepositoryIndex, schema 1)` metadata before any index-derived `ObjectVersion` is constructed.
- Index schema and repository ID are authenticated. Logical IDs are unique and strictly sorted, object kinds and hashes are validated, opaque references are unique, and the referenced ciphertext set must exactly equal the provider snapshot.
- Every opaque object ID must equal lowercase SHA-256 of its exact ciphertext. Every CHS1 envelope must authenticate under its logical ID/kind metadata, and decrypted plaintext must match the authenticated plaintext hash.
- Uploads use fresh CHS1 envelopes; the index stores sorted entries with numeric kind, plaintext hash, deletion flag, ciphertext hash, and version. Git paths expose only opaque `.chs` content.
- All allowed downloads are durably staged, reread, hash-checked, and strict-UTF-8/complete-JSONL validated before conflict-store publication, Codex mutation, or remote publication.
- CAS publication is resolved before live Codex imports/tombstones. A rejected attempt cleans staging and repeats from scanning; exactly five rejected publications throw without baseline advancement or downloaded-history mutation.
- Pull forbids upload/tombstone publication; Push forbids imports/local tombstone application; Bidirectional permits both. Planning and conflict reporting run in every mode.
- Imports and tombstones delegate final mutation to the existing atomic writer and its last-moment Codex process guard. Interrupted replacement and Codex-start tests preserve an existing live file and its prior baseline.
- Planned, retrying, remote-removal, and tombstone-race conflicts preserve encrypted evidence. The live local version is never implicitly overwritten by a conflict.
- Device baseline is atomically saved only after remote publication and every permitted local operation finish. Explicit local deletion projection makes a safe already-applied tombstone recoverable when the prior baseline remains.
- The staging root is canonicalized, rejected if it overlaps synchronized Codex paths, and operation directories are removed in `finally`. Tests prove invalid staged plaintext leaves no staged JSONL residue.
- No logging was added. Keys, prompts, plaintext history, credentials, and local paths are not emitted to remote storage or command output.

## Self-review and review status

The final requirement-by-requirement self-review found and corrected the remote-removal conflict, tombstone-race evidence, and strict stage-before-conflict-publication gaps described above. An independent read-only reviewer was requested but did not return a usable finding set or verdict before being interrupted; no independent approval is claimed. The controller can perform its own final review from commit `c121e38` plus the report commit.

## Concerns

- Task 2 intentionally deferred attachment discovery because no safe documented typed attachment field was available. The Task 7 engine therefore synchronizes the active and archived session objects returned by the current scanner; authenticated attachment entries are not installed as Codex history.
- Automatic recovery refuses to overwrite a file whose current hash matches neither the journaled before-state nor after-state. The marker and backups remain for explicit recovery rather than risking loss of a concurrent user edit.
