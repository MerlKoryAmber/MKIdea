# Сборщик контекста: статус проекта одним вызовом в начале сессии агента (вместо 5–10 мелких чтений).
# Только чтение. Без сети: сверка с origin — по последнему fetch; свежая — с ключом -Fetch.
# Запуск: powershell -NoProfile -ExecutionPolicy Bypass -File tools\rehydrate.ps1 [-Fetch] [-Lines 25]

param(
    [switch]$Fetch,
    [int]$Lines = 25
)

$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Section([string]$title) { ""; "## $title" }
function Invoke-Git { $out = & git @args 2>$null; if ($LASTEXITCODE -eq 0) { $out } }

"# Rehydrate — $(Split-Path -Leaf $root) · $((Get-Date).ToUniversalTime().AddHours(3).ToString('yyyy-MM-dd HH:mm')) МСК"

# ---------- Git ----------
Section "Git"
if ($Fetch) { git fetch --quiet 2>$null }
$branch = Invoke-Git rev-parse --abbrev-ref HEAD
$upstream = Invoke-Git rev-parse --abbrev-ref '@{u}'
$line = "ветка: $branch"
if ($upstream) {
    $counts = (Invoke-Git rev-list --left-right --count "HEAD...@{u}") -split '\s+'
    $line += " · ${upstream}: впереди $($counts[0]), позади $($counts[1])"
    $fetchHead = Join-Path $root ".git\FETCH_HEAD"
    if (-not $Fetch -and (Test-Path $fetchHead)) {
        $age = (Get-Date) - (Get-Item $fetchHead).LastWriteTime
        $line += " (fetch $([int]$age.TotalHours) ч назад; свежая сверка — -Fetch)"
    }
}
$line
$main = @("main", "master") | Where-Object { Invoke-Git rev-parse --verify --quiet "refs/heads/$_" } | Select-Object -First 1
if ($main -and $branch -ne $main) {
    $diff = (Invoke-Git rev-list --left-right --count "$main...HEAD") -split '\s+'
    "относительно ${main}: в ветке новых $($diff[1]), в $main новых $($diff[0])"
}
$status = @(Invoke-Git status --short)
if ($status.Count -eq 0) { "рабочая копия чистая" }
else {
    "незакоммичено: $($status.Count)"
    $status | Select-Object -First 15 | ForEach-Object { "  $_" }
    if ($status.Count -gt 15) { "  …" }
}
$stash = @(Invoke-Git stash list)
if ($stash.Count -gt 0) { "stash: $($stash.Count)" }
"последние коммиты:"
Invoke-Git log -5 --format="  %h %cr · %s" | ForEach-Object { $_ }

# ---------- Приложения: версия, релиз, верхняя запись CURRENT ----------
$apps = Get-ChildItem (Join-Path $root "docs") -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName "CURRENT.md") }
foreach ($app in $apps) {
    $proj = Get-ChildItem (Join-Path $root "src") -Filter *.csproj -Recurse -Depth 2 -ErrorAction SilentlyContinue |
        Where-Object { $_.BaseName -ieq $app.Name -and $_.FullName -notmatch '\\(bin|obj)\\' } | Select-Object -First 1
    $version = ""
    if ($proj) {
        $m = Select-String -Path $proj.FullName -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if ($m) { $version = $m.Matches[0].Groups[1].Value }
    }
    $dist = Get-ChildItem (Join-Path $root "dist") -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$($proj.BaseName)-*" } |
        Sort-Object { try { [version]($_.Name -replace '^.*-', '') } catch { [version]"0.0" } } | Select-Object -Last 1

    Section "$($app.Name)$(if ($version) { " · версия $version" })$(if ($dist) { " · dist: $($dist.Name)" })"
    $current = Join-Path $app.FullName "CURRENT.md"
    $text = Get-Content $current -Encoding UTF8
    $start = ($text | Select-String -Pattern '^## ' | Select-Object -First 1).LineNumber
    if ($start) {
        $end = ($text | Select-Object -Skip $start | Select-String -Pattern '^## ' | Select-Object -First 1).LineNumber
        $entry = if ($end) { $text[($start - 1)..($start + $end - 2)] } else { $text[($start - 1)..($text.Count - 1)] }
    }
    else {
        $entry = $text
    }

    $entry = @($entry | Where-Object { $_.Trim() })
    $entry | Select-Object -First $Lines | ForEach-Object { if ($_.Length -gt 400) { $_.Substring(0, 400) + "…" } else { $_ } }
    if ($entry.Count -gt $Lines) { "… (ещё $($entry.Count - $Lines) строк — docs\$($app.Name)\CURRENT.md)" }
    "CURRENT.md: $([int]((Get-Item $current).Length / 1KB)) КБ — дальше читать диапазонами"

    # Хвосты: «👀 Требует ручной проверки» из последнего отчёта приложения
    $report = Get-ChildItem (Join-Path $root "docs\agent_reports") -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$($app.Name)-*" -and (Test-Path (Join-Path $_.FullName "REPORT.md")) } |
        Sort-Object { try { [version]($_.Name -replace '^.*-', '') } catch { [version]"0.0" } } | Select-Object -Last 1
    if ($report) {
        $r = Get-Content (Join-Path $report.FullName "REPORT.md") -Encoding UTF8
        $i = ($r | Select-String -Pattern '^## 👀' | Select-Object -First 1).LineNumber
        if ($i) {
            $tail = @($r | Select-Object -Skip $i | ForEach-Object { $_ } | Where-Object { $_.Trim() } )
            $stop = 0; while ($stop -lt $tail.Count -and $tail[$stop] -notmatch '^## ') { $stop++ }
            $tail = if ($stop -gt 0) { $tail[0..($stop - 1)] } else { @() }
            if ($tail.Count -gt 0 -and ($tail -join ' ') -notmatch 'Не требуется') {
                "хвосты (docs\agent_reports\$($report.Name)):"
                $tail | Select-Object -First 8 | ForEach-Object { "  $_" }
            }
        }
    }
}

# ---------- Карта docs ----------
Section "Docs"
Get-ChildItem (Join-Path $root "docs") -Recurse -File -Include *.md -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\agent_reports\\' } |
    ForEach-Object { "  $($_.FullName.Substring($root.Length + 1)) · $([math]::Max(1, [int]($_.Length / 1KB))) КБ" }
$reports = @(Get-ChildItem (Join-Path $root "docs\agent_reports") -Directory -ErrorAction SilentlyContinue)
if ($reports.Count -gt 0) { "  docs\agent_reports\ · отчётов: $($reports.Count)" }
