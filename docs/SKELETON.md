# MKIdea — скелет репозитория

Репозиторий виндовых утилит: [MerlKoryAmber/MKIdea](https://github.com/MerlKoryAmber/MKIdea).  
Язык чата/docs/коммитов — русский, кратко. Docs важнее памяти агента.

## Продукты

| Продукт | Путь | Статус | Назначение |
|---------|------|--------|------------|
| **AdServerGroups** | `src/AdServerGroups` | боевой контур | AD-группы → локальные Administrators (+ SQL sysadmin provider) |
| **LinuxOps** | `src/LinuxOps` (+ `src/LinuxOps.PsHost`) | 0.3.40 | Actions + SSH Files + Terminal + PowerShell + History |

Пакет/инсталлятор «всех утилит» — позже; пока отдельные `dist\<App>-<ver>\`.

## Общие правила

1. **Site-данные не вшивать в код** — только `*.defaults.json` рядом с exe (и DPAPI settings/state).
2. **Версию bump’ать** на каждый publish (`Version` / `FileVersion` в csproj).
3. **Publish** → `dist\<Name>-<version>\` (self-contained win-x64). `dist/` в git не коммитить.
4. **Секреты** (`.settings`, `.state`, пароли, ключи) — не в git.
5. **После фичи** — обновить этот SKELETON и `docs/<app>/` (см. `.cursor/rules/mkidea-docs.mdc`).
6. Вопрос ≠ команда кодить; после реализации — publish в dist, если работа закончена.

## Solution

- `AdServerGroups.sln` — пока общий solution (имя историческое). Проекты: AdServerGroups*, LinuxOps, LinuxOps.PsHost (движок PowerShell).
- Точечный `git add` файлов; push — только по команде.

## Инструменты

- `tools/rehydrate.ps1` — статус репо одним вызовом для агента: git, версии и dist, верхние записи `docs/<app>/CURRENT.md`, хвосты 👀 из последнего отчёта, карта docs. Только чтение; `-Fetch` — сверка с origin.
- `tools/AdSmoke`, `tools/LinuxOpsSmoke`, `tools/LinuxOpsLabCheck`, `tools/RunAsDomain` — проверочные утилиты.

## Лабы (не путать)

| Среда | Где | Доступ |
|-------|-----|--------|
| AD Merl.loc | см. `docs/adservergroups/` | Windows Auth / domain creds |
| Linux CentOS Stream 9 | `192.168.0.178` | `root` + `~/.ssh/radiusproxy_ed25519` |

Подробности лаб — в docs конкретного приложения, не дублировать пароли в код.
