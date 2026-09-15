# Codex JSONL compatibility

## 2026-07-28 gate result

- `codex --version`: `codex-cli 0.146.0-alpha.3.1`
- Command (sensitive paths redacted): `dotnet CodexHistorySync.Cli.dll doctor --compatibility-session <inactive archived JSONL> --codex-exe <codex executable>`
- Result: exit code `0`; the app-server reported `codex_vscode/0.146.0-alpha.3.1 (Windows 10.0.26200; x86_64) unknown (codex-history-sync; 0.1.0)` and listed the imported thread from the disposable Codex home.
- The archived source JSONL was read only. Its path and contents were neither printed nor committed. No SQLite or authentication state was copied from the source profile.

## Kimi Code CLI copies (2026-09-15 note)

Cross-agent copies **into** Kimi are synthesized best-effort: a fresh `session_<uuid>` directory with `state.json`, a minimal `wire.jsonl`, and a merged `session_index.jsonl` line, written to match the observed on-disk shape. Resuming such a session inside Kimi Code CLI has **not** been verified the way the Codex import above was verified against a live CLI; treat imported copies as archived transcripts. Sync of native Kimi sessions (state + wires + plans) round-trips by package hash across machines and is covered by tests.
