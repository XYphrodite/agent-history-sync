# Task 1 report: Solution Scaffold and JSONL Compatibility Gate

## Status

DONE

## Commits

- `f45fe1dab9741d143eff570db5c8c828014a93c9` — solution scaffold, probe, CLI, deterministic protocol test, and sanitized compatibility evidence.
- `79d247a` — post-review malformed-session and cleanup hardening, tests, and this report.

## Files changed

- `CodexHistorySync.sln` and `Directory.Build.props`
- `src/CodexHistorySync.Core/CodexHistorySync.Core.csproj`
- `src/CodexHistorySync.Core/Codex/CodexCompatibilityProbe.cs`
- `src/CodexHistorySync.Cli/CodexHistorySync.Cli.csproj` and `Program.cs`
- `tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj` and `CodexCompatibilityProbeTests.cs`
- `docs/compatibility.md`

## Tests

- `dotnet test tests/CodexHistorySync.IntegrationTests --filter ImportedJsonlIsListedWithoutCopyingSqlite` — initial RED: missing probe type; later RED: missing `initialized` notification; final PASS.
- `dotnet test ... --filter MalformedJsonlReturnsAnIncompatibleDiagnostic` — RED: `JsonReaderException` escaped; fixed by returning a content-free incompatible diagnostic.
- `dotnet test CodexHistorySync.sln --results-directory <temporary directory>` — PASS: 2 passed, 0 failed, 0 skipped.

## Compatibility-gate evidence

- Exact installed version: `codex-cli 0.146.0-alpha.3.1`.
- Sanitized explicit command: `dotnet CodexHistorySync.Cli.dll doctor --compatibility-session <inactive archived JSONL> --codex-exe <codex executable>`.
- Result: exit code `0`; the app-server reported `codex_vscode/0.146.0-alpha.3.1 (Windows 10.0.26200; x86_64) unknown (codex-history-sync; 0.1.0)` and listed the imported thread from a disposable Codex home.
- The source fixture was read only. Its path and contents were never printed or committed. The probe copied no SQLite or authentication state.

## Self-review

- The deterministic fake app-server asserts initialization, `initialized`, `thread/list` with `useStateDbOnly: false`, no pre-existing SQLite in the disposable home, imported JSONL presence in `sessions/YYYY/MM/DD`, and thread-ID matching.
- External review found malformed-session and cleanup robustness issues. Malformed JSONL now returns an incompatible diagnostic; cleanup retries transient IO/access failures and treats an already-removed home as cleaned up.
- `git diff --check` was clean before the initial implementation commit. No fixture file is tracked.

## Concerns

None remaining. Windows may briefly retain app-server SQLite log handles after process termination; the probe retries disposable-home deletion for five seconds and the verified real gate completed successfully.

## Fix round 1

- Commit: `87cca669dbac75dced0ae78aa24f86045f1d5c61`.
- Covering test file: `tests/CodexHistorySync.IntegrationTests/CodexCompatibilityProbeTests.cs`.
- `dotnet test tests/CodexHistorySync.IntegrationTests --filter DisallowedSessionFileIsRejectedBeforeCopyOrChildLaunch` — initial RED: five disallowed files reached later probe behavior; GREEN: 5 passed. The cases cover `auth.json`, case variation, `*.sqlite`, `*.sqlite-*`, and non-JSONL extension; each proves no child launch and no path/content in the diagnostic.
- `dotnet test tests/CodexHistorySync.IntegrationTests --filter PersistentCleanupFailureReturnsAnIncompatibleDiagnostic` — initial RED: no injected cleanup seam; GREEN: 1 passed. Persistent cleanup failure is returned as a sanitized incompatible result, which the CLI maps to exit code 3.
- `dotnet test tests/CodexHistorySync.IntegrationTests --filter LargeChildStderrDoesNotBlockTheCompatibilityProbe` — initial RED: cancellation after a stderr pipe block; GREEN: 1 passed after discard-only asynchronous stderr draining.
- `dotnet test CodexHistorySync.sln --results-directory <temporary directory>` — PASS: 9 passed, 0 failed, 0 skipped.
- Residual concern: cleanup is best-effort. If deletion remains impossible after retries, the probe reports incompatibility and does not claim deletion; it does not disclose the disposable path or copied session content.
