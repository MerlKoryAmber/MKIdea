# LinuxOps Web — интеграция с API

API только читает. Внешний сервис запускает на хостах Linux встроенные действия, которые собирают информацию, и забирает результат. Действия, которые что-то меняют, через API не видны и не запускаются.

- Адрес: `https://<сервер LinuxOps Web>/api/v1`.
- Машинное описание (OpenAPI 3.0) — `GET /api/v1/openapi.json`, без токена. Файл в репозитории: `src/LinuxOps.Web/wwwroot/api/openapi.json`.
- Код: `src/LinuxOps.Web/Services/Api/` — `ApiEndpoints.cs` (маршруты и вход), `ApiJobs.cs` (запуск и лимиты).

## 1. Подготовка в панели (делает «Админ»)

1. ⚙ → «Сервисы API» → имя (латиница, цифры, `. _ -`; например `svc-siem`) и назначение → «Завести и выдать токен».
2. Токен `lo_…` показывается **один раз**. Положите его в хранилище секретов сервиса. В LinuxOps хранится только хэш, восстановить токен нельзя — только выпустить новый.
3. ⚙ → «Назначения» → «сервису API» → учётка → роль на хост или папку. Нужны два права:
   - «Видеть хосты» — хост попадает в `/hosts`;
   - «Опрос и Dry-run» — на хосте можно запускать действия.
4. Ключ SSH каждого хоста должен быть уже подтверждён в панели. Через API подтвердить его некому — хост с новым ключом не проверится.

Управление учёткой там же:
- «Новый токен» — старый перестаёт работать сразу;
- «Выключить» — все запросы получают `401`;
- «Удалить» — снимаются и её назначения.

## 2. Вход

Каждый запрос — с заголовком `Authorization: Bearer lo_…`. Cookie панели не принимаются.

Всё, что делает учётка, пишется в History и аудит от имени `api:<учётка> (<IP>)`. Там же — отказы, но не чаще раза в минуту с одного адреса или от одной учётки.

## 3. Порядок работы

```
GET  /actions        что можно запустить
GET  /hosts          где можно запустить (id хостов)
POST /jobs           запустить → 202 и id задания
GET  /jobs/{id}      опрашивать, пока status = running; потом — результаты
```

### curl

```bash
T='lo_…'; U='https://linuxops-web/api/v1'
curl -s -H "Authorization: Bearer $T" "$U/actions"
curl -s -H "Authorization: Bearer $T" "$U/hosts"
curl -s -H "Authorization: Bearer $T" -H 'Content-Type: application/json' \
     -d '{"action":"versions-check","hosts":["<id хоста>"],"params":{"UPD_CVE":"да"}}' "$U/jobs"
curl -s -H "Authorization: Bearer $T" "$U/jobs/<id задания>"
```

### PowerShell

```powershell
$h = @{ Authorization = "Bearer $env:LINUXOPS_TOKEN" }
$u = 'https://linuxops-web/api/v1'
$hosts = Invoke-RestMethod "$u/hosts" -Headers $h | Where-Object can_run
$body = @{ action = 'versions-check'; hosts = @($hosts.id) } | ConvertTo-Json
$job = Invoke-RestMethod "$u/jobs" -Method Post -Headers $h -ContentType 'application/json; charset=utf-8' -Body $body
do { Start-Sleep 10; $job = Invoke-RestMethod "$u/jobs/$($job.id)" -Headers $h } while ($job.status -eq 'running')
$job.results | Select-Object host, status, summary
```

Опрашивать раз в 5–15 секунд. Время действия ограничено его `timeout_seconds` (до 300 с у «Версий ПО»).

## 4. Методы

### `GET /actions`

Действия, доступные через API. Сейчас их четыре:

| id | Что | Параметры |
|---|---|---|
| `check-updates` | доступные обновления пакетов (apt / dnf / zypper) | — |
| `list-users` | аудит учёток: sudo, блокировки, ключи SSH | — |
| `proxy-check` | прокси пакетного менеджера и окружения, доступен ли репозиторий | — |
| `versions-check` | версии nginx, PHP, OpenSSL, OpenSSH, СУБД и др. на хосте и в контейнерах; обновления пакетов; сверка CVE | `UPD_CVE` — `да` / `нет`, по умолчанию `да` |

Поля: `id`, `name`, `description`, `distros`, `timeout_seconds`, `params[]` (`name`, `label`, `kind` — `bool` или `text`, `default`). Значения `bool` — строки `да` / `нет`.

Параметр `UPD_VIA_PC` (туннель в интернет через сервер для хостов без выхода) через API закрыт. Его включает только человек в панели, по согласованию с ИБ.

### `GET /hosts`

Хосты Linux, которые видит учётка: `id`, `name`, `address`, `port`, `folder` (`Отдел А / Отдел Б`), `can_run` — есть ли право запускать.

### `POST /jobs`

