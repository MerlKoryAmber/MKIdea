# LinuxOps — скелет

## Контуры

| Контур | Содержимое |
|--------|------------|
| Inventory | дерево: хосты Linux, хосты Windows (RDP) и vCenter по папкам (вложенные, сворачиваются, перетаскивание мышью) → defaults.json |
| Actions | дерево ресурсов с галочками (у папки — все внутри) · каталог · план · редактор действия; Bash по SSH на хостах, PowerShell на отмеченных vCenter |
| SSH Files | дерево хостов Linux (без vCenter и Windows) + SFTP dual-pane + terminal command; sudo — просмотр чужих папок от root |
| Подключения | дерево хостов Linux и Windows (без vCenter), общие вкладки сессий: SSH — xterm-эмулятор (`TerminalScreen` + `TerminalCanvas`), мышь для mc/htop; RDP — встроенный mstscax (`RdpControl` в `WindowsFormsHost`), окно подключения при каждом входе, Remote Credential Guard / Restricted Admin — галочки |
| PowerShell | как VS Code: скрипты (AvalonEdit, подсказки по мере набора, Ctrl+F, автоотступ) + настоящая консоль PowerShell с PSReadLine (ConPTY): список подсказок у курсора, серый хвост из истории; F5/F8 — в эту консоль; «vSphere…» — модули PowerCLI |
| History | LinuxOps.history.json |

## Процессы

- `LinuxOps.exe` — WPF, .NET 8, single-file.
- `pwsh\LinuxOps.PsHost.exe` — движок PowerShell 7.4 (`Microsoft.PowerShell.SDK`), отдельный процесс: модули (PowerCLI) не делят сборки с LinuxOps, зависший вызов снимается вместе с процессом. Связь — анонимные каналы, строки JSON (`PsProtocol`). Встроенные модули PowerShell — `pwsh\Modules` ($PSHOME).
- `LinuxOps.PsHost.exe --console --suggest <канал>` — консоль вкладки PowerShell: `ConsoleShell.Start` (как pwsh.exe) с PSReadLine 2.3.6 (`pwsh\Modules\PSReadLine`, вложен в репозиторий) внутри псевдоконсоли Windows (ConPTY). Предиктор `CompletionPredictor` шлёт дополнения набранной строки по именованному каналу, список рисует LinuxOps.
- `Modules\` рядом с LinuxOps.exe — модули пользователя (PowerCLI), первыми в PSModulePath. Переносится вместе с папкой LinuxOps на машины без интернета.

## Ключевые типы

- `SshRunner` / `SshAuth` / `SftpBrowser`
- `PlanRunner` — план параллельно (до 4 хостов), отмена и прогресс
- `UserProvisioning` + `AddUserWindow` — учётка на отмеченных хостах; с ключом и sudo — `/etc/sudoers.d/linuxops-<логин>` NOPASSWD, без ключа — группа sudo/wheel
- `UserListStore` + `UserListWindow` — базовый список пользователей
- `AppSession` — общий Inventory: `Hosts`, `VCenters`, `WindowsHosts`, `Folders` (плоские списки; папка — `FolderId` у ресурса), событие `TreeChanged`; `Items` — для Actions (Linux + vCenter), `AllItems` — всё, `ConnectItems` — Linux + Windows
- `IInventoryItem` (`HostConfig`, `VCenterProfile`, `WindowsHost`), `HostFolder`; `InventoryTree` (`FolderNode` / `ItemNode`) + `ResourceTree` — одно дерево на все вкладки (`Scope`: All / Actions / Linux / Connect): папки, галочки, поиск, перетаскивание
- `HostDialogs` — добавить / изменить / дублировать / удалить ресурс и папку; `VCenterEditWindow`, `WindowsHostEditWindow`
- RDP: `RdpControl` (AxHost над mstscax, события `IMsTscAxEvents`, `IMsRdpExtendedSettings`) + `RdpSessionView` / `RdpSessionViewModel` (вкладка сессии, размер по вкладке, «Открыть в mstsc») + `RdpConnectWindow` (учётка, пароль — в памяти до закрытия вкладки, галочки); `TestCredential` — только Debug, учётка для проверки на лабе из DPAPI-файла владельца
- ViewModels вкладок (`CommunityToolkit.Mvvm`)
- `ActionCatalog` + `BuiltinActions` + `VsphereActions` + `ActionEditWindow`; `ActionDefinition.Kind` = bash | powershell, `Parameters` (у Bash — переменные окружения через `SshRunner.EncodeEnv`, base64)
- `ProxyScripts` — прокси на хостах: проверить / прописать / убрать (apt, dnf·yum, zypper, окружение; dry-run — `LINUXOPS_DRY=1`). «Прописать» заменяет чужой прокси (строки отключаются пометкой LinuxOps-disabled, profile.d перекрывается `zz-linuxops-proxy.sh`), «убрать» возвращает его; `ProxyCheck` — разбор «проверить» в таблицу (`ResultRow.Proxy`)
- `PsEngineClient` — процесс движка, события в UI-поток; `PsPrompts` — Read-Host / Get-Credential / выбор окнами
- `PowerShellViewModel` + `PowerShellView` — вкладка PowerShell; `PsEditorAssist` (подсказки, поиск, автоотступ, отмена по словам) + `PsHighlighting` — редактор; `PsEngineClient` в режиме «только дополнения» — подсказки редактора
- `ConPty` (псевдоконсоль Windows) + `PsConsoleSession` (консоль: ConPTY + `TerminalScreen`, канал подсказок, перезапуск) + `PsConsoleView` (холст терминала, список подсказок у курсора, копирование/вставка); `TerminalKeys` — клавиши в VT и win32-input-mode (общие с SSH-сессиями «Подключений»)
- `PsActionRunner` — PowerShell-действия: свой движок, подключение к vCenter держится до закрытия, пароль vCenter только в памяти
- `PowerCliInstaller` — PowerCLI из PowerShell Gallery (nupkg + зависимости), из ZIP/папки, экспорт в ZIP
- `VsphereWindow` (модули PowerCLI), `PsRunWindow` («Параметры действий» — Bash и PowerShell, перед запуском, если они есть)
- `DistroLabel` + цветные монограмм-иконки в списке хостов
- `HistoryStore`

## Publish

`dist\LinuxOps-<ver>\LinuxOps.exe` + `pwsh\` (движок PowerShell, кладётся целью `BundlePsHost` после publish).
