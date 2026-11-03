# Сборка self-contained single-file exe + опциональные Providers\ рядом с exe.
# Перед релизом поднимите Version в src\AdServerGroups\AdServerGroups.csproj.

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\AdServerGroups\AdServerGroups.csproj"
$sqlProject = Join-Path $root "src\AdServerGroups.SqlProvider\AdServerGroups.SqlProvider.csproj"
$outDir = Join-Path $root "dist"

[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "0.0.0" }

$publishDir = Join-Path $outDir "AdServerGroups-$version"
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -o $publishDir

$exe = Join-Path $publishDir "AdServerGroups.exe"
if (-not (Test-Path $exe)) { throw "Exe not found: $exe" }

$defaults = Join-Path $publishDir "AdServerGroups.defaults.json"
if (-not (Test-Path $defaults)) {
    $srcDefaults = Join-Path $root "src\AdServerGroups\AdServerGroups.defaults.json"
    if (Test-Path $srcDefaults) { Copy-Item $srcDefaults $defaults -Force }
}
if (-not (Test-Path $defaults)) { throw "Defaults JSON missing: $defaults" }
Write-Host "Defaults    : $defaults"

# SQL-провайдер — отдельная DLL в Providers\ (можно не класть → вкладка SQL скрыта)
$providersDir = Join-Path $publishDir "Providers"
New-Item -ItemType Directory -Force -Path $providersDir | Out-Null
$providerOut = Join-Path $outDir "_provider-build"
if (Test-Path $providerOut) { Remove-Item $providerOut -Recurse -Force }
dotnet publish $sqlProject -c $Configuration -o $providerOut
Copy-Item (Join-Path $providerOut "*") $providersDir -Recurse -Force
# Abstractions уже в host; дубликат в Providers оставляем — load context предпочитает host

$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
Write-Host "Published: $exe"
Write-Host ("ProductName : {0}" -f $info.ProductName)
Write-Host ("CompanyName : {0}" -f $info.CompanyName)
Write-Host ("FileVersion : {0}" -f $info.FileVersion)
Write-Host ("ProductVersion : {0}" -f $info.ProductVersion)
Write-Host ("FileDescription : {0}" -f $info.FileDescription)
Write-Host ("Providers   : {0}" -f $providersDir)
Get-ChildItem $providersDir -Filter "*.dll" | ForEach-Object { Write-Host ("  DLL {0}" -f $_.Name) }
