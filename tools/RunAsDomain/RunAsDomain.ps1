# Запуск AdServerGroups с сетевыми учётными данными домена (ПК в workgroup).
# Показывает Windows Credential UI, затем стартует exe через Start-Process -Credential.
param(
    [string]$Exe = "C:\Code\MKADIdea\dist\AdServerGroups-0.2.6\AdServerGroups.exe",
    [string]$User = "MERL\merlkory"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms | Out-Null

if (-not (Test-Path -LiteralPath $Exe)) {
    [System.Windows.Forms.MessageBox]::Show("Не найден exe:`n$Exe") | Out-Null
    exit 1
}

$cred = $host.ui.PromptForCredential(
    "AD Server Groups — доменный запуск",
    "Учётка должна быть локальным админом на EXCHANGE и других серверах.",
    $User,
    "Merl.loc")

if ($null -eq $cred) { exit 2 }

Get-Process AdServerGroups -ErrorAction SilentlyContinue | Stop-Process -Force

$dir = Split-Path -Parent $Exe
try {
    $p = Start-Process -FilePath $Exe -WorkingDirectory $dir -Credential $cred -PassThru
    Start-Sleep -Seconds 1
    if (-not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) {
        throw "Процесс сразу завершился (часто: на workgroup ПК нельзя Start-Process -Credential для доменной учётки без доверия к DC)."
    }
    [System.Windows.Forms.MessageBox]::Show("OK PID $($p.Id)`n$($cred.UserName)") | Out-Null
}
catch {
    # Fallback: runas /netonly — процесс локальный, сеть = домен
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "$env:SystemRoot\System32\runas.exe"
    $psi.Arguments = "/netonly /user:$($cred.UserName) `"$Exe`""
    $psi.UseShellExecute = $true
    [System.Windows.Forms.MessageBox]::Show(
        "Start-Process -Credential не сработал:`n$($_.Exception.Message)`n`nОткроется окно runas /netonly — введите пароль ещё раз.") | Out-Null
    [Diagnostics.Process]::Start($psi) | Out-Null
}
