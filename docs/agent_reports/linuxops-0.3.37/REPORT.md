# LinuxOps 0.3.37 — вкладка PowerShell, PowerCLI и vSphere-действия

Дата: 2026-11-10 (МСК).

## Задача
PowerShell 7 в LinuxOps: отдельная вкладка в стиле ISE, модули PowerCLI для vSphere 8 (в том числе без интернета), PowerShell-действия во вкладке Actions. Встроенный набор — только vSphere; AD и Exchange 2019 — по запросу.

Согласовано: PowerShell 7.4 (.NET 8), зависимости Microsoft.PowerShell.SDK и AvalonEdit, без стенда vCenter.

## Изменения
- `src/LinuxOps.PsHost/` (новый проект) — движок: `Program` (каналы, запросы ввода), `PsEngine` (runspace на сеанс, запуск, стоп), `LoHost` (PSHost: вывод, Read-Host, Get-Credential, выбор, ширина). Встроенные модули переносятся в `pwsh\Modules`.
- `Services/PsProtocol.cs` — сообщения JSON (общий файл, в помощник подключён ссылкой).
- `Services/PsEngineClient.cs`, `Services/PsPrompts.cs` — процесс движка и окна запросов.
- `ViewModels/PowerShellViewModel.cs`, `Views/PowerShellView.xaml(.cs)`, `Views/PsConsoleColorizer.cs` — вкладка.
- `Services/PowerCliInstaller.cs`, `VsphereWindow.xaml(.cs)` — модули и профили vCenter.
- `Services/PsActionRunner.cs`, `Services/VsphereActions.cs`, `PsRunWindow.xaml(.cs)` — PowerShell-действия.
- `Models/AppModels.cs` — `ActionDefinition.Kind/Parameters`, `ActionParam`, `VCenterProfile`, `AppDefaults.VCenters`.
- `ActionsViewModel` — Bash и PowerShell в одном запуске, превью и сводка плана. `ActionEditWindow` — тип и параметры. `ActionCatalog` — набор vSphere.
- `MainWindow` — вкладка PowerShell, вопрос о несохранённых скриптах при закрытии. `AppDialog.AskSave`.
- `DefaultsStore.Write` — пароли хостов шифруются при любой записи.
- `LinuxOps.csproj` — AvalonEdit, публикация движка в `pwsh\`. `AdServerGroups.sln` — проект движка.

## ✅ Проверено автоматически
- Сборка Release: 0 ошибок, 0 предупреждений.
- Движок через каналы: вывод и таблицы, ошибки, предупреждения, цвета Write-Host, прогресс, Read-Host, Get-Credential, PromptForChoice, стоп (в том числе во время запроса ввода), сохранение состояния, выход вместе с LinuxOps.
- Вкладка PowerShell, UI Automation, тёмная и светлая темы (скриншоты): F5 с Read-Host → окно → вывод; ошибка и предупреждение цветом; Format-Table; Get-Credential из консоли (пароль дошёл); F8 с выбором; «Стоп» → «остановлено»; ↑ — история; переменная сохраняется между запусками; ширина консоли 347 колонок; закрытие с несохранённым → «Сохранить / Не сохранять / Отмена» → выход, процесс движка не остаётся.
- PowerCLI: «Скачать из интернета» — 5 модулей, 13.3.0.24145081, 8 с; `Import-Module` в движке — 4 с, `Get-VM` доступен; экспорт в ZIP (29,8 МБ) → очистка `Modules` → «Установить из ZIP» → снова 13.3.
- Профиль vCenter сохраняется в defaults.json (без пароля).
- Все 5 vSphere-действий (Dry-run, vCenter 127.0.0.1): окно параметров, запрос пароля, импорт модуля, разбор скриптов — до `Connect-VIServer`; FAIL с понятной причиной («не удалось подключиться к 127.0.0.1 — No connection could be made…»), записи в History. Неверный вход не запоминается — следующий запуск спрашивает снова.
- Смешанный запуск: Bash «Проверить обновления» на lab-centos — OK (12 обновлений), PowerShell-действие с отменённым входом — FAIL «вход в vCenter 127.0.0.1 отменён».
- Редактор действия: новое PowerShell-действие с параметром → JSON с `Kind=powershell`, `Parameters`, без дистрибутивов.
- Безопасность: пароли vCenter — только в памяти процесса; каналы анонимные (без имени, наследуются только дочерним процессом); значения параметров и адрес vCenter подставляются в одинарных кавычках с экранированием; распаковка nupkg не выходит за папку модуля; скачивание — HTTPS с PowerShell Gallery. Найден и исправлен давний баг: пароли хостов попадали в defaults.json открытым текстом при сохранении плана, темы или редактора.

## 👀 Требует ручной проверки
- vSphere-действия против настоящего vCenter 8: стенда нет, проверено только до подключения. Проверка: «vSphere…» → профиль → Actions → действие vSphere → Dry-run.
- Независимое ревью и аудит (не автором) по правилам не проводились — нужен отдельный проход проверяющего.

## Известные ограничения
- Нет автодополнения и обзора команд — в план не входили, можно добавить следующим шагом.
- Предупреждение CEIP PowerCLI видно при `Import-Module` на вкладке PowerShell (в действиях скрыто). Убирается `Set-PowerCLIConfiguration -Scope User -ParticipateInCEIP $false`.
- LinuxOps и движок на .NET 8 / PowerShell 7.4 — поддержка Microsoft заканчивается 10.11.2026; переезд — отдельная задача.
