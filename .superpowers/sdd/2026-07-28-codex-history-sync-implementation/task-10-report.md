# Task 10 Report: Security Audit, Recovery Drill, and Release Packaging

## Status

Implementation, documentation, security/recovery tests, full Release verification, publication, and local executable smoke/audit are complete through review round 3. The release artifact was rebuilt from the corrected source tree. No clean-profile result or independent review approval is claimed.

## Implemented scope

- Added a synthetic repository-wide security boundary test. It initializes a public authenticated setup manifest and encrypted empty index, seeds unique runtime canaries in prompt, auth, SQLite, log, sandbox-secret, attachment, passphrase, credential-URL, and local-path inputs, then publishes multiple history revisions through the real `SyncEngine` and local bare Git provider.
- The audit enumerates every object reachable from every Git ref with `rev-list --objects --all`, scans every reachable blob and path (including historical commits), authenticates the allowed setup manifest separately, requires `CHS1` for every encrypted index/history blob, decrypts every historical index, and authenticates each referenced history object using its indexed logical ID and kind. It also audits the dedicated working clone, typed agent logs, and staging cleanup.
- Attachment canaries are exclusion evidence: attachment discovery remains intentionally disabled until Codex supplies a documented typed attachment reference.
- Added an end-to-end recovery drill over a real bare remote and two disposable devices: baseline, divergent same-chat changes, conflict preservation, `--export-both`, literal SHA-256 set comparison, keep-remote resolution, injected disk-full import plus simultaneous cleanup failure, unchanged live history and baseline, and verified backup restoration.
- The injected disk-full test proves its primary error remains visible when operation cleanup also fails; no history or baseline advances. The pre-existing wrong-key, tamper, offline, push-race, interruption, late-process, rollback, and exhausted-retry tests were rerun as part of the focused failure matrix.
- Fixed initialization cleanup on Windows. A successfully published remote initialization is authoritative; cleanup now normalizes owned temporary-clone file attributes and is best effort, so a read-only Git artifact or cleanup error cannot replace the primary result and encourage unsafe reinitialization.
- Configured the Release CLI for `win-x64`, self-contained, single-file, untrimmed publication with assembly name `codex-sync`. The properties are Release-conditioned so ordinary test builds do not require single-file linker tooling; evaluated Release values match the required contract.
- Added `README.md`, `docs/security.md`, and expanded `docs/operations.md` with install/init/join/agent/recovery/uninstall commands; threat model and GitHub-visible metadata; passphrase and DPAPI recovery limits; private visibility failure; exclusions and attachment policy; migration-based key rotation; tombstone/backup retention; manual hash-verified offline restore; and unsupported same-chat merge behavior.
- Removed fixed plaintext passphrase/prompt markers and complete credential-bearing URLs from tracked test source while preserving runtime redaction coverage through assembled synthetic values.

## TDD evidence

Initial focused command:

```powershell
dotnet test tests\CodexHistorySync.IntegrationTests --filter "SecurityBoundaryTests|RecoveryDrillTests" --no-restore --verbosity minimal
```

After test-harness compile corrections, the behavioral RED was 1 passed / 2 failed:

- release identity expected `codex-sync`, actual `CodexHistorySync.Cli`;
- the real security flow published initialization and then failed because temporary Git cleanup threw `UnauthorizedAccessException`, masking successful publication;
- the recovery drill passed.

After Release identity and owned-temporary cleanup wiring, two audit-only parser corrections classified `rev-list` tree objects separately from blobs. Fresh focused GREEN: 3 passed, 0 failed, 0 skipped in 23.6 seconds.

## Verification evidence

Focused security/failure/ownership matrix, with the repository's documented PowerShell execution-policy fixture setting:

