# Notepad++ (portable x64) для LinuxOps

- Версия: 8.9.8.1, релиз от 2026-09-24.
- Источник: https://github.com/notepad-plus-plus/notepad-plus-plus/releases/tag/v8.9.8.1
- Файл: `npp.8.9.8.1.portable.x64.zip`; SHA-256 из `npp.8.9.8.1.checksums.sha256` релиза — в `.sha256` рядом (совпал при загрузке). `notepad++.exe` подписан издателем NOTEPAD++ (подпись проверена).
- Лицензия: GPL v3 (`license.txt` внутри архива, попадает в релиз рядом с программой). Notepad++ распространяется без изменений.

При `dotnet publish` архив распаковывается в `Tools\Notepad++\` рядом с `LinuxOps.exe` (цель `BundleNotepadPlusPlus` в `LinuxOps.csproj`). Portable-режим: настройки Notepad++ хранятся в его папке (`doLocalConf.xml`), автообновление выключено (`disableNppAutoUpdate.xml`).

Обновить: положить новый `npp.<версия>.portable.x64.zip`, сверить SHA-256 с файлом checksums релиза, поменять `NppZip` в `LinuxOps.csproj`, старый архив удалить.
