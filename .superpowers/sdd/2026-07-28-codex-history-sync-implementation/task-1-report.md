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

- The deterministic fake app-server asserts initialization, `initialized`, `thread/list` with `useStateDbOnly: false`, no pre-existing SQLite in the disposable home, imported JSONL presence, and thread-ID matching.
- External review found malformed-session and cleanup robustness issues. Malformed JSONL now returns an incompatible diagnostic; cleanup retries transient IO/access failures and treats an already-removed home as cleaned up.
- `git diff --check` was clean before the initial implementation commit. No fixture file is tracked.

## Concerns

None remaining. Windows may briefly retain app-server SQLite log handles after process termination; the probe retries disposable-home deletion for five seconds and the verified real gate completed successfully.
