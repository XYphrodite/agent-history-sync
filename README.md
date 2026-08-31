# Agent History Sync

Windows 11 x64 CLI that synchronizes **Codex**, **Grok CLI**, **Claude Code**, and **Continue** conversation history through an encrypted private GitHub repository.

CLI для Windows 11 x64: синхронизация истории **Codex**, **Grok CLI**, **Claude Code** и **Continue** через зашифрованный private-репозиторий GitHub.

---

## English

> **Upgrading from 0.5.x with more than one machine:** update **every** machine before the first `push` that carries a Claude Code session. An older build rejects the whole encrypted index when it meets the new object kind, which breaks `pull` there entirely — not just for Claude. See [operations](docs/operations.md#upgrade-every-machine-before-the-first-claude-push).

### What it syncs

| Agent | Local path | What is uploaded |
|---|---|---|
| **Codex** | `%USERPROFILE%\.codex` | Active/archived session JSONL (size-normalized; no SQLite, auth, logs, or attachments) |
| **Grok CLI** | `%USERPROFILE%\.grok\sessions` | Per-session package: `chat_history` + `summary` (no `terminal/` logs) |
| **Claude Code** | `%USERPROFILE%\.claude\projects` | One transcript JSONL per session (nothing from `backups/`, `ide/`, `shell-snapshots/`, `session-env/`) |
| **Continue** | `%USERPROFILE%\.continue\sessions` | One session JSON plus its entry in the shared `sessions.json` (nothing from `config.yaml`, `config.ts`, `dev_data/`, `index/`) |

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
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1))) -Version v0.7.0 -AddToPath
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1))) -Version v0.7.0 -NoPath
```

**From a local build:**

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexHistorySync'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\agent-sync.exe (Join-Path $install 'agent-sync.exe')
& (Join-Path $install 'agent-sync.exe') --help
```

**Publish a new release** (maintainers): push an annotated tag `vX.Y.Z` (or run `.\scripts\publish-release.ps1 -Version X.Y.Z`). GitHub Actions builds `win-x64` and attaches `agent-sync.exe` + SHA-256.

### Update

```powershell
agent-sync --version          # what is installed
agent-sync update --check     # what is published
agent-sync update             # install the latest release
agent-sync update --version v0.7.0   # pin a tag, including an older one
```

`update` replaces the running `agent-sync.exe` in place. The download must match the release's SHA-256 asset, be a Windows executable, and answer `--help` before it is installed and again afterwards; any failure leaves the previous binary in place. The source repository is fixed in code and cannot be redirected. Close `--manage` and `--sessions` first, and note that a build older than 0.8.0 has no `update` command — install it once with `scripts/install.ps1`. See [operations](docs/operations.md#updating).

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
agent-sync update
agent-sync conflicts
agent-sync resolve CONFLICT_ID --keep-local    # or --keep-remote
agent-sync resolve CONFLICT_ID --export-both C:\Recovery\new-directory
```

If an active Codex or Grok process locks a session after scanning, `push`/`sync` publishes the remaining sessions and safely retries the locked session on a later run.

Manual `sync`, `push`, and `pull` print elapsed phase updates while they run. Codex/Grok and per-session reads are bounded-parallel; unchanged repositories authenticate the encrypted index and exact opaque-object set without loading every remote ciphertext body.

### Local session manager

```powershell
agent-sync --manage
```

This opens a session manager with one panel per installed agent (Codex, Grok, Claude, Continue) for local copy and local deletion. Panels wrap onto a second band when the terminal is too narrow for all of them side by side. It does not contact GitHub, Git, or the configured sync repository, and it does not change sync state. See [operations](docs/operations.md#local-cross-agent-session-manager) for the safety rules and controls.

### Session viewer

```powershell
agent-sync --sessions
```

One list of every session from every installed agent, with the selected conversation beside it. Scroll it, search inside it, export it to Markdown, and delete it. Also local-only. See [operations](docs/operations.md#session-viewer).

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
| **Claude Code** | `%USERPROFILE%\.claude\projects` | По одному JSONL-транскрипту на сессию (ничего из `backups/`, `ide/`, `shell-snapshots/`, `session-env/`) |
| **Continue** | `%USERPROFILE%\.continue\sessions` | JSON сессии плюс её запись в общем `sessions.json` (ничего из `config.yaml`, `config.ts`, `dev_data/`, `index/`) |

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
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1))) -Version v0.7.0 -AddToPath
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/agent-history-sync/main/scripts/install.ps1))) -Version v0.7.0 -NoPath
```

**Из локальной сборки:**

```powershell
$install = Join-Path $env:LOCALAPPDATA 'Programs\CodexHistorySync'
New-Item -ItemType Directory -Force $install | Out-Null
Copy-Item .\agent-sync.exe (Join-Path $install 'agent-sync.exe')
& (Join-Path $install 'agent-sync.exe') --help
```

**Новый релиз** (для maintainers): тег `vX.Y.Z` или `.\scripts\publish-release.ps1 -Version X.Y.Z` — Actions соберёт `win-x64` и приложит `agent-sync.exe` + SHA-256.

### Обновление

```powershell
agent-sync --version          # что установлено
agent-sync update --check     # что опубликовано
agent-sync update             # поставить последний релиз
agent-sync update --version v0.7.0   # закрепить тег, в том числе более старый
```

`update` заменяет запущенный `agent-sync.exe` на месте. Скачанный файл обязан совпасть с SHA-256 из релиза, быть Windows-исполняемым и ответить на `--help` до установки и ещё раз после; любая осечка оставляет прежний бинарь на месте. Репозиторий-источник зашит в коде и не переопределяется. Сначала закройте `--manage` и `--sessions`; сборка старше 0.8.0 команды `update` не знает — её ставят один раз через `scripts/install.ps1`. Подробности — в [operations](docs/operations.md#updating).

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
agent-sync update
agent-sync conflicts
agent-sync resolve CONFLICT_ID --keep-local    # или --keep-remote
agent-sync resolve CONFLICT_ID --export-both C:\Recovery\new-directory
```

Если активный процесс Codex или Grok блокирует сессию после сканирования, `push`/`sync` публикует остальные и безопасно повторит заблокированную сессию позже.

Ручные `sync`, `push` и `pull` печатают фазы с затраченным временем. Чтение Codex/Grok и тел сессий идёт ограниченным параллелизмом; для неизменённого репозитория проверяются зашифрованный индекс и набор opaque-объектов, без загрузки каждого ciphertext.

### Локальный менеджер сессий

```powershell
agent-sync --manage
```

Менеджер сессий с панелью на каждого установленного агента (Codex, Grok, Claude, Continue) для локального копирования и удаления. Если терминал узкий, панели переносятся на вторую полосу. Не обращается к GitHub/Git и не меняет состояние синхронизации. Правила и клавиши — в [operations](docs/operations.md#local-cross-agent-session-manager).

### Просмотр сессий

```powershell
agent-sync --sessions
```

Один список сессий всех установленных агентов и текст выбранной рядом. Прокрутка, поиск внутри сессии, экспорт в Markdown и удаление. Тоже только локально. Подробности — в [operations](docs/operations.md#session-viewer).

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
