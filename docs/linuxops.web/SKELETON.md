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

## Компоненты

`App` → `Routes` → `Home` (адрес клиента) → `Shell` (шапка, вкладки, подвал) → `InventoryTab` / `ActionsTab` (+ `DetailView`) / `HistoryTab` / `Placeholder`; `HostTree` — дерево с папками; `DialogHost` (+ `HostDialog`, `AddUserDialog`), `PromptHost`. Стили — `wwwroot/app.css`: цвета тем `Themes/Light.xaml` и `Dark.xaml` настольной версии.

## Publish

`dist\LinuxOps.Web-<ver>\` — linux-x64 self-contained; `deploy/linuxops-web.service` — юнит systemd.
