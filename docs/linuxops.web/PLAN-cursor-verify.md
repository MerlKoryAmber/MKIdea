# LinuxOps Web — план независимой проверки (для Cursor)

Роль — **Проверяющий** (`.cursor/rules/universal_rules.mdc`, §5–6): аудит чужой работы. Код продукта не правишь, «принято» не ставишь. Находки — в отчёт, приёмка — за владельцем.

Что проверяем: **LinuxOps Web 0.7.1**, ветка `feature/linuxops-web` (коммит `3bf9280`). Это весь план веба: этапы Ф0–Ф5 и Ф8, версии 0.1.0–0.7.1. Чек-листы ниже — по областям. Все пункты плана проверяются одним заходом.

## Правила проверки
- Каждый пункт — с доказательством: вывод команды, ответ API, скриншот, `файл:строка`. Без доказательства пункт идёт в 👀, а не в ✅.
- Нашёл ошибку — опиши: файл:строка, сценарий «вход → что получилось → что ожидалось», серьёзность (**важно** / **мелочь** / **было до 0.7**). Не чини.
- Не выдумывай. Нет доступа, данных или инструмента — так и напиши и проверяй только то, что доступно.
- Тестовые данные, созданные тобой (учётки API, назначения, хосты, файлы), в конце убери. Что осталось — перечисли в отчёте.
- Значения паролей и токенов в отчёт и в чат не выписывать.

## Запреты
- Только лаба: стенд 192.168.0.169, хосты 192.168.0.178 и .188, домен `merl.loc`. Ничего вне `192.168.0.x`.
- **dc-01 (192.168.0.175)** — там сидит владелец. По RDP не входить, его сеанс не трогать. На контроллере ничего не менять, кроме тестовых объектов в OU `LinuxOps-Lab` (через `ssh stand lab-ad …`).
- Не трогать `C:\Code\admanager` и его `.env`, не вводить настоящие пароли владельца и учётки других систем.
- На стенде не делать `linuxops-web uninstall`, `purge` и `restore`, не менять `sshd_config` и файрвол. Перед любой выкладкой — `ssh stand "sudo linuxops-web backup"`.
- Не коммитить и не пушить. `dist\LinuxOps-*` не удалять.
- Скачивать новые пакеты и бинарники — только с согласия владельца.

## Где что лежит

| Что | Путь |
|---|---|
| Правила агента | `.cursor/rules/universal_rules.mdc`, `.cursor/rules/mkidea-docs.mdc` |
| Состояние, история версий, «Отложено» | `docs/linuxops.web/CURRENT.md` |
| Карта кода | `docs/linuxops.web/SKELETON.md`, общая — `docs/SKELETON.md` |
| План и решения владельца | `docs/linuxops/PLAN-web.md` (решения 1–16) |
| Отчёты автора | `docs/agent_reports/linuxops.web-<версия>/REPORT.md` (0.1.0 … 0.7.1) |
| Ядро (общее с настольной) | `src/LinuxOps.Core` — `Services/PlanRunner.cs`, `BuiltinActions.cs`, `ActionCatalog.cs`, `SshRunner.cs`, `VersionsCheck.cs`, `CveCheck.cs` |
| Веб-сервис | `src/LinuxOps.Web` — `Program.cs`, `Services/`, `Components/`, `wwwroot/` |
| Вход и права | `src/LinuxOps.Web/Services/Auth/` — `LoginService*.cs`, `AuthEndpoints.cs`, `AuthModels.cs`, `Access.cs`, `LdapAuth.cs`, `MkMfa.cs`, `Totp.cs`, `PasswordHash.cs`, `AdminCli.cs` |
| Хранилище | `Services/WebStore*.cs` (SQLite), `SecretBox.cs` (AES-256-GCM), `AuditLog.cs`, `HostLocks.cs` |
| Сеансы guacd | `Services/Guac/` (`GuacTunnel`, `GuacTickets`, `GuacProtocol`), `WebSession.Sessions.cs`, `wwwroot/guac.js` |
| Файлы | `Services/Files/FileDownloads.cs`, `WebSession.Files.cs` |
| Парк, записи | `FleetCheck.cs`, `WebStore.Fleet.cs`, `RecordingsKeeper.cs` |
| API (Ф8) | `Services/Api/ApiEndpoints.cs`, `ApiJobs.cs`, `WebStore.Api.cs`, `wwwroot/api/openapi.json`, `Components/ApiClientsPanel.razor` |
| PowerShell (Ф5) | `Services/PsConsole.cs`, `Components/PowerShellTab.razor`, `PsPanel.razor` |
| Сервер | `src/LinuxOps.Web/deploy/` — `install.sh`, `update.sh`, `uninstall.sh`, `linuxops-web.sh` (меню), `lib/common.sh`, `linuxops-web.service`, `nginx-linuxops-web.conf` |
| Сборка и выкладка | `tools/package-linuxops-web.ps1`, `tools/deploy-linuxops-web.ps1` |
| Лаба AD | `tools/lab/` (`lab-ad.sh` → на стенде `lab-ad`) |