```json
{ "action": "versions-check", "hosts": ["<id>", "<id>"], "params": { "UPD_CVE": "нет" } }
```

- `hosts` — от 1 до 50 id из `/hosts`.
- `params` — только объявленные у действия. Чего нет — берётся значение по умолчанию.
- Ответ `202` — задание без результатов, `status: running`.
- Запуск всегда в режиме опроса (dry-run): `apply` в API нет.

### `GET /jobs`

До 50 последних заданий учётки, свежие сверху, без результатов. Задания хранятся 7 дней.

### `GET /jobs/{id}`

Задание с результатами. Чужое задание — `404`, как несуществующее.

```json
{
  "id": "4f1bc53c…", "client": "svc-siem", "action": "versions-check", "action_name": "Версии ПО: nginx, PHP… и контейнеры",
  "params": { "UPD_VIA_PC": "нет", "UPD_CVE": "да" }, "hosts": ["vm-188"],
  "status": "done", "message": "0 OK, 1 WARN, 0 FAIL, 0 SKIP",
  "started": "2026-11-22T11:07:02Z", "finished": "2026-11-22T11:08:10Z",
  "results": [ {
    "host_id": "…", "host": "vm-188", "status": "WARN",
    "summary": "nginx 1.20.1, OpenSSL 3.5.9, … · CVE закрыть сейчас: 4",
    "connect": "SSH OK · CentOS Stream 9 (rhel)",
    "output": "<вывод с хоста>",
    "report": { "…": "…" }
  } ]
}
```

- `status` задания: `running` | `done` | `failed`. `failed` — сбой самого задания (в том числе «прервано: служба перезапускалась»), текст в `message`.
- `status` хоста: `OK` | `WARN` | `FAIL` | `SKIP`. `SKIP` — хост занят другим действием (`занят: <кто>, <что>`) или тот же адрес и порт уже есть в задании под другим именем.
- Время — UTC, ISO 8601. Имена полей — `snake_case`, кириллица без `\u`.

### `report` — разобранный отчёт

Есть у `versions-check`, `list-users` и `proxy-check`. У `check-updates` его нет — смотрите `summary` и `output`. Основные поля:

- **versions-check:**
  - `host`, `package_manager`, `security_info`, `update_error`;
  - `items[]` — `container` (пусто — сам хост), `product`, `version`, `package`, `package_version`, `update`, `security_update`, `severity` (`bad` | `warn` | `ok` | `none`), `state_text`;
  - у каждого из `items[]` — CVE: `cve_level` (`now` — закрыть сейчас, `plan`, `ok`, `none`), `cves[]` (`id`, `score`, `network`, `kev`, `epss`, `level`, `fix`, `title`), `cve_note`;
  - `containers[]` (`name`, `image`, `runtime`, `mode`), `bad_count`, `warn_count`, `cve_done`, `cve_errors`.
- **list-users:** учётки (`login`, `uid`, `home`, `shell`, `groups`, `sudo`, `password`, `expired`) и их ключи SSH (`type`, `bits`, `fingerprint`, `comment`, `file`).
- **proxy-check:** `host`, `package_manager`, `proxy`, где он прописан, `repo_url`, `repo_code`.

Набор полей отчёта может расширяться. Неизвестные поля пропускайте.

## 5. Ошибки

Тело ошибки: `{"error":{"code":"…","message":"…"}}`.

| HTTP | code | Когда |
|---|---|---|
| 400 | `bad_request` | нет `action`, не JSON, хостов 0 или больше 50, у действия нет такого параметра |
| 401 | `unauthorized` | нет токена, токен неверный или учётка выключена |
| 403 | `no_access` | у учётки нет назначений |
| 403 | `action_not_readonly` | действие меняет хост или не встроенное |
| 403 | `param_forbidden` | параметр закрыт для API (`UPD_VIA_PC`) |
| 403 | `forbidden` | нет права «Опрос и Dry-run» на хосте |
| 404 | `action_not_found` / `host_not_found` / `job_not_found` | нет такого (или учётка его не видит) |
| 429 | `too_many_jobs` | у учётки уже идут 3 задания — дождитесь окончания |
| 503 | `store_busy` | задание не записалось — повторите позже |

## 6. Ограничения

- Только встроенные действия «только чтение». Свои действия каталога и PowerShell через API недоступны.
- До 3 одновременно идущих заданий на учётку, до 50 хостов в задании. Задание, где все хосты получили `SKIP`, заканчивается сразу и место не занимает. Ограничения частоты запросов нет — не опрашивайте чаще раза в 5 секунд.
- На время задания хосты под замком, как у людей. Занятый хост — `SKIP`, а не очередь: запустите позже.
- Результат «Версий ПО» попадает и в «Парк» панели от имени учётки.
- Сверка CVE идёт с сервера LinuxOps (нужен его выход в интернет). Наружу уходят только имена пакетов, версии и номера CVE.
