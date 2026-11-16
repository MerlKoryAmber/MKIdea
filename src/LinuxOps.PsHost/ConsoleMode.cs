using System.Management.Automation.Runspaces;
using Microsoft.PowerShell;

namespace LinuxOps.PsHost;

/// <summary>
/// Вкладка PowerShell: настоящая консоль PowerShell (та же, что в pwsh.exe и терминале VS Code) — LinuxOps запускает
/// помощник с --console внутри псевдоконсоли Windows (ConPTY) и рисует её своим эмулятором xterm.
/// Отсюда — PSReadLine (подсказки, Tab, история), вывод внешних программ, Ctrl+C, cls, ReadKey — как в обычной консоли.
/// </summary>
public static class ConsoleMode
{
    public static int Run(string[] args)
    {
        // Модули: Modules рядом с LinuxOps.exe (PowerCLI офлайн) — первыми; pwsh\Modules ($PSHOME) — встроенные и PSReadLine.
        var user = PsEngine.UserModules;
        Directory.CreateDirectory(user);
        var path = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
        if (!path.Split(';').Contains(user, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PSModulePath", user + ";" + path);

        var iss = InitialSessionState.CreateDefault2();
        // Подписи модулей не проверяем: без интернета проверка отзыва сертификатов вешает Import-Module на минуты.
        iss.ExecutionPolicy = ExecutionPolicy.Bypass;
        // --suggest <канал>: список подсказок рисует LinuxOps (как в терминале VS Code), сюда приходят только правки строки.
        string? pipe = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--suggest" && i + 1 < args.Length) pipe = args[++i];
            else rest.Add(args[i]);
        }

        CompletionPredictor.Register(pipe);
        // После профиля пользователя: серый хвост из истории (→ — принять; F2 — список PSReadLine), дополнения — списком LinuxOps;
        // prompt (свой или из профиля) дополнительно сообщает папку — подсказки путей идут от неё.
        const string setup = """
            if (Get-Module PSReadLine) { Set-PSReadLineOption -PredictionSource HistoryAndPlugin -PredictionViewStyle InlineView }
            # cls / Ctrl+L: псевдоконсоль стирает только видимый экран — старый вывод оставался выше при прокрутке.
            # ESC[3J она пропускает: очищаем и историю прокрутки, как в Windows Terminal.
            $global:__linuxopsClear = ${function:Clear-Host}
            function global:Clear-Host { & $global:__linuxopsClear; [Console]::Write("`e[3J") }
            if (Get-Module PSReadLine) {
                Set-PSReadLineKeyHandler -Chord Ctrl+l -BriefDescription ClearScreen -Description 'Очистить экран и прокрутку' -ScriptBlock {
                    [Microsoft.PowerShell.PSConsoleReadLine]::ClearScreen(); [Console]::Write("`e[3J")
                }
            }
            $global:__linuxopsPrompt = $function:prompt
            function global:prompt {
                [LinuxOps.PsHost.CompletionPredictor]::Location = $executionContext.SessionState.Path.CurrentFileSystemLocation.ProviderPath
                & $global:__linuxopsPrompt
            }
            # Окно Out-GridView во встроенном PowerShell не работает (и роняло консоль) — замена в консоли:
            # таблица, а с -PassThru / -OutputMode — выбор строк по номерам.
            function global:Out-GridView {
                [CmdletBinding()]
                param(
                    [Parameter(ValueFromPipeline)] $InputObject,
                    [string] $Title,
                    [switch] $PassThru,
                    [ValidateSet('None', 'Single', 'Multiple')] [string] $OutputMode = 'None',
                    [switch] $Wait
                )
                begin { $items = [System.Collections.Generic.List[object]]::new() }
                process { if ($null -ne $InputObject) { $items.Add($InputObject) } }
                end {
                    if ($Title) { Write-Host $Title -ForegroundColor Cyan }
                    if (-not $PassThru -and $OutputMode -eq 'None') { $items | Format-Table -AutoSize | Out-Host; return }
                    $i = 0
                    $items | ForEach-Object { $i++; [pscustomobject]@{ '№' = $i; 'Строка' = ($_ | Out-String -Width 200).Trim() } } | Format-Table -AutoSize -Wrap | Out-Host
                    $hint = if ($OutputMode -eq 'Single') { 'Номер строки (пусто — отмена)' } else { 'Номера через запятую или диапазон 2-5, * — все (пусто — отмена)' }
                    $answer = Read-Host $hint
                    if ([string]::IsNullOrWhiteSpace($answer)) { return }
                    if ($answer.Trim() -eq '*') { return $items }
                    $picked = foreach ($part in $answer -split '[,; ]+') {
                        if ($part -match '^(\d+)-(\d+)$') { [int]$Matches[1]..[int]$Matches[2] } elseif ($part -match '^\d+$') { [int]$part }
                    }
                    if ($OutputMode -eq 'Single') { $picked = $picked | Select-Object -First 1 }
                    foreach ($n in $picked) { if ($n -ge 1 -and $n -le $items.Count) { $items[$n - 1] } }
                }
            }
            """;
        var shellArgs = new List<string> { "-NoLogo" };
        shellArgs.AddRange(rest);
        shellArgs.AddRange(new[] { "-NoExit", "-Command", setup });
        return ConsoleShell.Start(iss, "", "", shellArgs.ToArray());
    }
}
