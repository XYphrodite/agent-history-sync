# Серверное хранилище (slice 3)

Windows-клиент может хранить зашифрованную историю в PostgreSQL на своём Linux-сервере. `catalog.db`, поиск и MCP остаются локальными. Сервер не получает пароль шифрования, ключи, тексты сообщений или парсеры агентов. Git/GitHub для этого backend не нужны; старый Git-backend сохранён.

Это новый backend, **не автоматическая миграция** действующего GitHub-хранилища. `init`/`join` не заменяют другой настроенный репозиторий. Используйте ещё не подключённый профиль; не удаляйте нынешнюю конфигурацию ради переключения. Повторный `join` того же хранилища сохраняет device ID и baseline.

## Граница доверия: Tailscale, без API-токена

API не требует `Authorization` и не проверяет пользователя. Любой процесс, который может обратиться к нему, может читать ciphertext и менять снимки всех репозиториев. Шифрование защищает содержимое, но не предотвращает удаление или порчу данных доверенным сетевым клиентом.

- Разрешайте порт API только нужным устройствам через Tailscale ACL/grants. Начните с loopback, проверьте правила, затем открывайте tailnet IP.
- Не используйте `0.0.0.0`, LAN/public IP, проброс портов маршрутизатора или Tailscale Funnel. Не подключайте посторонние контейнеры к сетям этого Compose.
- Проверка адресного диапазона клиентом **не доказывает**, что работает Tailscale. Проверяйте реальную маршрутизацию, host firewall и правила Docker, включая forwarded traffic. Не полагайтесь только на UFW.
- Процессы на самом сервере, администраторы Docker и контейнеры его сетей входят в границу доверия. Здесь нет многопользовательского разграничения по репозиториям.
- API отклоняет `Origin`, браузерный `Sec-Fetch-Site`, посторонний `Host` и неверный Content-Type; CORS не включён. Это дополнительная защита, не замена сетевой изоляции.

