# Agent History Sync

Windows 11 x64 CLI that synchronizes **Codex** and **Grok CLI** conversation history through an encrypted private GitHub repository.

CLI для Windows 11 x64: синхронизация истории **Codex** и **Grok CLI** через зашифрованный private-репозиторий GitHub.

---

## English

### What it syncs

| Agent | Local path | What is uploaded |
|---|---|---|
| **Codex** | `%USERPROFILE%\.codex` | Active/archived session JSONL (size-normalized; no SQLite, auth, logs, or attachments) |
| **Grok CLI** | `%USERPROFILE%\.grok\sessions` | Per-session package: `chat_history` + `summary` (no `terminal/` logs) |

Each successful publish rewrites `main` to a **single orphan commit** (snapshot store, not append-only history). Large tool outputs, compaction snapshots, and images are stripped or truncated before encrypt/upload. Local agent homes on disk are **not** modified.

Release binary name remains `codex-sync.exe` (stable install path). Prerequisites: Git, GitHub CLI (`gh`), and the agents you use.

### Repositories

| Repo | Purpose |
|---|---|
| [`XYphrodite/agent-history-sync`](https://github.com/XYphrodite/agent-history-sync) | Source code |
| [`XYphrodite/agent-history-sync-data`](https://github.com/XYphrodite/agent-history-sync-data) | Encrypted history data (**private**) |

### Install

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexHistorySync'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\codex-sync.exe (Join-Path $install 'codex-sync.exe')
& (Join-Path $install 'codex-sync.exe') --help
```

### Initialize and join

On the first PC (empty private data repo):

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

Passphrases are prompted interactively and must **never** be put on the command line.

### Everyday commands

```powershell
codex-sync status
codex-sync doctor
codex-sync push    # upload only
codex-sync pull    # download only
codex-sync sync    # bidirectional
codex-sync agent install
codex-sync agent uninstall
codex-sync conflicts
codex-sync resolve CONFLICT_ID --keep-local    # or --keep-remote
codex-sync resolve CONFLICT_ID --export-both C:\Recovery\new-directory
```

### Docs

- [operations](docs/operations.md)
- [security](docs/security.md)
- [compatibility](docs/compatibility.md)

### Uninstall

```powershell
codex-sync agent uninstall
# then delete the executable
```

Deleting `%LOCALAPPDATA%\CodexHistorySync` removes keys, config, conflict evidence, and backups — do this only after all devices are verified.

---

## Русский

### Что синхронизируется

| Агент | Локальный путь | Что уходит в cloud |
|---|---|---|
| **Codex** | `%USERPROFILE%\.codex` | JSONL активных/архивных сессий (сжатый вид; без SQLite, auth, логов, вложений) |
| **Grok CLI** | `%USERPROFILE%\.grok\sessions` | Пакет на сессию: `chat_history` + `summary` (без логов `terminal/`) |

Каждый успешный publish переписывает `main` в **один orphan-коммит** (хранилище-snapshot, не append-only история). Крупные tool-output’ы, compaction и картинки отбрасываются или обрезаются перед шифрованием. Локальные каталоги агентов **не меняются**.

Имя exe по-прежнему `codex-sync.exe` (стабильный путь установки). Нужны: Git, GitHub CLI (`gh`) и сами агенты.

### Репозитории

| Репозиторий | Назначение |
|---|---|
| [`XYphrodite/agent-history-sync`](https://github.com/XYphrodite/agent-history-sync) | Исходный код |
| [`XYphrodite/agent-history-sync-data`](https://github.com/XYphrodite/agent-history-sync-data) | Зашифрованные данные (**private**) |

### Установка

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexHistorySync'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\codex-sync.exe (Join-Path $install 'codex-sync.exe')
& (Join-Path $install 'codex-sync.exe') --help
```

### Init и join

Первый ПК (пустой private data-репо):

```powershell
codex-sync init https://github.com/OWNER/agent-history-sync-data.git
codex-sync push
```

Второй ПК:

```powershell
codex-sync join https://github.com/OWNER/agent-history-sync-data.git
codex-sync join https://github.com/OWNER/agent-history-sync-data.git --apply
codex-sync sync
```

Passphrase вводится интерактивно и **никогда** не передаётся аргументом CLI.

### Обычные команды

```powershell
codex-sync status
codex-sync doctor
codex-sync push    # только отдать
codex-sync pull    # только принять
codex-sync sync    # в обе стороны
codex-sync agent install
codex-sync agent uninstall
codex-sync conflicts
codex-sync resolve CONFLICT_ID --keep-local    # или --keep-remote
codex-sync resolve CONFLICT_ID --export-both C:\Recovery\new-directory
```

### Документация

- [operations](docs/operations.md) — эксплуатация  
- [security](docs/security.md) — угрозы и ключи  
- [compatibility](docs/compatibility.md) — gate совместимости Codex  

### Удаление

```powershell
codex-sync agent uninstall
# затем удалить exe
```

Удаление `%LOCALAPPDATA%\CodexHistorySync` стирает ключи, конфиг, конфликты и бэкапы — только после проверки всех устройств.
