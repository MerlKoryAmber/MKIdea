# AD Server Groups

Windows WPF exe: компьютеры Windows Server из OU → AD-группы по шаблону из **настроек** → локальные `Administrators`.

## Релиз (exe)

Перед каждым релизом поднимите в `src/AdServerGroups/AdServerGroups.csproj`:

- `Version`
- `AssemblyVersion`
- `FileVersion`
- `InformationalVersion`

Иначе Касперский/белый список может схлопнуть разные сборки в одно приложение по Product+Vendor без различия версий.

```powershell
.\publish.ps1
```

Готовый файл: `dist\AdServerGroups-<version>\AdServerGroups.exe` (self-contained, win-x64).

Опционально: `Providers\` рядом с exe — DLL SQL-провайдера (`AdServerGroups.SqlProvider.dll` + зависимости).  
Нет папки / нет DLL → вкладка «SQL sysadmin» скрыта. Можно раздавать exe без Providers.

В свойствах файла заполнены: Product, Company (Merl), FileDescription, FileVersion, ProductVersion, Copyright.

## Авторизация

1. Сначала прозрачная (текущий Windows-токен / SSO).
2. Пароль спрашивается **только** если SSO к AD не прошла.
3. Пароль в settings не сохраняется.

На вкладке «Группы» шаблон и OU можно переопределить для текущей задачи; «Из настроек» возвращает defaults.

Вкладка «Выполнение»:
1. **Показать план (dry-run)** — только расчёт, без изменений
2. **Бэкап Administrators** — JSON+TXT снимок членов с выбранных серверов
3. **Экспорт плана** — сохранить текст плана
4. **Применить план** — реальное выполнение

## Лаб Merl.loc

- `OU=Servers,OU=Infra,DC=Merl,DC=loc` (Prod/Test + SRV-*)
- `OU=LocalAdmins,OU=Groups,DC=Merl,DC=loc`

## Dev

```powershell
dotnet run --project .\src\AdServerGroups\AdServerGroups.csproj
```

Настройки: зашифрованный файл **рядом с exe** — `AdServerGroups.settings` (DPAPI, CurrentUser).  
История применений: `AdServerGroups.state` (тоже DPAPI) — dry-run ставит `[SKIP]` на уже сделанное; после apply пишется снимок Administrators (hash) — при ручных правках в плане `[CONFLICT]` и кнопка «Diff с прошлым».  
Старый `%AppData%\AdServerGroups\settings.json` при первом запуске мигрирует и удаляется.  
В настройках также размер/позиция окна и высота панели лога.
