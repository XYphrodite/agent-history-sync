# Профиль для каждой команды

Выбирайте подключение и локальные папки на время одного запуска:

```powershell
agent-sync sync --profile xeon
agent-sync pull --profile reader
agent-sync --sessions --profile reader
agent-sync search "нужная фраза" --profile reader
```

Флаг можно поставить перед командой: `agent-sync --profile xeon status`.
Поддерживается и `--profile=xeon`. Для выбора стрелками из списка используйте
`agent-sync sync --select-profile`. Меню требует интерактивный терминал;
в скриптах и настройках MCP указывайте имя. Неизвестное имя завершает команду
с ошибкой, не переключая её на другой профиль.

Без флага сохраняется прежнее поведение. Имя `default` обозначает те же настройки,
которые использует обычная команда без выбора профиля. Постоянный активный профиль
не устанавливается.

## Регистрация существующих подключений

Если подключение к серверу уже настроено в отдельном каталоге:

```powershell
agent-sync profile add xeon --data-dir "$env:USERPROFILE\agent-sync-xeon"
agent-sync status --profile xeon
```

`--data-dir` указывает на каталог, содержащий `CodexHistorySync\config.json`,
`CodexHistorySync\keys` и состояние синхронизации. Регистрация сохраняет ссылку
на этот каталог. Она не копирует ключи, не переносит историю и не выполняет
`init`/`join`. Пароль остаётся необходимым при первом подключении через `init` или `join`.

Чтобы дать имя нынешнему подключению:

```powershell
agent-sync profile add github --data-dir "$env:LOCALAPPDATA"
```

Список и сведения о профиле:

```powershell
agent-sync profile list
agent-sync profile show xeon
```

Имена состоят из 1–48 латинских букв, цифр, `-` и `_`, начинаются с буквы или цифры
и не зависят от регистра. Повторное добавление имени не заменяет его настройки.
`agent-sync profile remove xeon` удаляет только регистрацию имени; файлы, ключи,
конфликты и резервные копии сохраняются.

## Новое подключение

```powershell
agent-sync profile add xeon
agent-sync init http://100.101.102.103:8080/v1/repositories/personal --profile xeon
agent-sync push --profile xeon
```

Без `--data-dir` настройки нового профиля размещаются в
`%LOCALAPPDATA%\CodexHistorySync\profiles\<name>\CodexHistorySync`.
Для уже существующего серверного репозитория вместо `init` используйте
`join <url> --apply --profile xeon` и его пароль шифрования.

## Отдельная копия для чтения

Отдельные настройки синхронизации сами по себе не отделяют историю агентов.
Без `--sessions-dir` используются обычные папки Codex, Grok, Claude, Continue и Kimi,
включая их переопределения через переменные окружения.

Для отдельной копии укажите общий каталог её агентских папок:

```powershell
agent-sync profile add reader --data-dir "$env:USERPROFILE\agent-sync-reader\profile" --sessions-dir "$env:USERPROFILE\agent-sync-reader\agents"
agent-sync join http://100.101.102.103:8080/v1/repositories/personal --apply --profile reader
agent-sync --sessions --profile reader
```

Если копия уже подключена, повторный `join` не нужен. Для обновления:

```powershell
agent-sync pull --profile reader
agent-sync --sessions --profile reader
```

Внутри `--sessions-dir` используются `codex\sessions`, `codex\archived_sessions`,
`grok\sessions`, `claude\projects`, `continue\sessions` и `kimi\sessions`. При первом запуске
синхронизации эти каталоги создаются. Каталог SQLite, аннотации, настройки
названий и диагностика используют `--data-dir` выбранного профиля.
Профиль задаёт папки и подключение; он не вводит отдельные права доступа на сервере.

## Хранение и автоматизация

Реестр имён находится в `%LOCALAPPDATA%\CodexHistorySync\profiles.json`.
Он читается **до** выбора профиля. Переопределения применяются только внутри
запущенного процесса и не меняют переменные Windows или родительского PowerShell.
Если ранее вручную меняли `$env:LOCALAPPDATA`, начните настройку имён в новом окне
PowerShell, чтобы использовать обычный реестр.

Для MCP добавьте аргументы `--profile`, `reader` после `mcp`.
Выбор профиля не добавляет текст в протокол stdio.

Фоновый процесс можно запустить явно: `agent-sync agent run --profile xeon`.
Установка и удаление задач планировщика с именованным профилем пока отклоняются:
существующая задача запускается с прежними настройками, без выбранного профиля.

Для поиска текста, который сам содержит глобальный флаг, используйте разделитель:
`agent-sync --profile reader search -- --profile`.
