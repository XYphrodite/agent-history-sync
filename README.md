# Agent History Sync

Windows 11 x64 CLI that synchronizes **Codex** and **Grok CLI** conversation history through an encrypted private GitHub repository.

- **Codex:** active/archived session JSONL under `%USERPROFILE%\.codex` (normalized for size; no SQLite/auth/logs).
- **Grok CLI:** session packages (`chat_history` + `summary`) under `%USERPROFILE%\.grok\sessions` (no terminal logs).

The release binary is still named `codex-sync.exe` (stable install path). Git, GitHub CLI (`gh`), and the relevant agent CLIs are prerequisites.

## Repositories

| Repo | Purpose |
|---|---|
| [`XYphrodite/agent-history-sync`](https://github.com/XYphrodite/agent-history-sync) | Source code |
| [`XYphrodite/agent-history-sync-data`](https://github.com/XYphrodite/agent-history-sync-data) | Encrypted history data (private) |

## Install

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexHistorySync'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\codex-sync.exe (Join-Path $install 'codex-sync.exe')
& (Join-Path $install 'codex-sync.exe') --help
```

## Initialize and join

```powershell
codex-sync init https://github.com/OWNER/agent-history-sync-data.git
codex-sync push
```

On another PC:

```powershell
codex-sync join https://github.com/OWNER/agent-history-sync-data.git
codex-sync join https://github.com/OWNER/agent-history-sync-data.git --apply
codex-sync sync
```

Passphrases are prompted interactively and must never be placed on the command line.

## Agent and recovery

```powershell
codex-sync agent install
codex-sync status
codex-sync doctor
codex-sync conflicts
codex-sync resolve CONFLICT_ID --export-both C:\Recovery\new-directory
codex-sync resolve CONFLICT_ID --keep-local
codex-sync resolve CONFLICT_ID --keep-remote
codex-sync agent uninstall
```

Read [operations](docs/operations.md), [security](docs/security.md), and [compatibility](docs/compatibility.md).

## Uninstall

`codex-sync agent uninstall`, then remove the executable. Deleting `%LOCALAPPDATA%\CodexHistorySync` removes keys, config, conflicts, and backups.
