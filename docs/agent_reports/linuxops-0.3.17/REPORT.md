# LinuxOps 0.3.17 — sudo на SUSE и проверка не-root

Статус: РЕАЛИЗОВАНО НО НЕ ПРИНЯТО.

## Изменено

- `Services/UserProvisioning.cs` около строки с `LINUXOPS_SUDO_MODE=file`: в `/etc/sudoers.d/linuxops-<login>` первой строкой пишется `Defaults:<login> !targetpw`, затем `login ALL=(ALL) ALL`. Повторный запуск считает это «уже есть», только если обе строки на месте.
- `LinuxOps.csproj` — 0.3.17. `docs/linuxops/CURRENT.md` — строка версии.

Другие файлы не менялись.

## Проверено

- `dotnet build -c Release -warnaserror`: 0 ошибок, 0 предупреждений.
- Лаба CentOS `192.168.0.178`, скрипт в режиме file: файл sudoers содержит `Defaults:linuxops-file !targetpw` и правило `ALL`. Учётка и файл удалены.
- `linuxops-pw` (wheel, пароль): «Проверить обновления» exit 0, пароля в выводе нет. «Добавить пользователя» — `создан, ключ добавлен`, пароля в строке нет.
- `linuxops-np` (NOPASSWD, вход по паролю): `sudo -n true` на хосте успешен. «Проверить обновления» exit 0, в выводе нет пароля и нет `command not found`. «Добавить пользователя» — создан. Учётки сняты.

## Не сделано

- Хост openSUSE в лабе нет, поэтому `sudo id` именно под `targetpw` не смотрел. На CentOS проверены содержимое файла и оба сценария sudo (пароль и NOPASSWD).
