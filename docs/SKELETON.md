# MKIdea — скелет репозитория

Репозиторий виндовых утилит: [MerlKoryAmber/MKIdea](https://github.com/MerlKoryAmber/MKIdea).  
Язык чата/docs/коммитов — русский, кратко. Docs важнее памяти агента.

## Продукты

| Продукт | Путь | Статус | Назначение |
|---------|------|--------|------------|
| **AdServerGroups** | `src/AdServerGroups` | боевой контур | AD-группы → локальные Administrators (+ SQL sysadmin provider) |
| **LinuxOps** | `src/LinuxOps` (+ `src/LinuxOps.PsHost`, ядро `src/LinuxOps.Core`) | 0.3.54 | Actions + SSH Files + Подключения (SSH + RDP) + PowerShell + History |
| **LinuxOps Web** | `src/LinuxOps.Web` (ядро `src/LinuxOps.Core`) | 0.1.0, ветка `feature/linuxops-web` | веб-сервис на CentOS: Inventory, Actions, «Версии ПО» с CVE, аудит |

Пакет/инсталлятор «всех утилит» — позже; пока отдельные `dist\<App>-<ver>\`.

## Общие правила

1. **Site-данные не вшивать в код** — только `*.defaults.json` рядом с exe (и DPAPI settings/state).
2. **Версию bump’ать** на каждый publish (`Version` / `FileVersion` в csproj).
3. **Publish** → `dist\<Name>-<version>\` (self-contained win-x64; LinuxOps.Web — linux-x64). `dist/` в git не коммитить.
4. **Секреты** (`.settings`, `.state`, пароли, ключи) — не в git.
5. **После фичи** — обновить этот SKELETON и `docs/<app>/` (см. `.cursor/rules/mkidea-docs.mdc`).
6. Вопрос ≠ команда кодить; после реализации — publish в dist, если работа закончена.

## Solution

- `AdServerGroups.sln` — пока общий solution (имя историческое). Проекты: AdServerGroups*, LinuxOps, LinuxOps.Core (ядро), LinuxOps.Web (веб), LinuxOps.PsHost (движок PowerShell).
- Точечный `git add` файлов; push — только по команде.

## Инструменты

- `tools/rehydrate.ps1` — статус репо одним вызовом для агента: git, версии и dist, верхние записи `docs/<app>/CURRENT.md`, хвосты 👀 из последнего отчёта, карта docs. Только чтение; `-Fetch` — сверка с origin.
- `tools/package-linuxops-web.ps1`, `tools/deploy-linuxops-web.ps1` — релиз LinuxOps Web (архив + `.sha256`) и выкладка на стенд.
- `tools/lab/` — автоматизация лабы Merl.loc: `Initialize-LabAd.ps1` (разово, владелец: OU=LinuxOps-Lab, svc-lo-lab, keytab на стенде), `lab-ad.sh` (на стенде `/usr/local/sbin/lab-ad`: тестовые учётки и группы только в этой OU), `lab-keytab.sh`.
- `tools/AdSmoke`, `tools/LinuxOpsSmoke`, `tools/LinuxOpsLabCheck`, `tools/RunAsDomain` — проверочные утилиты.

## Лабы (не путать)

| Среда | Где | Доступ |
|-------|-----|--------|
| AD Merl.loc | см. `docs/adservergroups/` | Windows Auth / domain creds |
| Linux CentOS Stream 9 (две ВМ, можно включать вместе) | `192.168.0.178` (ssh `lab`) · `192.168.0.188` (vm-188, ssh `lab188`) | `root`; .178 — `~/.ssh/lab2fa_ed25519`, .188 — `~/.ssh/radiusproxy_ed25519` |
| Стенд LinuxOps Web (AlmaLinux 10.3) | `192.168.0.169` (linuxops-web, ssh `stand`) | `root` по ключу `~/.ssh/radiusproxy_ed25519`, пароль выключен; сервис `https://192.168.0.169`; корневой CA лабы — `/root/lab-ca` там же, копия `dist/lab-ca/MKIdea-Lab-Root-CA.crt` |

Подробности лаб — в docs конкретного приложения, не дублировать пароли в код.
