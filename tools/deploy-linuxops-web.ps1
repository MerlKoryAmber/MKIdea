# Выкладка LinuxOps Web на сервер: publish linux-x64 → копирование → перезапуск службы → проверка.
# Пример: .\tools\deploy-linuxops-web.ps1 -Target stand
# Target — имя из ~/.ssh/config (вход root по ключу). Данные (/var/lib/linuxops-web) и ключ (/etc/linuxops-web) не трогаются.
param(
    [string]$Target = 'stand',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\LinuxOps.Web\LinuxOps.Web.csproj'
$ver = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root "dist\LinuxOps.Web-$ver"

if (-not $NoBuild) {
    Write-Host "Сборка $ver → $dist"
    dotnet publish $proj -c Release -r linux-x64 --self-contained true -p:DebugType=embedded -o $dist -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не прошла' }
}
if (-not (Test-Path (Join-Path $dist 'LinuxOps.Web'))) { throw "Нет сборки в $dist" }

$tgz = Join-Path $env:TEMP "linuxops-web-$ver.tgz"
tar -czf $tgz -C $dist .
if ($LASTEXITCODE -ne 0) { throw 'Не удалось упаковать' }
scp -q $tgz "${Target}:/root/linuxops-web.tgz"
if ($LASTEXITCODE -ne 0) { throw 'Не удалось скопировать' }
Remove-Item $tgz

# Скрипт для сервера — через stdin, без кавычек PowerShell внутри.
$remote = @'
set -e
id linuxops-web >/dev/null 2>&1 || useradd -r -s /sbin/nologin linuxops-web
install -d -m 755 /opt/linuxops-web
install -d -o linuxops-web -g linuxops-web -m 700 /var/lib/linuxops-web /etc/linuxops-web
systemctl stop linuxops-web 2>/dev/null || true
find /opt/linuxops-web -mindepth 1 -delete
tar -xzf /root/linuxops-web.tgz -C /opt/linuxops-web && rm -f /root/linuxops-web.tgz
chown -R root:root /opt/linuxops-web && chmod 755 /opt/linuxops-web/LinuxOps.Web
cp /opt/linuxops-web/deploy/linuxops-web.service /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now linuxops-web
for i in $(seq 1 20); do curl -s -o /dev/null http://127.0.0.1:5080/ && break; sleep 1; done
echo "служба: $(systemctl is-active linuxops-web)"
curl -sk -o /dev/null -w "https: %{http_code}\n" https://127.0.0.1/ || echo "https: нет (nginx не настроен?)"
journalctl -t linuxops-web -n 1 --no-pager -o cat
'@
# Файлом, а не через stdin: PowerShell 5.1 добавляет в поток BOM и CRLF.
$sh = Join-Path $env:TEMP 'linuxops-web-deploy.sh'
[IO.File]::WriteAllText($sh, ($remote -replace "`r", ''), (New-Object Text.UTF8Encoding $false))
scp -q $sh "${Target}:/root/linuxops-web-deploy.sh"
Remove-Item $sh
ssh $Target 'bash /root/linuxops-web-deploy.sh; rc=$?; rm -f /root/linuxops-web-deploy.sh; exit $rc'
if ($LASTEXITCODE -ne 0) { throw 'Выкладка на сервере не прошла' }
