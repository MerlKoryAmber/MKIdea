# LinuxOps Web — скелет

## Проекты

| Проект | Что |
|--------|-----|
| `src/LinuxOps.Core` | ядро, общее с настольной версией (net8.0): SSH, план, каталог, «Версии ПО», CVE, обновление, учётки, прокси, история. Пространства имён прежние (`LinuxOps.Services`, `LinuxOps.Models`) |
| `src/LinuxOps.Web` | Blazor Server (net8.0), одна страница `/`, рендер `InteractiveServer` без пререндера |
| `src/LinuxOps` | настольная WPF-версия, ссылается на ядро |

## Связь ядра с интерфейсом

- `IUserPrompts` (`UserPrompts.Current`) — вопросы ядра: пароль ключа, доверие ключу хоста, пароль sudo. Настольная — `WpfPrompts` (окна), веб — `WebPrompts`: через `AsyncLocal<PromptChannel>` вопрос попадает в окно браузера, которое запустило действие; фоновый поток ждёт ответа до 10 минут.
- `CrashLog` — запись в ядре; окно и ловушка нативных падений — `CrashReporter` настольной версии.
- `LINUXOPS_DATA_DIR` — каталог данных ядра (known_hosts, кэш CVE, свои действия). Веб задаёт его в `WebPaths.Prepare()`.
- `HostConfig.PrivateKeyText` — ключ текстом, только в памяти (из хранилища сервиса).

## Сервис

- `Program.cs` — пути, `WebPrompts`, DI: синглтоны `SecretBox`, `WebStore`, `AuditLog`, `HostLocks`, `GuacTickets`, `FileDownloads`, `FleetCheck`, `RecordingsKeeper` (обе и фоновые службы); на окно браузера — `PromptChannel`, `UiDialogs`, `WebSession`. `UseWebSockets` → `/guac/tunnel`.
- `WebStore` — SQLite (WAL): `folders`, `hosts` (JSON без секретов; хост Windows — `DistroHint = "windows"`, `StoredHost.IsWindows`), `secrets` (зашифровано), `history`, `audit`, `settings`, `sessions` (сеансы guacd), `fleet` (последний итог «Версии ПО» по хосту). `ForConnect` — копия хоста с расшифрованными секретами, только для подключения.
- `SecretBox` — AES-256-GCM, `v1:` + base64(nonce|tag|шифр); мастер-ключ 32 байта, создаётся при первом запуске (права 600).
- `AuditLog` — база + `audit.log` + syslog.
- `HostLocks` — замок на `хост:порт` на время действия.
- `WebSession` — перенос `ActionsViewModel`: выбор хостов и плана, предпросмотр, запуск (строки RUN, живой вывод, время), опрос, «Добавить пользователя», «Обновить отмеченное», хосты и папки.
- `UiDialogs` — стек модальных окон с `TaskCompletionSource` (как `ShowDialog`).

## Вход и права (`Services/Auth`)

