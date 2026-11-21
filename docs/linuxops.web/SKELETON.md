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

- `Program.cs` — пути, `WebPrompts`, DI: синглтоны `SecretBox`, `WebStore`, `AuditLog`, `HostLocks`; на окно браузера — `PromptChannel`, `UiDialogs`, `WebSession`.
- `WebStore` — SQLite (WAL): `folders`, `hosts` (JSON без секретов), `secrets` (зашифровано), `history`, `audit`, `settings`. `ForConnect` — копия хоста с расшифрованными секретами, только для подключения.
- `SecretBox` — AES-256-GCM, `v1:` + base64(nonce|tag|шифр); мастер-ключ 32 байта, создаётся при первом запуске (права 600).
- `AuditLog` — база + `audit.log` + syslog.
- `HostLocks` — замок на `хост:порт` на время действия.
- `WebSession` — перенос `ActionsViewModel`: выбор хостов и плана, предпросмотр, запуск (строки RUN, живой вывод, время), опрос, «Добавить пользователя», «Обновить отмеченное», хосты и папки.
- `UiDialogs` — стек модальных окон с `TaskCompletionSource` (как `ShowDialog`).

## Вход и права (`Services/Auth`)

- `LoginService` — шаги входа: пароль → (локальный админ) TOTP или привязка; AD → роли по группам. Пауза после 5 неудач. `Resolve` — права при открытии окна и на каждом запросе.
- `AuthEndpoints` — POST `/auth/login`, `/auth/totp`, `/auth/logout` (антиподделка), cookie `lo.auth`; «ждём код» и сообщение — защищённые cookie. `Validate` — простой, общий срок, админ включён, роли есть. `/auth/ping` — продление из открытого окна (`App.razor`).
- `LdapAuth` — bind учёткой пользователя, группы через `member:1.2.840.113556.1.4.1941:`; `Probe` — rootDSE анонимно. На Linux подсказывает загрузчику `libldap.so.2`.
- `Totp` (RFC 6238), `PasswordHash` (PBKDF2-SHA256, 600 000), `AuthModels` — `Perm`, `Role` (встроенные: admin, operator, auditor), `AuthSettings`, `LocalAdmin`, `UserInfo`.
- `AdminCli` — `LinuxOps.Web admin status | password [--generate] | totp-reset | enable | disable | lab on|off` (лаба: admin/admin без TOTP, плашка «ЛАБА»); меню сервера зовёт его через `runuser -u linuxops-web`.
- `WebStore.Auth.cs` — настройки `auth`, `local_admin` (секрет TOTP зашифрован `SecretBox`), таблица `roles`.
- `WebSession.Access.cs` — `User`, `Can`, отказ с аудитом, папки роли (`ApplyFolderScope`, `InScope`).
- Страницы: `Login.razor` (статическая, без WebSocket), `Home.razor` (`[Authorize]`, форма выхода, `lo-idle`), `SettingsTab.razor`.

## Компоненты

`App` → `Routes` → `Home` (адрес клиента) → `Shell` (шапка, вкладки, подвал) → `InventoryTab` / `ActionsTab` (+ `DetailView`) / `HistoryTab` / `Placeholder`; `HostTree` — дерево с папками; `DialogHost` (+ `HostDialog`, `AddUserDialog`), `PromptHost`. Стили — `wwwroot/app.css`: цвета тем `Themes/Light.xaml` и `Dark.xaml` настольной версии.

## Publish

`dist\LinuxOps.Web-<ver>\` — linux-x64 self-contained, рядом `LinuxOps.Web-<ver>-linux-x64.tar.gz` + `.sha256` (`tools/package-linuxops-web.ps1`).

## Сервер (`deploy/`)

- `lib/common.sh` — пути, ввод (`/dev/tty` или stdin), сертификаты (свой CA с `nameConstraints`), источник релизов (GitHub / GitLab API), `activate_release` (переключить `current`, поднять, при неудаче вернуть прежний).
- `install.sh` (`--upgrade` из update), `update.sh`, `uninstall.sh` (`--purge`), `linuxops-web.sh` — меню; `/usr/bin/linuxops-web` — обёртка на `current/deploy/linuxops-web.sh`.
- `VERSION` пишет publish (цель `WriteDeployVersion` в csproj).
- На сервере: `/opt/linuxops-web/releases/<ver>`, `current`, `previous`; `/var/lib/linuxops-web` — данные; `/etc/linuxops-web` — мастер-ключ, `release.conf`, `git-token`; `/etc/pki/linuxops-web` — сертификат и CA; `/var/backups/linuxops-web` — бэкапы.
- С ПК на стенд — `tools/deploy-linuxops-web.ps1 -Target stand`.
