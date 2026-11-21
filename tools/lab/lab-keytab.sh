#!/usr/bin/env bash
# Keytab служебной учётки лабы svc-lo-lab из пароля на stdin (зовёт Initialize-LabAd.ps1; пароль не пишется на диск).
set -euo pipefail
PRINC=svc-lo-lab@MERL.LOC
KT=/root/lab/svc-lo-lab.keytab
install -d -m 700 /root/lab
IFS= read -r pw; pw=${pw%$'\r'}
[[ -n "$pw" ]] || { echo "lab-keytab: empty password" >&2; exit 1; }
export KRB5CCNAME=FILE:/root/lab/krb5cc_setup
# AD только что сменил пароль — дать контроллеру мгновение
for i in 1 2 3 4 5; do printf '%s\n' "$pw" | kinit "$PRINC" >/dev/null 2>&1 && break; sleep 2; done
klist -s || { echo "lab-keytab: kinit with the new password failed" >&2; exit 1; }
kv=$(ldapsearch -LLL -Q -Y GSSAPI -H ldap://dc-01.merl.loc -b "DC=Merl,DC=loc" "(sAMAccountName=svc-lo-lab)" msDS-KeyVersionNumber | awk '/^msDS-KeyVersionNumber/{print $2}')
[[ -n "$kv" ]] || { echo "lab-keytab: no kvno" >&2; exit 1; }
rm -f "$KT"
printf 'addent -password -p %s -k %s -e aes256-cts-hmac-sha1-96\n%s\nwkt %s\nquit\n' "$PRINC" "$kv" "$pw" "$KT" | ktutil >/dev/null
unset pw
chmod 600 "$KT"
kdestroy
rm -f /root/lab/krb5cc_lab
KRB5CCNAME=FILE:/root/lab/krb5cc_lab kinit -kt "$KT" "$PRINC" && echo "keytab OK: $KT (kvno $kv)"