- `LoginService` — шаги входа: пароль → (локальный админ) TOTP или привязка; AD → группы и назначения (нет назначений — не пускает). Пауза после 5 неудач. `Resolve` — назначения перечитываются при открытии окна и на каждом запросе; группы — из cookie (`grp`, с момента входа).
- `AuthEndpoints` — POST `/auth/login`, `/auth/totp`, `/auth/logout` (антиподделка), cookie `lo.auth`; «ждём код» и сообщение — защищённые cookie. `Validate` — простой, общий срок, админ включён, роли есть. `/auth/ping` — продление из открытого окна (`App.razor`).
- `LdapAuth` — bind учёткой пользователя, группы через `member:1.2.840.113556.1.4.1941:`; `Probe` — rootDSE анонимно. На Linux подсказывает загрузчику `libldap.so.2`. `Find` — поиск групп и людей учёткой поиска из настроек (`AdLookupUser`, пароль зашифрован), до 25 строк.
- `Totp` (RFC 6238), `PasswordHash` (PBKDF2-SHA256, 600 000), `AuthModels` — модель как в vSphere: `Perm` (11 прав; `Global` — аудит, настройки, vCenter — только из назначения на «всё»; `AdminOnly` — vCenter), `Role` — только набор прав (встроенные: admin, operator, auditor, viewer; не меняются), `Assignment` — кто (группа или человек AD) → роль → объект (`Scope` all / folder / host), `Grant`, `UserInfo` (`Can` — право где-нибудь, `Limited`), `LegacyRole` — для переноса, `AuthSettings`, `LocalAdmin`.
- `AdminCli` — `LinuxOps.Web admin status | password [--generate] | totp-reset | enable | disable | lab on|off` (лаба: admin/admin без TOTP, плашка «ЛАБА»); меню сервера зовёт его через `runuser -u linuxops-web`.
- `WebStore.Auth.cs` — настройки `auth`, `local_admin` (секрет TOTP зашифрован `SecretBox`), таблицы `roles` (свои роли) и `assignments`. Перенос ролей до 0.4.0 (группы и папки внутри роли) в назначения — один раз, флаг `auth_model=2`. Удалили папку или хост — их назначения тоже.
- `WebSession.Access.cs` — `User`, `Can` (где-нибудь), `CanOn` (хост: «всё», сам хост, папка выше), `CanOnFolder` (папка или выше; корень — только «всё»); отказ с аудитом и сообщением (`Notice` — поверх любой вкладки); видимые хосты и папки (`ApplyFolderScope`); `AllHostsAllowed` — запуск только если право есть на каждом отмеченном хосте. Папки: создать — право на родителя, переименовать — на саму, удалить — на родителя.
- Страницы: `Login.razor` (статическая, без WebSocket), `Home.razor` (`[Authorize]`, форма выхода, `lo-idle`), `SettingsTab.razor` — открывается шестерёнкой в шапке (вкладки нет): тема всем, вход и роли — с правом «Настройки».

## Компоненты

`App` → `Routes` → `Home` (адрес клиента) → `Shell` (шапка, вкладки, подвал) → `InventoryTab` / `ActionsTab` (+ `DetailView`) / `HistoryTab` / `Placeholder`; `HostTree` — дерево с папками (галка папки — только если отмечена сама папка, иначе «частично»; правая кнопка → `Context`); `InventoryTab` — кнопки и меню правой кнопки по правам на объект, `AccessPanel` — «Доступ…»: назначения на объекте (свои, от папок выше, «на всё»), назначить группе или человеку; `AdPicker` — поиск в AD для ролей; `DialogHost` (+ `HostDialog`, `AddUserDialog`), `PromptHost`. Стили — `wwwroot/app.css`: цвета тем `Themes/Light.xaml` и `Dark.xaml` настольной версии. `App.razor` — своя полоска обрыва связи (`#components-reconnect-modal`): служба перезапустилась — страница перезагружается сама.

## Publish