```powershell
$env:NUGET_PACKAGES='C:\Users\Gamer\.nuget\packages'
$env:PSExecutionPolicyPreference='Bypass'
dotnet test tests\CodexHistorySync.IntegrationTests --filter "(FullyQualifiedName~SecurityBoundaryTests|FullyQualifiedName~RecoveryDrillTests|FullyQualifiedName~SyncFailureTests|FullyQualifiedName~CliTests|FullyQualifiedName~CliServiceTests)&FullyQualifiedName!~ReparseDirectory" --no-restore --verbosity minimal
dotnet test tests\CodexHistorySync.Git.Tests --no-restore --verbosity minimal
dotnet test tests\CodexHistorySync.Windows.Tests --filter "AgentSchedulerTests" --no-restore --verbosity minimal
```

Result: Integration 102/102, Git 14/14, Windows scheduler 10/10.

Fresh full unrestricted Debug suite:

```powershell
$env:NUGET_PACKAGES='C:\Users\Gamer\.nuget\packages'
$env:PSExecutionPolicyPreference='Bypass'
dotnet test CodexHistorySync.sln --no-restore --verbosity minimal
```

Result: 274 passed, 0 failed, 0 skipped — Core 107, Integration 129, Git 14, Windows 24.

Recovery-session verification on the final source tree:

```powershell
$env:NUGET_PACKAGES='C:\Users\Gamer\.nuget\packages'
$env:PSExecutionPolicyPreference='Bypass'
dotnet test tests\CodexHistorySync.IntegrationTests --filter "SecurityBoundaryTests|RecoveryDrillTests" --no-restore --verbosity minimal
dotnet test CodexHistorySync.sln -c Release --no-restore --verbosity minimal
```

Result: focused 3/3; full Release 276/276 — Core 107, Integration 131, Git 14, Windows 24. The ordinary restricted sandbox token first produced four unavailable symbolic-link/reparse dynamic skips surfaced as failures and three expected ACL/DPAPI `UnauthorizedAccessException` failures. The exact full Release command then passed under the unrestricted Windows token required by those tests.

Release property evaluation:

```powershell
dotnet msbuild src\CodexHistorySync.Cli\CodexHistorySync.Cli.csproj -p:Configuration=Release -getProperty:RuntimeIdentifier,SelfContained,PublishSingleFile,PublishTrimmed,AssemblyName
```

Result: `win-x64`, `true`, `true`, `false`, `codex-sync`.

Tracked-source scans found no complete credential-bearing URL and no fixed prompt/passphrase canary. `git diff --check` passed. Two known disposable initialization directories left by the intentionally failing RED were validated inside the system temp root and removed; the later GREEN flow left none.

## Release artifact and unclaimed acceptance

The required publication completed from the final verified source tree:

```powershell
dotnet publish src/CodexHistorySync.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --no-restore -o artifacts/win-x64
```

`artifacts/win-x64` contains exactly one file: `codex-sync.exe`, 74,169,082 bytes, with PE `MZ` header and SHA-256 `36c1a4f1f2df0b4e3231db61f3b3d7f1043dca04f17216ccd684f012943ddc07`. `--help` returned exit code 0 and listed `init`, `join`, `sync`, `pull`, `push`, `status`, `doctor`, `conflicts`, `resolve`, and `agent`. `doctor` returned the expected unconfigured-profile gate code 3 and emitted only named PASS/FAIL checks. Binary scans found no workspace path or fixed passphrase/credential canary; tracked-source scans found no complete credential-bearing URL, workspace path, or fixed 32-hex canary. Disposable security, initialization, recovery, owned-cleanup, and isolated-smoke temp-directory scans were empty. `git diff --check` passed.

The artifact was exercised on this development Windows profile. A clean Windows 11 profile run remains unverified and must not be inferred from the self-contained publication or local smoke test.

## Review round 1 corrections

