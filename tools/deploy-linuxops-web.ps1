# Выкладка LinuxOps Web на сервер с этого ПК (разработка; на боевом — linuxops-web update из релиза Git).
# Собирает релиз (package-linuxops-web.ps1), копирует архив и ставит:
#   есть меню на сервере — linuxops-web update --file; нет — первая установка deploy/install.sh.
# Пример: .\tools\deploy-linuxops-web.ps1 -Target stand
# Target — имя из ~/.ssh/config (вход root по ключу). Данные и мастер-ключ на сервере не трогаются.
param(
    [string]$Target = 'stand',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\LinuxOps.Web\LinuxOps.Web.csproj'
$ver = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

& (Join-Path $PSScriptRoot 'package-linuxops-web.ps1') -NoBuild:$NoBuild
$name = "LinuxOps.Web-$ver-linux-x64.tar.gz"
$tgz = Join-Path $root "dist\$name"

scp -q $tgz "$tgz.sha256" "${Target}:/var/tmp/"
if ($LASTEXITCODE -ne 0) { throw 'Не удалось скопировать' }

# Скрипт для сервера — файлом: PowerShell 5.1 портит stdin (BOM, CRLF).
$remote = @"
set -e
f=/var/tmp/$name
if [ -x /usr/bin/linuxops-web ] && [ -L /opt/linuxops-web/current ]; then
  linuxops-web update --file "`$f"
else
  d=`$(mktemp -d /var/tmp/linuxops-web-install.XXXXXX)
  tar -xzf "`$f" -C "`$d"
  bash "`$d/LinuxOps.Web-$ver/deploy/install.sh"
  rm -rf "`$d"
fi
rm -f "`$f" "`$f.sha256"
linuxops-web status
"@
$sh = Join-Path $env:TEMP 'linuxops-web-deploy.sh'
[IO.File]::WriteAllText($sh, ($remote -replace "`r", ''), (New-Object Text.UTF8Encoding $false))
scp -q $sh "${Target}:/var/tmp/linuxops-web-deploy.sh"
Remove-Item $sh
ssh $Target 'bash /var/tmp/linuxops-web-deploy.sh; rc=$?; rm -f /var/tmp/linuxops-web-deploy.sh; exit $rc'
if ($LASTEXITCODE -ne 0) { throw 'Выкладка на сервере не прошла' }