Тестовых проектов нет. Гейт — сборка без предупреждений плюс сценарии ниже.

## Стенд и вход
- Стенд: `ssh stand` (root по ключу, `~/.ssh/config`), AlmaLinux 10, SELinux enforcing. Сервис — `linuxops-web.service`, данные — `/var/lib/linuxops-web`, мастер-ключ — `/etc/linuxops-web/master.key`. Записи сеансов — `/var/lib/linuxops-web-rec`, модули PowerShell — `/var/lib/linuxops-web-ps/Modules`.
- Браузер — `https://192.168.0.169/` (сертификат лабы: `dist/lab-ca/MKIdea-Lab-Root-CA.crt`) или туннель мимо nginx:
  ```powershell
  ssh -N -o ExitOnForwardFailure=yes -L 15080:127.0.0.1:5080 stand
  ```
  затем `http://localhost:15080/`. Если туннель уже поднят (порт занят), второй не нужен.
- **Локальный админ** — лабораторный режим, красная плашка «ЛАБА»: `admin` / `admin` без TOTP (CURRENT 0.3.0).
- **Учётки AD** (пароли — на стенде, `sudo cat /root/lab/test-accounts`):

| Учётка | Что должна видеть (по назначениям на стенде) |
|---|---|
| `lo-admin-test` | группа `GRP-LOWEB-Admins` → «Админ» на всё: все вкладки, ⚙ целиком, PowerShell |
| `lo-oper-test` | `GRP-LOWEB-Ops-Nested` ∈ `GRP-LOWEB-Operators` (проверка вложенных групп) → «Оператор» на всё: Actions с «Применить», терминал, файлы; без PowerShell и настроек |
| `lo-dept-test` | `GRP-LOWEB-DeptA` → «Linux-оператор отдела» и «Оператор» на папку «Отдел А / Отдел Б», «Просмотр» на lab-178: в Отделе Б работает, lab-178 только видит |
| `lo-host-test` | `GRP-LOWEB-Host188` → роль «GRP-LOWEB-Host188 — проверка» (просмотр, Dry-run) только на vm-188 |
| `lo-audit-test` | лично → «Аудитор» на всё: History, аудит, сеансы; без «Применить» |

  Назначения перед проверкой сверь в ⚙ → «Назначения», членство в группах — `ssh stand lab-ad ls` (OU `LinuxOps-Lab`). Таблица — состояние на момент написания плана.
  `lab-ad` (`/usr/local/sbin`, только root — `ssh stand` входит под root, без `sudo`): `lab-ad user NAME` (создать, печатает пароль), `group`, `member GROUP MEMBER`, `unmember`, `del NAME` — только объекты в `LinuxOps-Lab`.
- **Хосты Inventory:** `lab-178` (root@192.168.0.178, ключ), `vm-188` (lo-web@192.168.0.188), `dc-01` (Windows, 192.168.0.175, RDP — **не подключаться**, см. запреты).
- MK 2FA в ⚙ выключен: живого сервера нет. Включать его не нужно.

