#!/usr/bin/env bash
# lab-ad — тестовые учётки и группы AD лабы Merl.loc без участия человека.
# Работает от служебной учётки svc-lo-lab (keytab, как SSH-ключ); права у неё — только на OU=LinuxOps-Lab.
# Ставится на стенд в /usr/local/sbin/lab-ad (tools/lab/Initialize-LabAd.ps1).
#   lab-ad user NAME [DISPLAY]     создать или сменить пароль; печатает «NAME<TAB>пароль»
#   lab-ad group NAME              создать группу безопасности
#   lab-ad member GROUP MEMBER     добавить в группу (учётку или группу)
#   lab-ad unmember GROUP MEMBER   убрать из группы
#   lab-ad del NAME                удалить (только объекты из OU=LinuxOps-Lab)
#   lab-ad ls                      что есть в OU
#   lab-ad check                   проверить keytab и доступ
set -euo pipefail
PRINC=svc-lo-lab@MERL.LOC
KT=/root/lab/svc-lo-lab.keytab
H=ldap://dc-01.merl.loc
BASE="DC=Merl,DC=loc"
OU="OU=LinuxOps-Lab,$BASE"
export KRB5CCNAME=FILE:/root/lab/krb5cc_lab

die() { echo "lab-ad: $*" >&2; exit 1; }
[[ -f $KT ]] || die "no keytab $KT — run tools/lab/Initialize-LabAd.ps1 once"
klist -s 2>/dev/null || kinit -kt "$KT" "$PRINC" || die "kinit failed"

# GSSAPI с шифрованием (sealing): без него AD не даёт задать пароль по 389.
M() { ldapmodify -Q -Y GSSAPI -H "$H" "$@" >/dev/null; }
S() { ldapsearch -LLL -Q -o ldif-wrap=no -Y GSSAPI -H "$H" "$@"; }
dn_of() { S -b "$BASE" "(sAMAccountName=$1)" dn | sed -n 's/^dn: //p' | head -1; }
in_ou() { [[ "${1,,}" == *",${OU,,}" ]]; }
pw_gen() { printf 'Lab%s#%d' "$(tr -dc 'A-HJ-NP-Za-km-z2-9' </dev/urandom | head -c 14)" $((RANDOM % 90 + 10)); }
pw_b64() { printf '"%s"' "$1" | iconv -f UTF-8 -t UTF-16LE | base64 -w0; }
name_ok() { [[ "$1" =~ ^[A-Za-z0-9._-]{1,20}$ ]] || die "bad name: $1"; }

cmd_user() {
  local n="$1" disp="${2:-$1}" pw dn; name_ok "$n"
  pw=$(pw_gen); dn=$(dn_of "$n")
  if [[ -z "$dn" ]]; then
    M -a <<EOF
dn: CN=$n,$OU
objectClass: user
sAMAccountName: $n
userPrincipalName: $n@merl.loc
displayName: $disp
description: LinuxOps lab test (lab-ad)
unicodePwd:: $(pw_b64 "$pw")
userAccountControl: 66048
EOF
  else
    in_ou "$dn" || die "$n exists outside $OU — not touching"
    M <<EOF
dn: $dn
changetype: modify
replace: unicodePwd
unicodePwd:: $(pw_b64 "$pw")
EOF
  fi
  printf '%s\t%s\n' "$n" "$pw"
}

cmd_group() {
  local n="$1"; name_ok "$n"
  [[ -n "$(dn_of "$n")" ]] && { echo "exists: $n"; return; }
  M -a <<EOF
dn: CN=$n,$OU
objectClass: group
sAMAccountName: $n
description: LinuxOps lab test (lab-ad)
groupType: -2147483646
EOF
  echo "group: $n"
}

cmd_member() {
  local op="$1" g m; g=$(dn_of "$2"); m=$(dn_of "$3")
  [[ -n "$g" && -n "$m" ]] || die "not found: $2 or $3"
  in_ou "$g" || die "group $2 is outside $OU"
  M <<EOF || [[ $op == delete ]]
dn: $g
changetype: modify
$op: member
member: $m
EOF
  echo "$op: $3 -> $2"
}

cmd_del() {
  local dn; dn=$(dn_of "$1"); [[ -n "$dn" ]] || { echo "absent: $1"; return; }
  in_ou "$dn" || die "$1 is outside $OU — not deleting"
  ldapdelete -Q -Y GSSAPI -H "$H" "$dn"; echo "deleted: $1"
}

case "${1:-}" in
  user) shift; cmd_user "$@" ;;
  group) cmd_group "${2:?}" ;;
  member) cmd_member add "${2:?}" "${3:?}" ;;
  unmember) cmd_member delete "${2:?}" "${3:?}" ;;
  del) cmd_del "${2:?}" ;;
  ls) S -b "$OU" "(|(objectClass=user)(objectClass=group))" sAMAccountName memberOf | grep -E '^(sAMAccountName|memberOf):' ;;
  check) klist | sed -n '1,3p'; S -b "$OU" -s base "(objectClass=*)" dn ;;
  *) sed -n '2,13p' "$0"; exit 1 ;;
esac
