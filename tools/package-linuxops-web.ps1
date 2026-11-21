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
# Скрипты для Linux — только LF: CRLF ломает bash.
Get-ChildItem (Join-Path $dist 'deploy') -Recurse -File -Exclude VERSION | ForEach-Object {
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
