# Релиз LinuxOps Web: publish linux-x64 → dist\LinuxOps.Web-<ver>-linux-x64.tar.gz + .sha256.
# Эти два файла выкладываются в релиз Git с тегом linuxops-web-v<ver>; сервер берёт их командой linuxops-web update.
# Пример: .\tools\package-linuxops-web.ps1
param([switch]$NoBuild)
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
foreach ($f in 'LinuxOps.Web', 'deploy\install.sh', 'deploy\VERSION') {
    if (-not (Test-Path (Join-Path $dist $f))) { throw "В $dist нет $f" }
}
# Пакеты в комплект (ставятся без сети). Скачиваются один раз в dist\vendor, сверяются по закреплённому SHA-256.
# OpenH264 (сборка Cisco, подпись EPEL): без неё guacd (FreeRDP 2) не подключает RDP, а CDN Cisco с серверов бывает недоступен.
# PowerShell 7 (Microsoft, MIT; LTS): консоль PowerShell и PowerCLI (Ф5). SHA-256 — из описания релиза на GitHub.
$vendorPkgs = @(
    @{
        Name = 'openh264-2.5.1-1.el10_1.x86_64.rpm'
        Url = 'https://codecs.fedoraproject.org/openh264/epel/10/x86_64/Packages/o/openh264-2.5.1-1.el10_1.x86_64.rpm'
        Sha256 = 'd4f002fd8ff2553b2d5080bd59d917f116847ba1aa4c3249cafa31b233a370c3'
    },
    @{
        Name = 'powershell-7.6.10-1.rh.x86_64.rpm'
        Url = 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.10/powershell-7.6.10-1.rh.x86_64.rpm'
        Sha256 = '77a2911f740db71bfd458a0fa9f5c7d134917fc146dae177ff846d82217afbcc'
    }
)
$vendor = Join-Path $dist 'deploy\vendor'
New-Item -ItemType Directory -Force $vendor | Out-Null
foreach ($pkg in $vendorPkgs) {
    $cache = Join-Path $root "dist\vendor\$($pkg.Name)"
    if (-not (Test-Path $cache)) {
        New-Item -ItemType Directory -Force (Split-Path $cache) | Out-Null
        Write-Host "Скачивание $($pkg.Name)"
        Invoke-WebRequest $pkg.Url -OutFile "$cache.part" -UseBasicParsing
        Move-Item -Force "$cache.part" $cache
    }
    if ((Get-FileHash $cache -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pkg.Sha256) {
        Remove-Item $cache
        throw "$($pkg.Name): SHA-256 не совпал — файл удалён, запустите сборку ещё раз"
    }
    Copy-Item $cache $vendor -Force
}

# Скрипты для Linux — только LF: CRLF ломает bash.
Get-ChildItem (Join-Path $dist 'deploy') -Recurse -File -Exclude VERSION, *.rpm | ForEach-Object {
    if ([IO.File]::ReadAllText($_.FullName).Contains("`r")) { throw "CRLF в $($_.FullName)" }
}

$name = "LinuxOps.Web-$ver-linux-x64.tar.gz"
$out = Join-Path $root "dist\$name"
# Внутри архива — одна папка с номером версии.
tar -czf $out -C (Join-Path $root 'dist') "LinuxOps.Web-$ver"
if ($LASTEXITCODE -ne 0) { throw 'Не удалось упаковать' }
$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$out.sha256", "$hash  $name`n", (New-Object Text.UTF8Encoding $false))
Write-Host "Готово: $out"
Write-Host "        $out.sha256"
Write-Host "Тег релиза: linuxops-web-v$ver"