- Removed the dedicated clone's blanket `.git` exclusion. The audit now uses a GitHub-shaped sanitized remote over a disposable loopback Git transport, explicitly allowlists structural Git metadata, and scans metadata paths and bytes, config, refs, reflogs, reachable objects, all stored objects, and raw loose/pack/temporary object-store files. Its RED run caught the prior local-path leak in the clone reflog; focused GREEN was 2/2.
- Replaced raw recursive initialization cleanup with a process-owned temporary directory. Each directory has an unpredictable durable marker token, must be an immediate canonical child with the expected prefix beneath a concrete temp root, is traversed manually without following reparse points, is fully revalidated after a simulated ancestor-swap boundary, and is deleted leaf-first with the marker and root last. Missing/tampered markers, reparse descendants, access/type uncertainty, and ancestor substitution retain the tree. Cleanup remains best effort and cannot replace a successful publication or primary error. RED was the missing ownership API; elevated focused GREEN was 4/4.
- Added deterministic interruption coverage after staged download plus backup/journal creation and immediately at guarded local replacement. Both cases preserve live bytes and baseline, retain verified backup and cleanup evidence, and prove a recreated engine removes abandoned plaintext staging before the injected offline provider is read. The existing simultaneous disk-full/cleanup-failure drill remains.
- Added a real local-Git `AgentWorker` acceptance run over two disposable devices. Interleaved cycles report exact upload/download counts; recreated engines and fresh scanner indexes converge after restart; same-chat divergence produces exact provenance, encrypted evidence, notification, and exported hashes; `ExportBoth` remains unresolved; explicit `KeepRemote` retires evidence and both devices converge.
- Expanded root-scoped ignore rules for generated configuration, DPAPI keys, state, provider clones, backups, conflicts, staging, logs, root `.codex`, and copied-history fixtures. A real `git check-ignore`/`ls-files` test proves sensitive generated paths are ignored while product source and synthetic test fixtures are not hidden. RED was 0/1; GREEN was 1/1.

Fresh combined focused verification passed 11/11. Full unrestricted Release verification passed 284/284 — Core 107, Integration 139, Git 14, Windows 24. The republished artifact contains one 74,169,082-byte PE executable with SHA-256 `36c1a4f1f2df0b4e3231db61f3b3d7f1043dca04f17216ccd684f012943ddc07`; help returned 0 and unconfigured doctor returned 3 with PASS/FAIL-only output. Artifact, source, temp-canary, and diff scans were clean.

Read-only host inspection found no `WindowsSandbox.exe`, Docker, Podman, VMware, VirtualBox, VM compute service, or installed WSL distribution. No feature or account was created. A supplemental current-host smoke redirected `USERPROFILE`, `LOCALAPPDATA`, `APPDATA`, `CODEX_HOME`, `TEMP`, and `TMP`; doctor returned 3 with PASS/FAIL-only output, created only Codex's three disposable argument-helper files beneath the redirected `CODEX_HOME`, and the isolated root was removed. This is not a clean-profile result.

The disposable two-device engine/recovery acceptance is automated and passing, but no real GitHub private repository, real user Task Scheduler task, real desktop notification, or real Codex history was mutated. Private visibility and scheduler ownership are covered through deterministic tests.

## Review round 2 native cleanup correction

- Replaced the remaining path-based validation/deletion gap with a Windows-only handle-relative deleter. Creation stores the root `FILE_ID_INFO`; cleanup opens the root without following reparse points and requires the stored volume/file identity before traversing.
- Directory enumeration uses `NtQueryDirectoryFile` on directory handles. Every enumerated child name rejects empty, dot, NUL, and separator components, and every child is opened relative to its retained parent handle with `NtCreateFile`, `RootDirectory`, and `FILE_OPEN_REPARSE_POINT`. Reparse points, type changes, identity changes, access failures, malformed native records, and unsupported native entry points fail closed and retain the owned root.
- Every root, directory, file, and marker handle is retained without `FILE_SHARE_DELETE` through complete identity/type/content snapshots and the exact pre-mutation test hook. This prevents a validated descendant ancestor from being renamed into a junction during the last instruction gap. The unpredictable marker is read and revalidated only through its retained handle.
- Deletion is leaf-first through `SetFileInformationByHandle(FileDispositionInfoEx)` with `DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE`; each successfully dispositioned leaf handle is then closed so its name is unlinked before its parent. No `FileBasicInfo`, attribute normalization, recursive path delete, or other path-based mutation remains. Marker and root are dispositioned last. Non-Windows or unavailable/unsupported native behavior returns false and leaves the owned temporary directory for inspection.
- The pre-existing review RED test was preserved unchanged. It first failed to compile because `beforeFirstMutation` did not exist. Elevated ownership verification then passed 4/4 with a real reparse point and a real successful read-only leaf deletion, proving the last-gap test is not vacuous. Combined elevated ownership/security/recovery verification passed 10/10 with zero skips.
- A fresh unrestricted Release run before the final exceptional-path handle-disposal-only correction passed 284/284 (Core 107, Integration 139, Git 14, Windows 24). The attempted repeat after that correction was interrupted by the controller; no result is inferred from the interrupted run.
- The republished artifact contains one 74,181,370-byte PE executable with SHA-256 `b1c3f586b55f8ec7a4fa03f6863b0fa449f527949e32c90e916c2b799918a0c0`. Help returned 0; unconfigured doctor returned 3 with PASS/FAIL-only output. Redirected-current-profile smoke created only expected Codex argument-helper files under the redirected profile and its verified temporary root was removed. Artifact, tracked-source, disposable-temp, and diff scans were clean.

