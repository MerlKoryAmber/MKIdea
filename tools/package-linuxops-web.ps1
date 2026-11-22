# Релиз LinuxOps Web: publish linux-x64 → dist\LinuxOps.Web-<ver>-linux-x64.tar.gz + .sha256.
# В releases/linuxops-web (Git LFS; оттуда берут get.sh и «Update» на серверах) — только по запросу, ключ -Publish.
# Примеры: .\tools\package-linuxops-web.ps1            — только dist\
#          .\tools\package-linuxops-web.ps1 -Publish   — плюс releases\linuxops-web и LATEST
#
# Проверки перед релизом (решение 18): SQLite и .NET лежат внутри сервиса (self-contained), dnf их не обновляет —
# на боевой исправления попадают только с нашим релизом.
#   1. Уязвимые NuGet-пакеты (в том числе транзитивные) — сборка останавливается.
#      Обойти — только с причиной: -AllowVulnerable "причина"; причину записать в отчёт версии.
#   2. .NET внутри сборки старее последнего исправления .NET 8 — предупреждение (обновить SDK или RuntimeFrameworkVersion).
#   3. Сторонний JS (guacamole-common-js) — SHA-256 файла сверяется с закреплённым в wwwroot/lib/guacamole/SOURCE.txt.
param([switch]$NoBuild, [switch]$Publish, [string]$AllowVulnerable = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\LinuxOps.Web\LinuxOps.Web.csproj'
$ver = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$dist = Join-Path $root "dist\LinuxOps.Web-$ver"

# 3. guacamole: файл в репозитории — тот, что взят из архива npm (sha512 архива — в SOURCE.txt); здесь — SHA-256 самого файла
$guacDir = Join-Path $root 'src\LinuxOps.Web\wwwroot\lib\guacamole'
$guacPin = [regex]::Match([IO.File]::ReadAllText((Join-Path $guacDir 'SOURCE.txt')), 'sha256 файла[^:\r\n]*:\s*([0-9a-f]{64})').Groups[1].Value
if (-not $guacPin) { throw 'В wwwroot/lib/guacamole/SOURCE.txt нет строки «sha256 файла: …»' }
$guacHash = (Get-FileHash (Join-Path $guacDir 'guacamole-common.min.js') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($guacHash -ne $guacPin) { throw "guacamole-common.min.js: SHA-256 $guacHash, а закреплён $guacPin — файл изменён" }
Write-Host "guacamole-common.min.js: SHA-256 совпал"

if (-not $NoBuild) {
    # 1. уязвимые пакеты — до сборки (нужен доступ к api.nuget.org)
    Write-Host 'Проверка уязвимых пакетов…'
    $json = dotnet list $proj package --vulnerable --include-transitive --format json 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package --vulnerable не выполнился:`n$json" }
    $vuln = @(($json | ConvertFrom-Json).projects | ForEach-Object { $_.frameworks } | Where-Object { $_ } | ForEach-Object {
        @($_.topLevelPackages) + @($_.transitivePackages) | Where-Object { $_ -and $_.vulnerabilities } | ForEach-Object {
            $p = $_; $p.vulnerabilities | ForEach-Object { "$($p.id) $($p.resolvedVersion): $($_.severity) $($_.advisoryurl)" }
        }
    })
    if ($vuln.Count -gt 0) {
        $list = ($vuln | Sort-Object -Unique) -join "`n  "
        if (-not $AllowVulnerable) { throw "Уязвимые пакеты:`n  $list`nОбновите их; выпустить всё равно — -AllowVulnerable ""причина"" (и запишите в отчёт версии)." }
        Write-Warning "Уязвимые пакеты пропущены (причина: $AllowVulnerable):`n  $list"
    } else { Write-Host 'Уязвимых пакетов нет' }

    Write-Host "Сборка $ver → $dist"
    dotnet publish $proj -c Release -r linux-x64 --self-contained true -p:DebugType=embedded -o $dist -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не прошла' }
}
foreach ($f in 'LinuxOps.Web', 'deploy\install.sh', 'deploy\VERSION') {
    if (-not (Test-Path (Join-Path $dist $f))) { throw "В $dist нет $f" }
}

# 2. .NET внутри сборки против последнего исправления .NET 8 (файл релизов Microsoft; нет сети — только предупреждение)
$rt = ((Get-Content (Join-Path $dist 'LinuxOps.Web.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks |
    Where-Object name -eq 'Microsoft.NETCore.App').version
try {
    $rel = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json' -TimeoutSec 20
    if ([version]$rt -lt [version]$rel.'latest-runtime') {
        Write-Warning ".NET внутри сборки $rt, последнее исправление .NET 8 — $($rel.'latest-runtime') от $($rel.'latest-release-date'). Обновите SDK (последний $($rel.'latest-sdk')) и пересоберите; в отчёте версии — отметить."
    } else { Write-Host ".NET внутри сборки $rt — последнее исправление" }
} catch { Write-Warning ".NET внутри сборки $rt; последнюю версию узнать не удалось: $($_.Exception.Message)" }
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

# Релиз в репозитории (как у MK Adm): releases/linuxops-web — архив (Git LFS), .sha256 и LATEST.
# Отсюда берут get.sh и меню сервера «Update». Только с -Publish: 100 МБ в LFS на каждую сборку не нужны. В папке — последние 5 версий.
if (-not $Publish) { Write-Host 'В releases\linuxops-web не выкладывалось (нужен -Publish).'; return }
$rel = Join-Path $root 'releases\linuxops-web'
New-Item -ItemType Directory -Force $rel | Out-Null
Copy-Item $out, "$out.sha256" $rel -Force
[IO.File]::WriteAllText((Join-Path $rel 'LATEST'), "$ver`n", (New-Object Text.UTF8Encoding $false))
Get-ChildItem $rel -Filter 'LinuxOps.Web-*-linux-x64.tar.gz' |
    Sort-Object { [version]($_.Name -replace '^LinuxOps\.Web-(.+)-linux-x64\.tar\.gz$', '$1') } -Descending |
    Select-Object -Skip 5 | ForEach-Object { Remove-Item $_.FullName, "$($_.FullName).sha256" -Force -ErrorAction SilentlyContinue }
Write-Host "В репозитории: releases\linuxops-web (LATEST = $ver)"
