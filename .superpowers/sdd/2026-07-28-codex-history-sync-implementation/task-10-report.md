# Task 10 Report: Security Audit, Recovery Drill, and Release Packaging

## Status

Implementation, documentation, security/recovery tests, full Release verification, publication, and local executable smoke/audit are complete. The release artifact was rebuilt from the verified source tree. No clean-profile result or independent review approval is claimed.

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

`artifacts/win-x64` contains exactly one file: `codex-sync.exe`, 74,164,986 bytes, with PE `MZ` header and SHA-256 `0deb6e6fc4f1ccb46f616b6bfa3f8c9881504752911ff69eefe235bd39f5a734`. `--help` returned exit code 0 and listed `init`, `join`, `sync`, `pull`, `push`, `status`, `doctor`, `conflicts`, `resolve`, and `agent`. `doctor` returned the expected unconfigured-profile gate code 3 and emitted only named PASS/FAIL checks. Binary scans found no workspace path or fixed passphrase/credential canary; tracked-source scans found no complete credential-bearing URL, workspace path, or fixed 32-hex canary. Disposable security, initialization, and recovery temp-directory scans were empty. `git diff --check` passed.

The artifact was exercised on this development Windows profile. A clean Windows 11 profile run remains unverified and must not be inferred from the self-contained publication or local smoke test.

The disposable two-device engine/recovery acceptance is automated and passing, but no real GitHub private repository, real user Task Scheduler task, real desktop notification, or real Codex history was mutated. Private visibility and scheduler ownership are covered through deterministic tests.

## Deferred risks

- DPAPI concurrent save/load/delete stress, replacement ACL retention, injected temp cleanup, and exceptional-path zeroization remain the Task 3 defense-in-depth minor; the existing DPAPI/user-isolation suite passes.
- Whitespace-only JSONL policy remains explicitly undecided; complete newline-terminated JSON records are required and partial records defer.
- Windows handle-relative traversal hardening against a privileged last-instruction ancestor swap remains beyond the existing canonical/reparse checks.
- Clean-profile execution remains unverified; only publication and local smoke are passed.
- Independent review remains pending; no independent approval is claimed.
