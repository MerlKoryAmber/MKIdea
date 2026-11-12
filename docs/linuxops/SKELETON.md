# LinuxOps — скелет

## Контуры

| Контур | Содержимое |
|--------|------------|
| Inventory | дерево: хосты Linux и vCenter по папкам (вложенные, сворачиваются, перетаскивание мышью) → defaults.json |
| Actions | дерево ресурсов с галочками (у папки — все внутри) · каталог · план · редактор действия; Bash по SSH на хостах, PowerShell на отмеченных vCenter |
| SSH Files | дерево хостов (без vCenter) + SFTP dual-pane + terminal command; sudo — просмотр чужих папок от root |
| SSH Terminal | дерево хостов (без vCenter), вкладки SSH-сессий, xterm-эмулятор (`TerminalScreen` + `TerminalCanvas`), мышь для mc/htop |
| PowerShell | в стиле ISE: скрипты (AvalonEdit) + консоль, F5/F8, стоп, перезапуск движка; «vSphere…» — модули PowerCLI |
| History | LinuxOps.history.json |

## Процессы

- `LinuxOps.exe` — WPF, .NET 8, single-file.
- `pwsh\LinuxOps.PsHost.exe` — движок PowerShell 7.4 (`Microsoft.PowerShell.SDK`), отдельный процесс: модули (PowerCLI) не делят сборки с LinuxOps, зависший вызов снимается вместе с процессом. Связь — анонимные каналы, строки JSON (`PsProtocol`). Встроенные модули PowerShell — `pwsh\Modules` ($PSHOME).
- `Modules\` рядом с LinuxOps.exe — модули пользователя (PowerCLI), первыми в PSModulePath. Переносится вместе с папкой LinuxOps на машины без интернета.

## Ключевые типы

- `SshRunner` / `SshAuth` / `SftpBrowser`
- `PlanRunner` — план параллельно (до 4 хостов), отмена и прогресс
- `UserProvisioning` + `AddUserWindow` — учётка на отмеченных хостах
- `UserListStore` + `UserListWindow` — базовый список пользователей
- `AppSession` — общий Inventory: `Hosts`, `VCenters`, `Folders` (плоские списки; папка — `FolderId` у ресурса), событие `TreeChanged`
- `IInventoryItem` (`HostConfig`, `VCenterProfile`), `HostFolder`; `InventoryTree` (`FolderNode` / `ItemNode`) + `ResourceTree` — одно дерево на все вкладки: папки, галочки, поиск, перетаскивание
- `HostDialogs` — добавить / изменить / дублировать / удалить ресурс и папку; `VCenterEditWindow`
- ViewModels вкладок (`CommunityToolkit.Mvvm`)
- `ActionCatalog` + `BuiltinActions` + `VsphereActions` + `ActionEditWindow`; `ActionDefinition.Kind` = bash | powershell, `Parameters`
- `PsEngineClient` — процесс движка, события в UI-поток; `PsPrompts` — Read-Host / Get-Credential / выбор окнами
- `PowerShellViewModel` + `PowerShellView` + `PsConsoleColorizer` — вкладка PowerShell (один сеанс на вкладку)
- `PsActionRunner` — PowerShell-действия: свой движок, подключение к vCenter держится до закрытия, пароль vCenter только в памяти
- `PowerCliInstaller` — PowerCLI из PowerShell Gallery (nupkg + зависимости), из ZIP/папки, экспорт в ZIP
- `VsphereWindow` (модули PowerCLI), `PsRunWindow` (параметры PowerShell-действий перед запуском, если они есть)
- `DistroLabel` + цветные монограмм-иконки в списке хостов
- `HistoryStore`

## Publish

`dist\LinuxOps-<ver>\LinuxOps.exe` + `pwsh\` (движок PowerShell, кладётся целью `BundlePsHost` после publish).
