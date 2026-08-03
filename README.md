# Codex History Sync

Codex History Sync is a Windows 11 x64 command-line utility that synchronizes active and archived Codex JSONL sessions through an encrypted private GitHub repository. It does not copy Codex SQLite state, credentials, logs, caches, sandbox data, or attachments.

The release is one self-contained file, `codex-sync.exe`; a separate .NET installation is not required. Git, GitHub CLI (`gh`), and Codex are still prerequisites.

## Install

Download the `win-x64` release artifact, verify its published hash, and place it in a per-user directory:

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexHistorySync'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\codex-sync.exe (Join-Path $install 'codex-sync.exe')
& (Join-Path $install 'codex-sync.exe') --help
```

Run commands with the full path or add that directory to the current user's `PATH`. Moving the executable after agent installation makes the exact scheduled-task ownership check fail; uninstall the agent before moving it, then install it again.

## Initialize and join

Create an empty private GitHub repository, authenticate `gh`, then initialize it from the first Windows profile:

```powershell
codex-sync init https://github.com/OWNER/REPOSITORY.git
codex-sync push
```

On another profile, preview first and apply only after reviewing the authenticated counts:

```powershell
codex-sync join https://github.com/OWNER/REPOSITORY.git
codex-sync join https://github.com/OWNER/REPOSITORY.git --apply
codex-sync sync
```

Setup fails closed unless GitHub private visibility can be verified. Passphrases are prompted interactively and must never be placed on the command line.

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

The per-user logon task runs the same synchronization engine as `sync`, `pull`, and `push`. It never replaces history while Codex is running. `agent uninstall` removes only the exact task owned by the current executable and user.

Read [operations](docs/operations.md) before restoring a backup or resolving a conflict, [security](docs/security.md) for the trust boundary and key limits, and [compatibility](docs/compatibility.md) for the disposable Codex discovery gate.

## Uninstall

First run `codex-sync agent uninstall`, then delete the installed executable. Removing `%LOCALAPPDATA%\CodexHistorySync` is optional and destructive: it deletes the DPAPI key cache, configuration, encrypted conflict evidence, and local backups. Keep it until every device and recovery copy has been verified.