## Review round 3 last-instruction ordering correction

- `WindowsOwnedTreeDeleter.TryDelete` now performs its final complete `ValidateSnapshot` before invoking `beforeFirstMutation`. If the hook returns, execution proceeds immediately through the already-built in-memory postorder and `DeleteByHandle`; no identity/type/enumeration/marker validation or path mutation occurs after the hook.
- The exact descendant-swap test now records and asserts `hookInvoked`, attempts the real descendant ancestor rename/junction substitution from that hook, requires `TryDelete` to fail, and proves the external sentinel is untouched. On the pre-fix round-2 implementation the strengthened test passed 4/4 because the retained handles already deny the rename; this was not represented as a new behavioral RED. The correction removes the redundant post-hook validation and establishes the requested no-operation gap directly in production ordering.
- Elevated ownership verification passed 4/4 with zero skips, including normal deletion of a read-only owned leaf. Combined elevated ownership/security/recovery verification passed 10/10 with zero skips. Full unrestricted Release verification passed 284/284: Core 107, Integration 139, Git 14, Windows 24.
- The republished output contains exactly one 74,181,370-byte PE executable with `MZ` header and SHA-256 `512664a8153ad3ca738dab30bcdaac521e6b29193a849405baa7d6410d816f47`. `--help` returned 0 and listed every documented command; unconfigured `doctor` returned 3 with nine named PASS/FAIL-only lines.
- Redirected-current-profile smoke returned the same doctor result, created only Codex's three disposable argument-helper files beneath redirected `CODEX_HOME`, and removed its validated GUID temporary root. Tracked-source and binary scans found no workspace path or credential-bearing URL; the only tracked 32-hex literals were three allowlisted cryptographic salt fixtures. Disposable security/recovery/ownership/agent/smoke/init temp scans were empty and `git diff --check` passed.
- A true clean Windows profile was not available and remains unverified. The user explicitly chose to skip creation of a temporary Windows account on 2026-08-05; this is an accepted external-test waiver, not a successful clean-profile result.

## Controller completion disposition

- Independent round-3 review passed with no findings after verifying the true final-validation-to-first-mutation gap and the non-vacuous normal-deletion test.
- Controller Release verification passed 284/284: Core 107, Integration 139, Git 14, Windows 24.
- Controller smoke verified the single 74,181,370-byte `codex-sync.exe`, SHA-256 `512664a8153ad3ca738dab30bcdaac521e6b29193a849405baa7d6410d816f47`, `--help` exit 0, and unconfigured `doctor` exit 3 with named PASS/FAIL-only output.
- Task 10 is complete subject to the explicitly accepted clean-profile waiver above.

## Deferred risks

- DPAPI concurrent save/load/delete stress, replacement ACL retention, injected temp cleanup, and exceptional-path zeroization remain the Task 3 defense-in-depth minor; the existing DPAPI/user-isolation suite passes.
- Whitespace-only JSONL policy remains explicitly undecided; complete newline-terminated JSON records are required and partial records defer.
- Clean-profile execution remains unverified by explicit user choice; publication and local/redirected-profile smoke are passed.