Перед реальными данными проверьте, что разрешённое устройство подключается, а LAN, интернет и неразрешённое устройство tailnet — нет. Локальная проверка Docker этого не заменяет. [Адреса Tailscale](https://tailscale.com/docs/reference/reserved-ip-addresses), [правила доступа](https://tailscale.com/docs/features/access-control), [Docker и firewall](https://docs.docker.com/engine/network/packet-filtering-firewalls/).

## Запуск на Linux / Xeon

Нужны Docker Engine с Compose v2, работающий Tailscale на хосте и checkout исходного кода. Из корня checkout:

```sh
mkdir -p .secrets
chmod 700 .secrets
# Не перезаписывайте существующий пароль при обновлениях!
test ! -e .secrets/postgres_password && openssl rand -base64 36 > .secrets/postgres_password
chmod 444 .secrets/postgres_password
docker compose -f compose.server.yaml config --quiet
docker compose -f compose.server.yaml up -d --build --wait
curl --fail http://127.0.0.1:8080/readyz
```

Это **пароль PostgreSQL**, не токен API и не пароль шифрования. Родительский каталог `0700` закрывает доступ другим пользователям хоста; файл `0444` нужен разным непривилегированным UID в контейнерах. Compose монтирует его read-only только в API и PostgreSQL. Права исходного файла важны: для file-backed Compose secrets нельзя полагаться на `uid`/`gid`/`mode` как на замену host permissions. Не помещайте секрет в Git, Docker-образ, shell history или публичный `.env`. Путь можно задать через `AGENT_SYNC_DB_PASSWORD_FILE`. [Compose secrets](https://docs.docker.com/reference/compose-file/services/#secrets).

По умолчанию порт API привязан к `127.0.0.1:8080`. PostgreSQL вообще не публикуется; у него отдельная internal-сеть и постоянный volume. API также подключён к сети для публикации своего порта; работает под UID 1654, с read-only rootfs, `/tmp` в tmpfs, без capabilities и Docker socket. Никакие каталоги истории агентов не монтируются.

После проверки ACL/firewall задайте **реальный IPv4 Tailscale Xeon**, например:

```sh
export AGENT_SYNC_BIND_IP=100.101.102.103
docker compose -f compose.server.yaml up -d --wait
docker compose -f compose.server.yaml ps
```

Сохраните выбранный адрес в защищённом локальном `.env` или окружении запуска, иначе следующий запуск вернётся к loopback. Это не секрет. Если Tailscale ещё не поднял интерфейс, публикация порта должна завершиться ошибкой. Не подменяйте адрес wildcard-привязкой.

Прямой HTTP допускается только на loopback или IP Tailscale: IPv4 `100.64.0.0/10`, IPv6 `fd7a:115c:a1e0::/48`. Имена `*.ts.net` допускаются только с HTTPS. Для HTTPS нужен отдельно настроенный приватный reverse proxy/Serve без Funnel и явное имя в `Storage__AllowedHosts`; автоматической настройки TLS нет. Compose-пример использует IPv4. Не пересылайте browser Origin/CORS и не превращайте proxy в публичный вход.

## Подключение новых Windows-профилей

```powershell
# Первый ПК: создаёт новый репозиторий, пароль шифрования вводится скрыто.
agent-sync init http://100.101.102.103:8080/v1/repositories/personal
agent-sync push

# Второй ПК: тот же URL и тот же пароль шифрования.
agent-sync join http://100.101.102.103:8080/v1/repositories/personal --apply
agent-sync sync
agent-sync status
agent-sync doctor
```

Допустимое имя: 1–64 символа `a-z`, `0-9`, `-`. URL не может содержать credentials, query или fragment. Клиент не следует редиректам и не использует системный HTTP proxy. `doctor` проверяет `server-access` вместо Git/`gh`; доступность сервера не означает успешную проверку ACL.

В фоновой синхронизации используется тот же backend, выбранный URL из локальной конфигурации. Секрет API не создаётся. Локальный ключ по-прежнему защищён Windows DPAPI. Не передавайте пароль шифрования в аргументах процесса.

Для Windows-хоста с Docker Desktop доступны [скрипты запуска и резервного копирования](../scripts/server/README.md). Сервер и PostgreSQL при этом работают в Linux-контейнерах. Учтите зависимость запуска Docker Desktop от входа пользователя Windows.

## Протокол и лимиты

`GET /v1/info` объявляет protocolVersion=1. `GET /healthz` — процесс работает, `/readyz` — доступна поддерживаемая схема PostgreSQL. Несовместимая схема останавливает запуск; миграция v1 выполняется транзакционно под advisory lock.

Относительно `/v1/repositories/<name>`:

| Метод и путь | Назначение |
|---|---|
| `PUT` | Создать manifest, зашифрованный индекс и revision, только если имени ещё нет |
| `GET /setup` | Прочитать manifest, индекс и revision вместе |
| `GET /snapshot` | Прочитать индекс, revision и закреплённые ссылки `objectId → ciphertext SHA-256` |
| `PUT /blobs/<sha256>` | Идемпотентно загрузить immutable ciphertext |
| `GET /blobs/<sha256>` | Прочитать ciphertext; клиент проверяет хеш и аутентификацию шифрования |
| `POST /publish` | Атомарный CAS: expectedRevision, новый индекс при необходимости, изменения ссылок |

Manifest содержит только версию формата, случайный repository ID, параметры Argon2id и authenticator. Индекс, сообщения, аннотации, память и tombstones зашифрованы CHS1. У старого CHS1 открыт аутентифицированный заголовок с native logical ID и видом объекта; у памяти Claude этот ID содержит имя файла и hex-кодированный проект. Поэтому HTTP-провайдер дополнительно шифрует **весь** объектный CHS1-конверт ключом, полученным через HKDF с отдельной меткой `agent-sync/http-blob-envelope/v1`. Внешний заголовок содержит только opaque ID и фиксированный generic kind (`Attachment`), а не native ID/путь/агента. Это не добавляет синхронизацию вложений; значение используется только как метка внешнего конверта. Форматы Core/Git не меняются. Заголовок индекса содержит только фиксированное `__repository_index__` и не требует этого дополнительного слоя.

Сервер видит размеры, время операций, имена репозиториев, revisions, opaque IDs, ciphertext hashes и общие технические поля внешнего CHS1. Он проверяет форму конверта и SHA-256, но без ключа не может аутентифицировать содержимое. Дополнительное шифрование означает, что SHA-256 blob и opaque ID исходного объекта различаются; связь хранится в закреплённом снимке. Лимит 100 МиБ относится к итоговому внешнему ciphertext.

Одновременные публикации сериализуются row lock: один CAS выигрывает, второй получает 409 и текущий revision. Ошибка внешнего ключа, лимита или отмена транзакции не оставляет частичного индекса/набора ссылок. Снимок читается в repeatable-read транзакции. Объекты закреплены хешами, а не изменяемыми адресами.

| Ограничение | Значение |
|---|---|
| Manifest | 64 КиБ |
| Зашифрованный индекс | 16 МиБ |
| Ciphertext одного blob | 100 МиБ |
| JSON-тело, включая base64 | 64 МиБ |
| Ссылки в снимке / изменения за запрос | 100 000 |
| Одновременные storage-запросы на процесс | 2; остальные получают 429 |
| Время обработки запроса | до 5 минут; SQL-команда до 4 минут; клиентский timeout 10 минут |

Ответы ограничены и на клиенте; превышение не обрезается молча. Размеры ciphertext не равны plaintext. Существующая клиентская нормализация/ограничение 95 МиБ для исходного объекта пока сохраняется и для HTTP. При 429/503 или разрыве соединения повторите синхронизацию после устранения причины. Потеря ответа после commit означает неопределённый исход запроса; следующий sync перечитает authoritative snapshot, а не будет считать локальный baseline обновлённым.

Лимиты памяти Compose — отправная точка: 2 ГиБ API, 1 ГиБ PostgreSQL, по 2 CPU. Запросы буферизуются с жёсткими пределами; это не потоковое хранилище неограниченных файлов. Следите за OOM, нагрузкой и диском на конкретном Xeon. Нагрузка с двумя blob по 100 МиБ проверяется отдельным тестом; это не измерение производительности вашего сервера.

## Обновление и резервные копии

Образы PostgreSQL и .NET закреплены digest в Compose/Dockerfile. При обновлении зависимостей пересоберите и перепроверьте образы; их фиксация не заменяет security updates. Версия протокола отделена от версии CLI.

```sh
# В каталоге checkout, с теми же .env/секретом/именем Compose-проекта.
mkdir -p backups
chmod 700 backups
umask 077
docker compose -f compose.server.yaml exec -T postgres \
  pg_dump -U agent_sync -d agent_sync -Fc > backups/agent-sync.dump
# Скопируйте backup на другое доверенное устройство.
docker compose -f compose.server.yaml up -d --build --wait
curl --fail http://127.0.0.1:8080/readyz
```

Выполняйте бинарное перенаправление в Linux shell; Windows PowerShell 5 может испортить бинарный dump. Для Windows используйте `pg_dump -f /tmp/backup.dump` внутри контейнера и `docker cp`. `pg_dump` даёт согласованный backup работающей БД. Не копируйте live PostgreSQL data directory как обычную папку. Backup защищайте как историю, даже если содержимое зашифровано. Для восстановления нужен также отдельно сохранённый пароль клиентского шифрования; одной БД недостаточно.

Никогда не выполняйте `docker compose down -v` на рабочем проекте. Для остановки достаточно `stop`, для обновления — `up -d --build`. Автоматического удаления старых/unreferenced blobs **нет**: они сохраняют читаемость закреплённых снимков, но увеличивают объём БД. Это не доступная пользователю история версий. Ручной DELETE из blobs без понимания ссылок/читателей не поддерживается.

Восстановление сначала проверяйте в **новом** PostgreSQL volume и отдельном Compose-проекте, с другим loopback-портом API. До запуска API загрузите dump в пустую БД:

```sh
# Только для отдельного тестового проекта agent-sync-restore, НЕ рабочего.
docker compose -p agent-sync-restore -f compose.server.yaml up -d postgres
docker compose -p agent-sync-restore -f compose.server.yaml exec -T postgres \
  pg_restore -U agent_sync -d agent_sync --no-owner --exit-on-error < backups/agent-sync.dump
# Затем запустите API с override на свободный loopback-порт, проверьте readyz и расшифровку клиентом.
```

Отдельный проект создаёт отдельный volume. Не запускайте второй API на уже занятом порту. Пример не содержит `--clean`, `DROP DATABASE` или удаления исходного volume. После восстановления сравните число объектов, revision, хеши и прочитайте сессии тестовым клиентом. Настоящее переключение клиентов — отдельная операция, которую этот slice не выполняет.

## Локальные тесты разработчика

Обычные тесты URL/протокола выполняются без БД. PostgreSQL-тесты явно пропускаются без `AGENT_SYNC_TEST_POSTGRES`, а не подменяются mock/in-memory БД. Перед полным regression suite соберите release CLI — часть существующих тестов запускает опубликованный executable.

```powershell
docker run -d --name agent-sync-test-postgres -p 127.0.0.1:55433:5432 `
  -e POSTGRES_PASSWORD=disposable-test-password -e POSTGRES_DB=agent_sync_test postgres:17-bookworm
$env:AGENT_SYNC_TEST_POSTGRES='Host=127.0.0.1;Port=55433;Database=agent_sync_test;Username=postgres;Password=disposable-test-password'
dotnet publish src/CodexHistorySync.Cli -c Release
dotnet test CodexHistorySync.sln
```

Используйте только одноразовую тестовую БД: fixture создаёт уникальную схему и удаляет **только её** после тестов. Два клиентских профиля создаются во временных каталогах; ваша установленная конфигурация и история не используются.

Чтобы проверить два Windows-профиля через API, уже запущенный в Linux Docker (например, на Xeon), задайте `AGENT_SYNC_SYNC_DRILL_URL` на **новый одноразовый** репозиторий:

```powershell
# Замените IP и порт адресом своего тестового API.
$env:AGENT_SYNC_SYNC_DRILL_URL = 'http://100.101.102.103:8080/v1/repositories/sync-drill-' + [guid]::NewGuid().ToString('N')
dotnet test tests/CodexHistorySync.Server.Tests -c Release --filter FullyQualifiedName~ContainerTwoClientsConvergeForAllScannableKinds
Remove-Item Env:AGENT_SYNC_SYNC_DRILL_URL
```

Этот тест выполняет init/push/join/sync для семи видов данных, проверяет обратную синхронизацию, конфликты и удаления. Оба профиля временные, Git не вызывается. Сервер и PostgreSQL работают по указанному адресу; локальный API не поднимается, `AGENT_SYNC_TEST_POSTGRES` не нужен. Существующий репозиторий тест использовать откажется. Зашифрованные синтетические данные остаются в тестовом сервере для проверки backup/restore; удаляйте их вместе с отдельным тестовым Compose-проектом после завершения проверки. Без переменной этот дополнительный тест явно пропускается.

После восстановления такого репозитория в отдельную БД задайте `AGENT_SYNC_RESTORE_SYNC_DRILL_URL` на его URL в восстановленном API и запустите `dotnet test tests/CodexHistorySync.Server.Tests -c Release --filter FullyQualifiedName~ContainerRestoredRepositoryImportsIntoFreshProfile`. Проверка подключает новый временный профиль, расшифровывает сессии, память и аннотации, проверяет сохранение удаления архивной сессии и отсутствие ожидающих изменений. Используйте только репозиторий, созданный предыдущим синтетическим тестом; его тестовый пароль фиксирован в коде.

Опциональный `ContainerDrillTests` проверяет настоящий Linux-контейнер. Задайте `AGENT_SYNC_DRILL_URL` на **новый одноразовый** репозиторий, например `http://127.0.0.1:58080/v1/repositories/backup-drill`, и `AGENT_SYNC_DRILL_PHASE=seed`; запустите `dotnet test tests/CodexHistorySync.Server.Tests --filter FullyQualifiedName~ContainerDrillTests`. Затем сделайте backup, перезапустите API/БД и восстановите dump в отдельный volume. Повторите тест с `AGENT_SYNC_DRILL_PHASE=verify` и URL восстановленного API. Он проверяет manifest, индекс, SHA-256 и расшифровку blob, а seed отказывается заменять существующий репозиторий. Пароль/сообщение в этом тесте синтетические, для реального хранилища его использовать нельзя.

Для нагрузки в том же одноразовом контейнере добавьте `AGENT_SYNC_DRILL_LARGE=1`: тест одновременно загрузит два одинаковых объекта по 100 МиБ и прочитает результат. Загрузка оставляет один дополнительный unreferenced blob в тестовой БД; после проверки удаляется весь одноразовый проект, а не данные рабочего хранилища.

## Результаты локальной проверки 2026-09-09

- Полный suite: **1176 passed, 0 failed, 0 skipped** — Core 622, Windows 38, Git 18, Integration 462, Server 36. Серверные сценарии выполнялись на реальном PostgreSQL, не mock.
- Два изолированных клиентских профиля: init/join/sync, все семь сканируемых видов данных, конфликтующие правки, tombstones, неправильный пароль и запрет случайной смены backend. Git factory в HTTP-сценарии намеренно бросает исключение при вызове.
- Проверены бинарные заголовки объектов в БД: внешний конверт не содержит native ID/типа агента/пути памяти Claude. Проверены хеши, неверный ключ, отказ импорта повреждённых данных без продвижения baseline, закреплённые снимки после замены и удаления ссылок.
- PostgreSQL/API: гонки init и CAS, rollback при отсутствующем blob, отмена, потерянный ответ, 100 000 ссылок без усечения, oversized chunked-тела, Origin/Host/Content-Type, предел параллелизма. Тест параллелизма дополнительно повторён пять раз.
- Windows single-file CLI и Linux Docker-образ собраны. Compose проверен с UID 1654, read-only API, секретом БД, непубликуемым PostgreSQL и loopback API.
- Два одновременных upload по 100 МиБ и последующий download прошли внутри API с лимитом 2 ГиБ/2 CPU; максимальный наблюдавшийся `memory.peak` после повторных прогонов — около 889 МиБ, OOM не было. Это измерение локального Docker Desktop, не прогноз для Xeon.
- Остановка тестовой БД: readiness 503, liveness 200; после перезапуска тот же снимок читается. `pg_dump`/`pg_restore` в отдельный volume проверен расшифровкой синтетического объекта; сообщение отсутствует в SQL dump, API-логи не содержат payload/секретов.

TRX последнего прогона находятся в локальном `artifacts/slice3-final-results/`, тестовый dump — `artifacts/server-drill/backup.dump` (оба пути исключены из Git). Тестовые данные одноразовые. Реальные сессии, установленный CLI и его GitHub-хранилище не переносились; deploy на Xeon и проверка LAN/интернет/ACL Tailscale не выполнялись. Коммит, push и релиз требуют отдельного запроса.

Последующая проверка на самом Xeon выполнена 2026-09-13: [результаты и границы сетевой проверки](xeon-test-2026-09-13.md). Это отдельный тестовый запуск, не постоянное развёртывание.
