# LinuxOps — скелет

## Контуры

| Контур | Содержимое |
|--------|------------|
| Inventory | CRUD хостов → defaults.json |
| Actions | хосты · каталог · план · редактор действия |
| SSH Files | SFTP dual-pane + terminal command |
| History | LinuxOps.history.json |

## Ключевые типы

- `SshRunner` / `SshAuth` / `SftpBrowser`
- `PlanRunner` — план параллельно (до 4 хостов), отмена и прогресс
- `UserProvisioning` + `AddUserWindow` — учётка на отмеченных хостах
- `AppSession` + ViewModels вкладок (`CommunityToolkit.Mvvm`)
- `ActionCatalog` + `BuiltinActions` + `ActionEditWindow`
- `DistroLabel` + цветные монограмм-иконки в списке хостов
- `HistoryStore`

## Publish

`dist\LinuxOps-<ver>\LinuxOps.exe`
