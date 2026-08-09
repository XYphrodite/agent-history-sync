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

The public command is `agent-sync`, and the release binary is `agent-sync.exe`. Prerequisites: Git, GitHub CLI (`gh`), and the agents you use.

For compatibility with existing installations, the installer deliberately keeps `agent-sync.exe` under `%LOCALAPPDATA%\Programs\CodexHistorySync`, while application state remains under `%LOCALAPPDATA%\CodexHistorySync`. These are separate directories; upgrading does not migrate or rename either one.

### Repositories

| Repo | Purpose |
|---|---|
| [`XYphrodite/agent-history-sync`](https://github.com/XYphrodite/agent-history-sync) | Source code |
| [`XYphrodite/agent-history-sync-data`](https://github.com/XYphrodite/agent-history-sync-data) | Encrypted history data (**private**) |

### Install

**From GitHub Releases (recommended):**

```powershell
# Latest release → %LOCALAPPDATA%\Programs\CodexHistorySync\agent-sync.exe
# Asks interactively whether to add the install dir to your user PATH.
irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1 | iex
```

```powershell
# Pin a version; force PATH yes/no without prompting
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1))) -Version v0.3.0 -AddToPath
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1))) -Version v0.3.0 -NoPath
```

**From a local build:**

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexHistorySync'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\agent-sync.exe (Join-Path $install 'agent-sync.exe')
& (Join-Path $install 'agent-sync.exe') --help
```

**Publish a new release** (maintainers): push an annotated tag `vX.Y.Z` (or run `.\scripts\publish-release.ps1 -Version X.Y.Z`). GitHub Actions builds `win-x64` and attaches `agent-sync.exe` + SHA-256.

### Initialize and join

On the first PC (empty private data repo):

```powershell
agent-sync init https://github.com/OWNER/agent-history-sync-data.git
agent-sync push
```

On another PC:

```powershell
agent-sync join https://github.com/OWNER/agent-history-sync-data.git
agent-sync join https://github.com/OWNER/agent-history-sync-data.git --apply
agent-sync sync
```

Passphrases are prompted interactively and must **never** be put on the command line.

### Everyday commands

```powershell
agent-sync status
agent-sync doctor
agent-sync push    # upload only
agent-sync pull    # download only
agent-sync sync    # bidirectional
agent-sync agent install
agent-sync agent uninstall
agent-sync conflicts
agent-sync resolve CONFLICT_ID --keep-local    # or --keep-remote
agent-sync resolve CONFLICT_ID --export-both C:\Recovery\new-directory
```

### Docs

- [operations](docs/operations.md)
- [security](docs/security.md)
- [compatibility](docs/compatibility.md)

### Uninstall

```powershell
agent-sync agent uninstall
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

Публичная команда называется `agent-sync`, а релизный файл — `agent-sync.exe`. Нужны: Git, GitHub CLI (`gh`) и сами агенты.

Для совместимости с существующими установками `agent-sync.exe` намеренно остаётся в `%LOCALAPPDATA%\Programs\CodexHistorySync`, а данные приложения — в `%LOCALAPPDATA%\CodexHistorySync`. Это разные каталоги; при обновлении ни один из них не переносится и не переименовывается.

### Репозитории

| Репозиторий | Назначение |
|---|---|
| [`XYphrodite/agent-history-sync`](https://github.com/XYphrodite/agent-history-sync) | Исходный код |
| [`XYphrodite/agent-history-sync-data`](https://github.com/XYphrodite/agent-history-sync-data) | Зашифрованные данные (**private**) |

### Установка

**Из GitHub Releases (рекомендуется):**

```powershell
# Последний релиз → %LOCALAPPDATA%\Programs\CodexHistorySync\agent-sync.exe
# Спросит, добавлять ли каталог в user PATH.
irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1 | iex
```

```powershell
# Конкретная версия; PATH без вопроса
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1))) -Version v0.3.0 -AddToPath
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1))) -Version v0.3.0 -NoPath
```

**Из локальной сборки:**

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexHistorySync'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\agent-sync.exe (Join-Path $install 'agent-sync.exe')
& (Join-Path $install 'agent-sync.exe') --help
```

**Новый релиз** (для maintainers): тег `vX.Y.Z` или `.\scripts\publish-release.ps1 -Version X.Y.Z` — Actions соберёт `win-x64` и приложит `agent-sync.exe` + SHA-256.

### Init и join

Первый ПК (пустой private data-репо):

```powershell
agent-sync init https://github.com/OWNER/agent-history-sync-data.git
agent-sync push
```

Второй ПК:

```powershell
agent-sync join https://github.com/OWNER/agent-history-sync-data.git
agent-sync join https://github.com/OWNER/agent-history-sync-data.git --apply
agent-sync sync
```

Passphrase вводится интерактивно и **никогда** не передаётся аргументом CLI.

### Обычные команды

```powershell
agent-sync status
agent-sync doctor
agent-sync push    # только отдать
agent-sync pull    # только принять
agent-sync sync    # в обе стороны
agent-sync agent install
agent-sync agent uninstall
agent-sync conflicts
agent-sync resolve CONFLICT_ID --keep-local    # или --keep-remote
agent-sync resolve CONFLICT_ID --export-both C:\Recovery\new-directory
```

### Документация

- [operations](docs/operations.md) — эксплуатация  
- [security](docs/security.md) — угрозы и ключи  
- [compatibility](docs/compatibility.md) — gate совместимости Codex  

### Удаление

```powershell
agent-sync agent uninstall
# затем удалить exe
```

Удаление `%LOCALAPPDATA%\CodexHistorySync` стирает ключи, конфиг, конфликты и бэкапы — только после проверки всех устройств.
