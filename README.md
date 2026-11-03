# MKIdea

Виндовые утилиты: [github.com/MerlKoryAmber/MKIdea](https://github.com/MerlKoryAmber/MKIdea).

**Скелет репо:** [`docs/SKELETON.md`](docs/SKELETON.md) — начинать отсюда.  
Статусы продуктов: `docs/<app>/CURRENT.md`.

| Утилита | Docs | Exe |
|---------|------|-----|
| AdServerGroups | [docs/adservergroups](docs/adservergroups/CURRENT.md) | `dist\AdServerGroups-<ver>\` |
| LinuxOps | [docs/linuxops](docs/linuxops/CURRENT.md) | `dist\LinuxOps-<ver>\` |

---

## AdServerGroups

Windows WPF: компьютеры Windows Server из OU → AD-группы по шаблону → локальные `Administrators` (контур SQL sysadmin — опциональный provider).

### Релиз

В `src/AdServerGroups/AdServerGroups.csproj` поднять Version / FileVersion, затем:

```powershell
.\publish.ps1
```

→ `dist\AdServerGroups-<version>\AdServerGroups.exe`.  
`Providers\` — SQL DLL; без неё вкладка SQL скрыта.

### Авторизация

SSO (текущий токен), пароль — только если SSO к AD не прошла. Пароль в settings не хранится.

### Dev

```powershell
dotnet run --project .\src\AdServerGroups\AdServerGroups.csproj
```

Настройки: `AdServerGroups.settings` (DPAPI) рядом с exe; база среды — `AdServerGroups.defaults.json`.

---

## LinuxOps

SSH-действия по Linux (built-in + `Actions\*.json`). Лаба: CentOS Stream 9 `192.168.0.178` (ключ `radiusproxy_ed25519`).

```powershell
dotnet run --project .\src\LinuxOps\LinuxOps.csproj -c Release
dotnet publish .\src\LinuxOps\LinuxOps.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\dist\LinuxOps-0.1.0\
```

Подробности: [`docs/linuxops/SKELETON.md`](docs/linuxops/SKELETON.md).
