# SDD ledger вЂ” plan: docs/superpowers/plans/2026-07-28-codex-history-sync-implementation.md

Branch start: 10a09cf
Worktree: C:\Repos\codex-history-sync\.worktrees\feature-history-sync

Task 9: implementation complete (canonical fail-closed process detection, exact per-user Task Scheduler ownership, active export-only worker, quiescent bidirectional recovery, notifications, redacted rotating logs, CLI/doctor wiring; independent review pending)
Task 9: verification (initial contract RED; notification RED 0/1 then GREEN 1/1; scheduler XML RED compile then GREEN 2/2; trusted-root fallback RED 0/1 then GREEN 1/1; final focused 29/29; build 0 warnings/errors; final full elevated 268/268 — Core 107, Integration 124, Git 14, Windows 23)
Task 9: fix round 1/5 implementation complete (additive configured-path/trusted-root process matching, typed late-Codex-active rollback/defer signal, and cancellation-safe generic failure handling; implementation commit feb83e4; independent re-review pending)
Task 9: fix round 1 verification (detector RED 0/1 then GREEN 8/8; cancellation RED leaked TaskCanceledException then GREEN AgentWorker 13/13; late-activity RED generic exception/failure path then GREEN 3/3; focused 32/32; build 0 warnings/errors; full unrestricted 271/271 — Core 107, Integration 126, Git 14, Windows 24)
Task 9: complete (commits 71440d6..fe5751d, round-1 independent review PASS with no findings; controller verification 271/271 — Core 107, Integration 126, Git 14, Windows 24)
Task 10: implementation/test/documentation and local release publication complete; independent review pending; no clean-profile claim
Task 10: TDD RED recovery 1/3 with release identity mismatch and successful-init cleanup masked by UnauthorizedAccessException; focused GREEN 3/3; failure matrix 126/126; full unrestricted Debug 274/274 — Core 107, Integration 129, Git 14, Windows 24
Task 10: recovery verification focused 3/3; full unrestricted Release 276/276 — Core 107, Integration 131, Git 14, Windows 24; published one 74,164,986-byte PE, SHA-256 0deb6e6fc4f1ccb46f616b6bfa3f8c9881504752911ff69eefe235bd39f5a734; help exit 0; unconfigured doctor exit 3 PASS/FAIL-only; artifact/source/temp scans clean
Task 10: review round 1/5 implementation complete (.git metadata/object/reflog audit with sanitized disposable transport; marker-owned fail-closed init cleanup; deterministic post-backup/pre-replace recovery; real interleaved AgentWorker two-device restart/conflict acceptance; root-scoped sensitive generated-data ignores); independent re-review pending; no clean-profile claim
Task 10: review round 1 TDD/verification (.git audit RED 1/2 local-path reflog leak then GREEN 2/2; cleanup RED missing API then elevated GREEN 4/4; interruption RED 0/2 then GREEN 2/2; agent acceptance RED 0/1 then GREEN 1/1; ignore RED 0/1 then GREEN 1/1; combined focused 11/11; full unrestricted Release 284/284 — Core 107, Integration 139, Git 14, Windows 24; one 74,169,082-byte PE, SHA-256 36c1a4f1f2df0b4e3231db61f3b3d7f1043dca04f17216ccd684f012943ddc07; normal and redirected-current-host smoke passed; true clean-profile unavailable/unclaimed; scans clean)
Task 10: review round 2/5 implementation complete (commit 7c987ee — Windows-only no-follow root identity, handle-relative NtCreateFile traversal, handle enumeration, retained no-share-delete handles through exact pre-mutation gap, marker-by-handle verification, and FileDispositionInfoEx leaf-first cleanup; independent re-review pending)
Task 10: review round 2 TDD/verification (preserved RED compile failure CS1739 for missing beforeFirstMutation; elevated OwnedTemporaryDirectory 4/4 with real reparse and concrete deletion; combined ownership/security/recovery 10/10; unrestricted Release before final exceptional-path handle-disposal-only correction 284/284 — Core 107, Integration 139, Git 14, Windows 24; repeat was controller-interrupted and is not claimed; republished one 74,181,370-byte PE, SHA-256 b1c3f586b55f8ec7a4fa03f6863b0fa449f527949e32c90e916c2b799918a0c0; help 0, doctor 3, redirected-current-profile smoke and scans clean; true clean-profile unavailable/unclaimed)
Task 10: review round 3/5 implementation complete (final complete snapshot validation now precedes beforeFirstMutation; execution after the hook is only in-memory postorder handle deletion; exact swap test explicitly proves hook invocation, failed swap cleanup, and untouched external sentinel); independent re-review pending; no clean-profile claim
Task 10: review round 3 verification (strengthened exact-gap test passed on pre-fix retained-handle implementation and was not claimed as behavioral RED; elevated ownership 4/4 and combined ownership/security/recovery 10/10 with zero skips; full unrestricted Release 284/284 — Core 107, Integration 139, Git 14, Windows 24; republished one 74,181,370-byte PE, SHA-256 512664a8153ad3ca738dab30bcdaac521e6b29193a849405baa7d6410d816f47; help 0, doctor 3, redirected-current-profile smoke and artifact/source/temp/diff scans clean; true clean-profile unavailable/unclaimed)