## Подготовка
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\rehydrate.ps1 -Fetch
```
```powershell
git fetch; git log --oneline -12 feature/linuxops-web
```
```powershell
dotnet build src\LinuxOps.Web -c Release -nologo
```
```powershell
dotnet build src\LinuxOps -c Release -nologo
```
Версия на стенде должна совпадать с веткой: `ssh stand "cat /opt/linuxops-web/current/deploy/VERSION"` и `v0.7.1` в шапке. Если не совпадает — выкладка (бэкап → `tools/deploy-linuxops-web.ps1 -Target stand`). Это допустимо, но в отчёте отметь.

## Чек-листы

### A. Сборка и зависимости
- Обе сборки Release — 0 ошибок, 0 предупреждений: веб и настольная, ядро у них общее.
- `dotnet list src\LinuxOps.Web package --vulnerable --include-transitive` — нет уязвимых пакетов. Нет сети — 👀.
- Сторонние файлы с закреплённой контрольной суммой: `wwwroot/lib/guacamole/SOURCE.txt` (sha512); `$vendorPkgs` в `tools/package-linuxops-web.ps1` (SHA-256 RPM openh264 и powershell). Сверь, что скрипт проверяет суммы.

### B. Ревью кода (диф `9c0d641..3bf9280` — версии 0.6.0–0.7.1; при желании — с начала ветки)
- Совпадает ли поведение с CURRENT и отчётами (каждое утверждение «проверено» — правда ли по коду).
- Ошибки логики, гонки (блокировки хостов, лимиты, Blazor-цепь при исключениях в обработчиках), утечки `IDisposable`, проглоченные исключения.
- Соответствие SKELETON коду: файлы, роли, имена.

### C. Безопасность (главное)
- **Права:** каждое действие, меняющее данные или хосты, проверяет право на сервере, а не только выключенной кнопкой: `WebSession.Access.cs` (`Allowed`, `CanOn`, `CanOnFolder`, `AllHostsAllowed`). Попробуй обойти: вызов метода из другой вкладки, хост из чужой папки, папка выше своей.
- **Вход:** `AuthEndpoints.cs`, `LoginService*.cs` — пауза после 5 неудач, одноразовость TOTP, «ждём код» в защищённой cookie, антиподделка на logout, `Validate` (простой, общий срок, выключенная учётка, нет назначений).
- **MK 2FA** (`MkMfa.cs`, `LoginService.Mfa.cs`): пускает только `consume` 200 с тем же логином и `context`. При ошибках MK 2FA вход закрыт, обхода нет.
- **Секреты:** пароли и ключи хостов не уходят в браузер (`HostDialog`, `ForConnect`), в журналы и аудит. Токен API хранится только хешем. Приватный ключ консоли не попадает в браузер (пропуск guacd собирает сервис).
- **guacd:** одноразовый пропуск на 60 с только тому же пользователю (`GuacTickets`). Запись `/guac/recording/{id}` — только с правом «аудит».
- **Файлы:** путь в SFTP и одноразовая ссылка скачивания (`FileDownloads`) — нельзя скачать чужое или повторно.
- **API (только чтение):** нельзя запустить меняющее действие, применить, задать `UPD_VIA_PC`, увидеть чужой хост или задание. Cookie панели к API не подходит. Неверный токен — 401 без утечки.
- **PowerShell:** вкладка и `OpenPowerShell` — только «Админ» (`Perm.Vcenter`, `Global` + `AdminOnly`). Учётка `linuxops-ps` не может писать в модули, данные, записи и `/etc/linuxops-web`. ZIP — нет выхода за каталог.
- **Сервер:** `deploy/*.sh` — подстановки и кавычки, права файлов (`stat` на стенде: данные 700, ключ 600, модули 2750, записи 2770), юнит (`ReadWritePaths`), nginx (TLS, заголовки, `X-Forwarded-For`).
- Известно и принято (не находка, если не нашёл обход): из консоли PowerShell видны Kestrel 127.0.0.1:5080 (доверяет `X-Forwarded-For` с loopback) и guacd без пароля. Консоль только у «Админ».

### D. Вход и права в интерфейсе (скриншоты)
- `admin` (лаба): плашка «ЛАБА», в аудите «без TOTP».
- Каждая учётка из таблицы: какие вкладки видны, какие кнопки включены в Inventory, Actions и ⚙, какие хосты в дереве. Сверь с таблицей и с правами ролей (⚙ → «Роли»).
- Учётка AD без назначений не входит: `ssh stand lab-ad user lo-noacc-test`, потом `ssh stand lab-ad del lo-noacc-test`.
- Локальный пользователь (⚙ → «Локальные пользователи»): заведение, вход без назначения — отказ, с назначением — QR и TOTP. Код TOTP считай по ключу из окна (например, `oathtool` или скрипт), телефон не нужен. В конце удалить.
- Выход, простой сессии (по умолчанию 30 мин — можно уменьшить в ⚙ и вернуть), F5 остаётся на вкладке.

### E. Inventory, Actions, History
- Inventory: хост и папка — создать, изменить, дублировать, удалить; «Доступ…» правой кнопкой; ключ SSH файлом. Пароль после сохранения в окне не показывается.
- Actions на `vm-188` (не root, sudo) и `lab-178`: «Опросить», Dry-run и «Применить» для «Проверить обновления», «Аудит пользователей / sudo», «Прокси: проверить», «Версии ПО». «Детали» показывает таблицы и CVE.
- «Установить обновления» и «Обновить отмеченное» — **только Dry-run / предпросмотр**: реальную установку на хостах лабы не запускать без согласия владельца.
- Блокировка хоста: второй запуск того же хоста из второго окна — «занят: кто, что, с какого времени».
- History — все запуски с «Кто». «Парк» — итог «Версий ПО». «Аудит» — записи о входах, отказах и правках.

### F. Подключения, файлы, записи
- Терминал SSH на `lab-178` и `vm-188`: ввод, вставка (Ctrl+V / Shift+Insert), выделение → буфер, размер окна. Новый ключ хоста спрашивается.
- RDP: только форма и сообщения. **dc-01 не подключать.** Другого Windows-хоста в лабе нет — 👀.
- SSH Files: обзор, «Скачать», «Загрузить…» в `/tmp` (до 1 ГБ, замена — с вопросом). Права «Файлы: чтение / запись» у разных учёток.
- History → «Сеансы»: запись проигрывается с перемоткой, текст терминала скачивается без управляющих символов. ⚙ → «Записи сеансов»: срок, объём, «Почистить сейчас».

### G. Парк по расписанию
- ⚙ → «Проверка парка по расписанию» → «Проверить сейчас»: History и аудит от «расписание», «Парк» обновился. Занятый хост пропускается.

### H. API (curl на стенде или из браузера)
Учётка: ⚙ → «Сервисы API» → завести `svc-verify`. Назначение «сервису API» — роль с «Видеть хосты» и «Опрос и Dry-run» на один хост. Токен — в переменную, в отчёт не писать.
```bash
T=lo_...   # токен из окна
curl -sk https://localhost/api/v1/openapi.json -o /dev/null -w '%{http_code}\n'
curl -sk -H "Authorization: Bearer $T" https://localhost/api/v1/actions
curl -sk -H "Authorization: Bearer $T" https://localhost/api/v1/hosts
curl -sk -H "Authorization: Bearer $T" -H 'Content-Type: application/json' \
  -d '{"action":"versions-check","hosts":["<id>"]}' https://localhost/api/v1/jobs
curl -sk -H "Authorization: Bearer $T" https://localhost/api/v1/jobs/<job_id>
```
(запросы на стенде — `ssh stand`, затем curl к `https://localhost`)

Ожидается:
- `/actions` — 4 действия, у «Версий ПО» нет параметра `UPD_VIA_PC`;
- `install-updates` → 403 `action_not_readonly`; `params: {"UPD_VIA_PC":"да"}` → 403 `param_forbidden`; лишний параметр → 400; чужой хост → 404; 4-е одновременное задание → 429;
- задание доходит до `done`, у хоста есть `connect` и `report`;
- History и аудит — от `api:svc-verify (адрес)`;
- выключение → 401, новый токен → старый 401, удаление → 401 и задания учётки удалены; cookie панели без токена → 401;
- ответы совпадают с `openapi.json` (коды, поля).

В конце удалить `svc-verify`.

### I. PowerShell и PowerCLI (под `admin` или `lo-admin-test`)
- Вкладка «PowerShell» → «Подключиться»: приглашение `PS /home/linuxops-ps>`, `whoami` → `linuxops-ps`. Сеанс есть в History → «Сеансы».
- ⚙ → «PowerShell и PowerCLI»: статус «pwsh 7.6.10 · PowerCLI 13.5.1…». «Удалить модули» и повторное «Скачать из интернета» займут ~1 мин и вернут PowerCLI в прежнее состояние.
- Под учёткой консоли: `Import-Module VMware.VimAutomation.Core` без предупреждения CEIP; `Connect-VIServer` на несуществующий адрес → сетевая ошибка. vCenter в лабе нет — 👀.
- Не-админ (`lo-oper-test`): вкладки и раздела ⚙ нет.

### J. Сервер и меню
- `ssh stand "sudo linuxops-web status"`: служба, nginx и guacd active, Health OK, сертификат.
- `sudo linuxops-web backup` — каталог в `/var/backups/linuxops-web`.
- Откат и возврат: `sudo linuxops-web rollback`, затем выкладка текущей версии заново (или `update` из файла). Служба поднимается, данные на месте. Не получилось — сразу верни текущую версию и опиши.
- Повторная установка идемпотентна: ключ консоли `/etc/linuxops-web/ps_ed25519` не пересоздаётся, модули остаются (выкладка = `install.sh --upgrade`).
- Чистая установка и `uninstall` — нужна отдельная ВМ, её нет → 👀.

### K. SELinux, журналы, ресурсы
- После сценариев D–I: `ssh stand "sudo ausearch -m avc -ts today"` → нет AVC. Процесс службы — `unconfined_service_t` (`ps -eZ | grep LinuxOps.Web`).
- `sudo linuxops-web logs service` (также `audit`, `nginx`, `guacd`) и `journalctl -u linuxops-web --since today` — без необработанных исключений.

### L. Интерфейс (скриншоты в папку отчёта)
- Все вкладки (Inventory, Actions, SSH Files, Подключения, PowerShell, History с четырьмя разделами, ⚙) в светлой и тёмной теме.
- Ширина 1280 и меньше 1080 (известный хвост: ширина меньше 1080 не проверялась) — ничего не обрезано и не наезжает.
- Тексты по-русски, без сырых исключений и английских сообщений сервера (кроме ответов guacd, которые не переведены нарочно).

## Известно и не считать ошибкой
Из `docs/linuxops.web/CURRENT.md`, раздел «Отложено»:
- живой MK 2FA, PowerCLI к живому vCenter, QR телефоном, RDP с правом входа;
- выключенные кнопки «Закрыть доступ», «Базовые пользователи», «Новое»;
- урезанный SSH Files (без создания, удаления и переименования);
- Ф7 не делается; второй фактор для опасных действий отложен.

Если поведение противоречит этим записям — это находка.

## Отчёт
- `docs/agent_reports/linuxops.web-0.7.1-verify/REPORT.md` (скриншоты — рядом, `shots/`).
- Состав:
  - что проверено (версия, коммит, дата);
  - находки списком от важного к мелкому (файл:строка, сценарий, серьёзность);
  - ✅ — что подтверждено и чем;
  - 👀 — что проверить не удалось и почему;
  - что из тестовых данных осталось.
- В чат — 5–8 строк в двух частях: «✅ Проверено автоматически:» и «👀 Требует ручной проверки:».