`dist\LinuxOps.Web-<ver>\` — linux-x64 self-contained, рядом `LinuxOps.Web-<ver>-linux-x64.tar.gz` + `.sha256` (`tools/package-linuxops-web.ps1`).

## Сервер (`deploy/`)

- `lib/common.sh` — пути, ввод (`/dev/tty` или stdin), сертификаты (свой CA с `nameConstraints`), источник релизов (GitHub / GitLab API), `activate_release` (переключить `current`, поднять, при неудаче вернуть прежний).
- `install.sh` (`--upgrade` из update), `update.sh`, `uninstall.sh` (`--purge`), `linuxops-web.sh` — меню; `/usr/bin/linuxops-web` — обёртка на `current/deploy/linuxops-web.sh`.
- `VERSION` пишет publish (цель `WriteDeployVersion` в csproj).
- На сервере: `/opt/linuxops-web/releases/<ver>`, `current`, `previous`; `/var/lib/linuxops-web` — данные; `/etc/linuxops-web` — мастер-ключ, `release.conf`, `git-token`; `/etc/pki/linuxops-web` — сертификат и CA; `/var/backups/linuxops-web` — бэкапы.
- С ПК на стенд — `tools/deploy-linuxops-web.ps1 -Target stand`.

## Сеансы, файлы, парк (0.5.0)

- `Services/Guac/` — `GuacProtocol` (кодирование «длина.значение», длины в кодовых точках), `GuacTickets` (одноразовый пропуск на 60 с: параметры connect, только тому же пользователю), `GuacTunnel` — `/guac/tunnel` (WebSocket, подпротокол `guacamole`): рукопожатие с guacd (`select` → `args` → `size`/`audio`/`video`/`image`/`timezone` → `connect`), служебная первая инструкция с номером сеанса, ping — эхом, отправка в WebSocket под замком; `sessions` и аудит; `/guac/recording/{id}` — запись (`.guac`) или текст терминала без управляющих символов, только с правом «аудит».
- `WebSession.Sessions.cs` — `OpenTerminal` (право «Терминал», проверка ключа хоста по SSH.NET, ключ для guacd — тип по порядку libssh2 из `/etc/ssh/ssh_host_*_key.pub` проверенного соединения), `OpenRdp` (право «RDP», своя учётка, NLA, `disable-gfx`; ответы guacd переводит `TerminalTab.Explain`), `RecordKeys` (`recording-include-keys` по настройке `rec.keys`), `TestRdpPort`. Записи — `WebPaths.RecordingsDir` (`/var/lib/linuxops-web-rec`, guacd:linuxops-web 2750, `recordings_dir_ensure` в `deploy/lib/common.sh`; там же `openh264_ensure DIR` — RPM из `DIR/deploy/vendor` под версию ОС (подпись EPEL, `rpm -K`), иначе репозиторий `epel-cisco-openh264`; `dnf swap noopenh264`: без неё FreeRDP 2 в guacd не подключает RDP. RPM кладёт в релиз `tools/package-linuxops-web.ps1`: скачивает один раз в `dist\vendor`, сверяет закреплённый SHA-256); guacd — `WebPaths.Guacd` (`localhost:4822`).
- `wwwroot/guac.js` (модуль) + `wwwroot/lib/guacamole/` (guacamole-common-js 1.5.0, ESM): экран, мышь, клавиатура только при фокусе, вставка (терминал — Ctrl+Shift+V в guacd, RDP — Ctrl+V), буфер из сеанса → буфер ПК, размер за окном, экран вписан в окно (`display.scale`); `.conn-host` и `.rec-player` — `isolation: isolate` (холст слоя 0 с z-index −1); проигрывание записи (`SessionRecording`, перемотка).
- `Services/Files/FileDownloads` — `/files/download/{id}`: одноразовая ссылка, SFTP → поток ответа. `WebSession.Files.cs` — `OpenFiles` (право «Файлы: чтение», имена владельцев через getent), `DownloadLink`, `Upload` (право «Файлы: запись», до 1 ГБ, замена — с вопросом).
- `FleetSnapshot` / `WebStore.Fleet.cs` — срочное из `VersionReport` (CVE «сейчас», обновления безопасности, bad/warn); пишется из `WebSession.Execute` на каждый итог «Версии ПО». `FleetCheck` — `BackgroundService`: раз в сутки в `fleet.time` (настройки `fleet.*`), `RunNow`; блокировки хостов, History и аудит от «расписание».
- `RecordingsKeeper` — `BackgroundService`, раз в час: записи по сеансам (файлы `<id>.*`), сначала старше `rec.days`, потом сверх `rec.maxgb` (самые старые); идущие и свежее 5 мин не трогает; `WebStore.SessionNote` — «запись удалена: …» в статус сеанса; аудит «хранение записей». `Usage`, `CleanAsync` — для ⚙. Юнит службы: `ReadWritePaths=-/var/lib/linuxops-web-rec`, каталог 2770.
- Компоненты: `TerminalTab` (Подключения: SSH и форма входа RDP), `FilesTab` (SSH Files), `WinHostDialog`; `HostTree.Filter` — Actions и файлы без хостов Windows; `HistoryTab` — History / Парк / Аудит / Сеансы.