Task 8: fix round 3/5 implementation complete (schema-1 legacy fingerprint candidate recomputation, untrusted stored-fingerprint validation, and cross-kind non-conflation; re-review pending)
Task 8: fix round 3 verification (RED 1 passed/3 failed; GREEN 4/4; ConflictStore 21/21; full elevated 239/239 — Core 107, Integration 112, Git 14, Windows 6; build 0 warnings/errors; independent re-review pending)
Task 8: fix round 2/5 implementation complete (per-side authenticated conflict metadata and cross-kind resolution, serialized engine disposal/key zeroization, exact stable-identity conflict unions, dual status revisions, and atomic evidence retirement; re-review pending)
Task 8: fix round 2 verification (focused correction set 10/10; retirement 2/2; full elevated 235/235 — Core 103, Integration 112, Git 14, Windows 6; build 0 warnings/errors; independent re-review pending)

Task 1: fix round 1/5 (2 addressed, 1 open — reject any basename containing .sqlite case-insensitively; commits 87cca66..0fb39b5)
Task 1: fix round 2/5 (1 addressed, 0 open — basename .sqlite rejection; commits 0449af8..e3a9cd4)
Task 1: complete (commits 10a09cf..e3a9cd4, review clean; controller verification 11/11 tests and real compatibility gate exit 0)
Task 2: fix round 1/5 (2 addressed, 0 open — parser guard and disallowed path segments; commit 1d098e6)
Task 2: complete (commits e3a9cd4..1d098e6, review clean; controller verification 22/22 tests)
Task 3: minor (deferred): add DPAPI concurrent save/load/delete, replacement ACL retention, injected temp cleanup, and exceptional-path zeroization regression coverage
Task 3: fix round 1/5 (2 addressed, 0 open — strict repository IDs and LocalAppData validation; commit 9918906)
Task 3: complete (commits 1d098e6..9918906, review clean; controller verification 49/49 tests; 1 deferred minor)
Task 4: fix round 1/5 (1 addressed, 0 open — atomic state replacement failure test; commit acc4ee7)
Task 4: complete (commits 9918906..acc4ee7, review clean; controller verification 65/65 tests)
Task 5: minor (deferred): decide whitespace-only JSONL line policy and add regression
Task 5: minor (deferred): ensure cleanup never masks primary error; preserve structured staging evidence
Task 5: minor (deferred): evaluate Windows handle-relative APIs for privileged last-instruction ancestor swap hardening
Task 5: fix round 1/5 (5 addressed, 0 open — rollback, authenticated staging, final guard, reparse boundaries, failure evidence; commit 96da065)
Task 5: complete (commits acc4ee7..96da065, review clean; controller verification 114/114 tests; 3 deferred minors)
Task 6: human ruling — replace fabricated ObjectVersion provider contract with keyless opaque ciphertext objects + encrypted authenticated index; Task 7 decrypts index into authoritative ObjectVersion metadata
Task 6: fix round 1/5 (7 addressed, 2 open — exact materialization and generic query redaction; commits 23124e5..6fffacb)
Task 6: fix round 2/5 (1 addressed, 2 open — query apostrophe boundary and non-stale push classification; commits 9c2c9b2..b05efe7)
Task 6: fix round 3/5 (2 addressed, 0 open — complete query redaction and authoritative push outcomes; commits f6ef443..5c480e0)
Task 6: complete (commits 96da065..5c480e0, review clean; controller verification 128/128 tests)
Task 7: implementation recovered (commits c121e38, 7caa04b; focused 33/33 and full 161/161 before independent review)
Task 7: fix round 1/5 implementation complete (commit e5fbb52 — scanner uncertainty/final absence rescan, explicit remote absence, guarded imports, OS-wide repository lock, staged/deduplicated conflict evidence, atomic multi-file rollback, and restart recovery)
Task 7: fix round 1 verification (focused 44/44; full 175/175 — Core 100, Integration 55, Git 14, Windows 6; build 0 warnings/errors; independent re-review pending)
Task 7: fix round 2/5 implementation complete (commit 34c4a1b — initialized repositories require authenticated indexes, missing roots are uncertain, tombstones have immediate pre-CAS absence checks, and committed cleanup is marker-last/non-failing/recoverable)
Task 7: fix round 2 verification (focused 51/51; scanner 13/13; full 183/183 — Core 101, Integration 62, Git 14, Windows 6; build 0 warnings/errors; independent re-review pending)
Task 7: fix round 3/5 implementation complete (commit ebe8c87 — strict startup cleanup blocks provider access until operation-dir absence is confirmed; post-commit cleanup is non-failing for expected safety/filesystem denial)
Task 7: fix round 3 verification (focused 53/53; full 185/185 — Core 101, Integration 64, Git 14, Windows 6; build 0 warnings/errors; independent re-review pending)
Task 7: fix round 4/5 implementation complete (commit 02b3503 — cleanup evidence is durable before plaintext staging, startup recovers the union of enumerated operation directories and evidence-referenced paths, and marker probing is fail-closed without File.Exists)
Task 7: fix round 4 verification (new regressions RED 0/3 then GREEN 3/3; focused 56/56; full 188/188 — Core 101, Integration 67, Git 14, Windows 6; build 0 warnings/errors; independent re-review pending)
Task 7: fix round 5/5 implementation complete (commit 32d4ff4 — evidence-referenced recovery targets are classified before marker access; reparse points, ordinary files, and access/type uncertainty fail closed)
Task 7: fix round 5 verification (new junction/symlink regression RED then GREEN 1/1; focused 57/57; full 189/189 — Core 101, Integration 68, Git 14, Windows 6; build 0 warnings/errors; independent re-review pending)
Task 7: complete (commits 5c480e0..e2c2f5b, round-5 independent review PASS with no findings; controller verification 189/189 — Core 101, Integration 68, Git 14, Windows 6)
Task 8: implementation complete (CLI contracts and real composition; focused 28/28; full elevated 217/217 — Core 101, Integration 96, Git 14, Windows 6; independent review pending)
Task 8: fix round 1/5 implementation complete (real lock/CAS conflict resolution, authenticated read-only planning, disposable fresh-install compatibility, terminal join-key zeroization, pre-secret emptiness gate, and SHA-pinned/rechecked join setup; re-review pending)
Task 8: fix round 1 verification (focused 38/38; full elevated 227/227 — Core 101, Integration 106, Git 14, Windows 6; build 0 warnings/errors; independent re-review pending)
Task 8: fix round 2/5 implementation complete (per-side conflict metadata and cross-kind resolution, serialized engine key disposal, exact conflict union, dual revisions, and atomic evidence retirement; commits 509f6a1, d83f415)
Task 8: fix round 2 verification (full elevated 235/235 — Core 103, Integration 112, Git 14, Windows 6; build 0 warnings/errors; independent re-review found one legacy compatibility issue)
Task 8: fix round 3/5 implementation complete (legacy schema-1 conflict fingerprint deduplication without cross-kind conflation; commits b812ec2, 71440d6)
Task 8: complete (commits e2c2f5b..71440d6, round-3 independent review PASS with no findings; controller verification 239/239 — Core 107, Integration 112, Git 14, Windows 6)
