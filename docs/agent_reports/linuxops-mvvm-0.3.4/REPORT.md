# LinuxOps 0.3.4 — разрез MainWindow

**Статус:** РЕАЛИЗОВАНО НО НЕ ПРИНЯТО (автор не ставит «принято»).

## Что сделано

- `MainWindow` оставлен оболочкой: чипы режима, футер, версия.
- Вкладки: `Views/ActionsView`, `InventoryView`, `SshFilesView`, `HistoryView` и ViewModel на CommunityToolkit.Mvvm.
- Общие хосты: `AppSession`.
- План: `Services/PlanRunner` — `CancellationToken`, `IProgress` (статус «хост i/n · действие», строки результата по мере хода). Кнопка **Стоп**. Окно на время прогона не блокируется целиком, чтобы Стоп оставался живым.
- Поведение плана, Extended-выделение действий, `SelectedActionIds`, sudo в терминале, `..`, AppDialog — без смены контракта.

## Проверки

- `dotnet build` Release и `dotnet publish` → `dist\LinuxOps-0.3.4\LinuxOps.exe`.
- Независимый UI-прогон (не автор) не делался. Старт exe — smoke, если процесс жив после запуска.

## Хвосты

- Code-behind SSH/Actions остаётся тонким адаптером (выделение ListBox, F-keys, терминал). Логика в ViewModel / PlanRunner.
- Отмена не обрывает уже идущий SSH-вызов, только границу между хостом и действием.
