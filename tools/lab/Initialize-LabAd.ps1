# Разовая настройка автоматизации AD для лабы Merl.loc (запускает владелец, один раз; повтор — смена ключа).
# Окно Windows спрашивает пароль администратора домена — скрипт его не сохраняет и не показывает.
# Делает:
#   OU=LinuxOps-Lab — сюда агент кладёт тестовые учётки и группы;
#   служебная учётка svc-lo-lab: случайный пароль (никто не видит), полный доступ к OU=LinuxOps-Lab, AES;
#   на стенде (ssh stand): keytab /root/lab/svc-lo-lab.keytab и команда /usr/local/sbin/lab-ad.
# Дальше агент создаёт тестовое в AD сам: ssh stand lab-ad user|group|member|del|ls.
param([string]$Dc = '192.168.0.175', [string]$Base = 'DC=Merl,DC=loc', [string]$Stand = 'stand')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.DirectoryServices

$cred = Get-Credential -UserName 'MERL\merlkory' -Message 'Администратор домена Merl.loc — разовая настройка автоматизации лабы'
if (-not $cred) { exit 2 }
$net = $cred.GetNetworkCredential()
$auth = [DirectoryServices.AuthenticationTypes]::Secure -bor [DirectoryServices.AuthenticationTypes]::Sealing -bor [DirectoryServices.AuthenticationTypes]::Signing
function Entry([string]$dn) { New-Object DirectoryServices.DirectoryEntry("LDAP://$Dc/$dn", $cred.UserName, $net.Password, $auth) }
function Find([string]$filter) {
    $s = New-Object DirectoryServices.DirectorySearcher((Entry $Base), $filter)
    $r = $s.FindOne(); if ($r) { $r.GetDirectoryEntry() } else { $null }
}

# 1. OU
$ouDn = "OU=LinuxOps-Lab,$Base"
$ou = Find '(&(objectClass=organizationalUnit)(ou=LinuxOps-Lab))'
if (-not $ou) {
    $ou = (Entry $Base).Children.Add('OU=LinuxOps-Lab', 'organizationalUnit')
    $ou.Properties['description'].Value = 'Тестовые объекты агента (LinuxOps Web и др.)'
    $ou.CommitChanges(); Write-Host 'OU: LinuxOps-Lab'
}

# 2. Служебная учётка, новый случайный пароль
$rng = New-Object Security.Cryptography.RNGCryptoServiceProvider
$alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
$bytes = New-Object byte[] 32; $rng.GetBytes($bytes)
$pw = 'Lo#9' + (-join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] }))
$svc = Find '(&(objectCategory=person)(sAMAccountName=svc-lo-lab))'
if (-not $svc) {
    $svc = (Entry "CN=Users,$Base").Children.Add('CN=svc-lo-lab', 'user')
    $svc.Properties['sAMAccountName'].Value = 'svc-lo-lab'
    $svc.Properties['userPrincipalName'].Value = 'svc-lo-lab@merl.loc'
    $svc.Properties['description'].Value = 'Агент: тестовые объекты в OU=LinuxOps-Lab (keytab на стенде 192.168.0.169)'
    $svc.CommitChanges(); Write-Host 'учётка: svc-lo-lab'
}
$svc.Invoke('SetPassword', $pw)
$svc.Properties['userAccountControl'].Value = 0x200 -bor 0x10000
$svc.Properties['msDS-SupportedEncryptionTypes'].Value = 24
$svc.CommitChanges()

# 3. Полный доступ svc-lo-lab к OU (с наследованием)
$sid = New-Object Security.Principal.SecurityIdentifier($svc.Properties['objectSid'].Value, 0)
$ou = Entry $ouDn
# PowerShell 5.1 прячет свойства DirectoryEntry за атрибутами LDAP — они в .psbase
$ou.psbase.Options.SecurityMasks = [DirectoryServices.SecurityMasks]::Dacl
$rule = New-Object DirectoryServices.ActiveDirectoryAccessRule($sid, 'GenericAll', 'Allow', [DirectoryServices.ActiveDirectorySecurityInheritance]::All)
$ou.psbase.ObjectSecurity.AddAccessRule($rule); $ou.psbase.CommitChanges()
Write-Host 'права: svc-lo-lab → OU=LinuxOps-Lab (полный доступ)'

# 4. Стенд: скрипты и keytab (пароль — в stdin ssh, на диск не пишется)
$here = $PSScriptRoot
scp -q (Join-Path $here 'lab-ad.sh') (Join-Path $here 'lab-keytab.sh') "${Stand}:/root/lab/"
if ($LASTEXITCODE -ne 0) { throw 'не удалось скопировать скрипты на стенд' }
ssh $Stand 'install -m 700 /root/lab/lab-ad.sh /usr/local/sbin/lab-ad; chmod 700 /root/lab/lab-keytab.sh'
$pw | ssh $Stand 'bash /root/lab/lab-keytab.sh'
$pw = $null
if ($LASTEXITCODE -ne 0) { throw 'keytab на стенде не создан' }
ssh $Stand 'lab-ad check'
Write-Host 'Готово: агент создаёт тестовое в AD сам (ssh stand lab-ad ...).'
