#!/usr/bin/env bash
# Общие функции install / update / uninstall / меню LinuxOps Web.
# Подключается через source; пути и константы — здесь, в одном месте.
# shellcheck disable=SC2034

set -euo pipefail

APP_DIR=/opt/linuxops-web
RELEASES_DIR=$APP_DIR/releases
CURRENT_LINK=$APP_DIR/current
PREVIOUS_FILE=$APP_DIR/previous
DATA_DIR=/var/lib/linuxops-web
ETC_DIR=/etc/linuxops-web
PKI_DIR=/etc/pki/linuxops-web
BACKUP_DIR=/var/backups/linuxops-web
SVC_USER=linuxops-web
REC_DIR=/var/lib/linuxops-web-rec
UNIT=linuxops-web
UNIT_FILE=/etc/systemd/system/linuxops-web.service
NGINX_CONF=/etc/nginx/conf.d/linuxops-web.conf
CLI=/usr/bin/linuxops-web
RELEASE_CONF=$ETC_DIR/release.conf
TOKEN_FILE=$ETC_DIR/git-token
LOCAL_URL=http://127.0.0.1:5080/

if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then green=$'\033[0;32m'; red=$'\033[0;31m'; plain=$'\033[0m'; else green=""; red=""; plain=""; fi

log()  { printf '[linuxops-web] %s\n' "$*"; }
warn() { printf '[linuxops-web] WARN: %s\n' "$*" >&2; }
die()  { printf '[linuxops-web] ERROR: %s\n' "$*" >&2; exit 1; }
have_cmd() { command -v "$1" >/dev/null 2>&1; }
need_root() { [[ "${EUID}" -eq 0 ]] || die "root required (use sudo). uid=${EUID}"; }

# Ввод: /dev/tty, если он реально есть (в pipe/агенте его нет), иначе stdin.
_tty_ok() { { printf '' >/dev/tty; } 2>/dev/null; }
ask() { # ask VAR "prompt" [default]
  local __v __p="$2" __d="${3:-}"
  [[ -n "$__d" ]] && __p="$__p [$__d]"
  if _tty_ok; then read -r -p "$__p: " __v </dev/tty || true; else read -r -p "$__p: " __v || true; fi
  printf -v "$1" '%s' "${__v:-$__d}"
}
ask_secret() { # ask_secret VAR "prompt"
  local __v
  if _tty_ok; then read -r -s -p "$2: " __v </dev/tty || true; else read -r -s -p "$2: " __v || true; fi
  echo >&2
  printf -v "$1" '%s' "$__v"
}
confirm() { # confirm "question" → 0 = yes; LINUXOPS_WEB_ASSUME_YES=1 — без вопроса
  [[ "${LINUXOPS_WEB_ASSUME_YES:-}" == 1 ]] && return 0
  local a; ask a "$1 [y/N]"
  [[ "$a" =~ ^[yY]([eE][sS])?$ ]]
}

# Версия релиза: deploy/VERSION, иначе имя каталога.
release_version() {
  local d="$1"
  if [[ -f "$d/deploy/VERSION" ]]; then tr -d ' \r\n' <"$d/deploy/VERSION"; else basename "$d" | sed -E 's/^LinuxOps\.Web-//'; fi
}
current_version() { [[ -L "$CURRENT_LINK" ]] && basename "$(readlink -f "$CURRENT_LINK")" || echo "-"; }
previous_version() { [[ -f "$PREVIOUS_FILE" ]] && basename "$(cat "$PREVIOUS_FILE")" || echo "-"; }

health_check() { # health_check [seconds]
  local i n="${1:-30}"
  for ((i = 0; i < n; i++)); do
    curl -fs -o /dev/null --max-time 3 "$LOCAL_URL" && return 0
    sleep 1
  done
  return 1
}

# Обёртка /usr/bin/linuxops-web: меню всегда из текущего релиза.
ensure_cli() {
  cat >"$CLI" <<EOF
#!/bin/bash
# LinuxOps Web — меню управления. Логика: $CURRENT_LINK/deploy/linuxops-web.sh
exec bash $CURRENT_LINK/deploy/linuxops-web.sh "\$@"
EOF
  chmod 755 "$CLI"
}

# Имена и адреса сервера — для сертификата.
server_names() { { hostname -s; hostname -f 2>/dev/null; } | sort -u | grep -v '^localhost' || true; }
server_ips() { hostname -I 2>/dev/null | tr ' ' '\n' | grep -E '^[0-9]+\.' || true; }
san_list() {
  local s="" n
  for n in $(server_names); do s+="DNS:$n,"; done
  for n in $(server_ips); do s+="IP:$n,"; done
  [[ -n "${1:-}" ]] && s+="$1,"
  echo "${s%,}"
}

# Свой CA сервера: подписывает только имена и адреса этого сервера (nameConstraints),
# поэтому его можно добавить в «Доверенные корневые» без риска для чужих сайтов.
local_ca_ensure() {
  local ca=$PKI_DIR/ca perm="" n
  [[ -f $ca/ca.key ]] && return 0
  install -d -m 700 "$ca"
  for n in $(server_names); do perm+="permitted;DNS:$n,"; done
  for n in $(server_ips); do perm+="permitted;IP:$n/255.255.255.255,"; done
  cat >"$ca/ca.cnf" <<EOF
[req]
distinguished_name=dn
prompt=no
x509_extensions=v3
[dn]
CN=LinuxOps Web CA ($(hostname -s))
[v3]
basicConstraints=critical,CA:TRUE,pathlen:0
keyUsage=critical,keyCertSign,cRLSign
subjectKeyIdentifier=hash
nameConstraints=critical,${perm%,}
EOF
  openssl genrsa -out "$ca/ca.key" 4096 2>/dev/null
  chmod 600 "$ca/ca.key"
  openssl req -x509 -new -key "$ca/ca.key" -sha256 -days 3650 -config "$ca/ca.cnf" -out "$ca/ca.crt"
}

# Сертификат сервиса от своего CA (825 дней).
cert_issue_local() {
  local ca=$PKI_DIR/ca tmp
  local_ca_ensure
  tmp=$(mktemp -d)
  cat >"$tmp/web.cnf" <<EOF
[req]
distinguished_name=dn
prompt=no
[dn]
CN=$(hostname -f 2>/dev/null || hostname)
[ext]
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=$(san_list)
authorityKeyIdentifier=keyid
subjectKeyIdentifier=hash
EOF
  openssl genrsa -out "$tmp/web.key" 3072 2>/dev/null
  openssl req -new -key "$tmp/web.key" -config "$tmp/web.cnf" -out "$tmp/web.csr"
  openssl x509 -req -in "$tmp/web.csr" -CA "$ca/ca.crt" -CAkey "$ca/ca.key" -CAcreateserial \
    -days 825 -sha256 -extfile "$tmp/web.cnf" -extensions ext -out "$tmp/web.crt" 2>/dev/null
  cat "$ca/ca.crt" >>"$tmp/web.crt"
  cert_put "$tmp/web.crt" "$tmp/web.key"
  rm -rf "$tmp"
}

# Поставить сертификат с проверкой; при ошибке nginx — вернуть старый.
cert_put() {
  local crt="$1" key="$2"
  [[ "$(openssl x509 -in "$crt" -noout -pubkey 2>/dev/null | sha256sum)" == "$(openssl pkey -in "$key" -pubout 2>/dev/null | sha256sum)" ]] \
    || die "certificate and key do not match"
  install -d -m 700 "$PKI_DIR"
  [[ -f $PKI_DIR/web.crt ]] && cp -p "$PKI_DIR/web.crt" "$PKI_DIR/web.crt.bak" && cp -p "$PKI_DIR/web.key" "$PKI_DIR/web.key.bak"
  install -m 644 "$crt" "$PKI_DIR/web.crt"
  install -m 600 "$key" "$PKI_DIR/web.key"
  if have_cmd nginx && [[ -f $NGINX_CONF ]]; then
    if ! nginx -t >/dev/null 2>&1; then
      [[ -f $PKI_DIR/web.crt.bak ]] && mv -f "$PKI_DIR/web.crt.bak" "$PKI_DIR/web.crt" && mv -f "$PKI_DIR/web.key.bak" "$PKI_DIR/web.key"
      die "nginx rejected the certificate, previous one restored"
    fi
    systemctl reload nginx 2>/dev/null || true
  fi
  rm -f "$PKI_DIR/web.crt.bak" "$PKI_DIR/web.key.bak"
}

cert_ensure() { [[ -f $PKI_DIR/web.crt && -f $PKI_DIR/web.key ]] || cert_issue_local; }

# Текущий сертификат выпущен своим CA сервера?
cert_from_local_ca() {
  [[ -f $PKI_DIR/ca/ca.crt && -f $PKI_DIR/web.crt ]] || return 1
  openssl verify -CAfile "$PKI_DIR/ca/ca.crt" "$PKI_DIR/web.crt" >/dev/null 2>&1
}

cert_show() {
  [[ -f $PKI_DIR/web.crt ]] || { echo "  no certificate"; return; }
  openssl x509 -in "$PKI_DIR/web.crt" -noout -subject -issuer -enddate -ext subjectAltName 2>/dev/null | sed 's/^/  /'
  if cert_from_local_ca; then echo "  local CA (add to Trusted Root): $PKI_DIR/ca/ca.crt"; fi
}

# Источник релизов: GitHub сейчас, внутренний GitLab потом — только настройка.
release_conf_default() {
  [[ -f $RELEASE_CONF ]] && return 0
  cat >"$RELEASE_CONF" <<'EOF'
# Источник релизов LinuxOps Web. Токен — в /etc/linuxops-web/git-token (600), только на чтение.
PROVIDER=github            # github | gitlab
API_URL=https://api.github.com   # gitlab: https://gitlab.example.local/api/v4
PROJECT=MerlKoryAmber/MKIdea     # gitlab: группа/проект
TAG_PREFIX=linuxops-web-v
ASSET_SUFFIX=linux-x64.tar.gz
EOF
  chmod 600 "$RELEASE_CONF"
}

release_conf_load() {
  release_conf_default
  # shellcheck disable=SC1090
  source <(sed -E 's/[[:space:]]+#.*$//' "$RELEASE_CONF")
  TOKEN=""
  if [[ -f $TOKEN_FILE ]]; then TOKEN=$(tr -d ' \r\n' <"$TOKEN_FILE"); fi
}

_api() { # _api URL → stdout
  local h=()
  if [[ -n "$TOKEN" ]]; then
    if [[ $PROVIDER == gitlab ]]; then h=(-H "PRIVATE-TOKEN: $TOKEN"); else h=(-H "Authorization: Bearer $TOKEN"); fi
  fi
  curl -fsSL --max-time 30 "${h[@]}" -H "Accept: application/json" "$1"
}

_enc() { jq -rn --arg s "$1" '$s|@uri'; }

# release_find [version] → печатает «tag<TAB>url архива<TAB>url .sha256 (может быть пусто)»
release_find() {
  local want="${1:-}" json
  release_conf_load
  have_cmd jq || die "jq is required"
  if [[ $PROVIDER == gitlab ]]; then
    json=$(_api "$API_URL/projects/$(_enc "$PROJECT")/releases?per_page=50") || die "release source unreachable or access denied (token?): $API_URL $PROJECT"
    jq -r --arg p "$TAG_PREFIX" --arg w "$want" --arg s "$ASSET_SUFFIX" '
      [.[] | select(.tag_name | startswith($p)) | select($w == "" or .tag_name == ($p + $w))][0] // empty
      | [.tag_name,
         ([.assets.links[] | select(.name | endswith($s))][0].url // ""),
         ([.assets.links[] | select(.name | endswith($s + ".sha256"))][0].url // "")] | @tsv' <<<"$json"
  else
    json=$(_api "$API_URL/repos/$PROJECT/releases?per_page=50") || die "release source unreachable or access denied (token?): $API_URL $PROJECT"
    jq -r --arg p "$TAG_PREFIX" --arg w "$want" --arg s "$ASSET_SUFFIX" --arg api "$API_URL/repos/$PROJECT/releases/assets/" '
      [.[] | select(.draft | not) | select(.tag_name | startswith($p)) | select($w == "" or .tag_name == ($p + $w))][0] // empty
      | [.tag_name,
         (([.assets[] | select(.name | endswith($s))][0].id // "") | if . == "" then "" else $api + tostring end),
         (([.assets[] | select(.name | endswith($s + ".sha256"))][0].id // "") | if . == "" then "" else $api + tostring end)] | @tsv' <<<"$json"
  fi
}

release_download() { # release_download URL FILE
  local h=()
  if [[ -n "$TOKEN" ]]; then
    if [[ $PROVIDER == gitlab ]]; then h=(-H "PRIVATE-TOKEN: $TOKEN"); else h=(-H "Authorization: Bearer $TOKEN"); fi
  fi
  [[ $PROVIDER == gitlab ]] || h+=(-H "Accept: application/octet-stream")
  curl -fSL --max-time 600 "${h[@]}" -o "$2" "$1"
}

# Проверка доступа к проекту (для пункта «git token»): 0 — ок.
release_probe() {
  release_conf_load
  if [[ $PROVIDER == gitlab ]]; then _api "$API_URL/projects/$(_enc "$PROJECT")" >/dev/null
  else _api "$API_URL/repos/$PROJECT" >/dev/null; fi
}

# OpenH264 для guacd (RDP): репозиторий Fedora/Cisco для EPEL, замена заглушки noopenh264.
openh264_ensure() {
  local repo=/etc/yum.repos.d/epel-cisco-openh264.repo
  [[ -f $repo ]] || cat >"$repo" <<'REPO'
[epel-cisco-openh264]
name=Extra Packages for Enterprise Linux $releasever openh264 (From Cisco) - $basearch
baseurl=https://codecs.fedoraproject.org/openh264/epel/$releasever/$basearch/
type=rpm
enabled=1
gpgcheck=1
gpgkey=file:///etc/pki/rpm-gpg/RPM-GPG-KEY-EPEL-$releasever
skip_if_unavailable=True
REPO
  # пакет из комплекта релиза (deploy/vendor) — сеть не нужна; подпись EPEL проверяется до установки
  local pkg="" el f
  el=$(rpm -E %rhel 2>/dev/null || true)
  for f in "${1:-}"/deploy/vendor/openh264-*.el"${el}"*."$(uname -m)".rpm; do [[ -f $f ]] && pkg=$f; done
  if [[ -n $pkg ]]; then
    rpm --import "/etc/pki/rpm-gpg/RPM-GPG-KEY-EPEL-$el" 2>/dev/null || true
    local chk; chk=$(rpm -K "$pkg" 2>/dev/null || true)
    if [[ $chk == *"signatures OK"* ]]; then log "OpenH264: $(basename "$pkg")"
    else warn "OpenH264: signature check failed for $(basename "$pkg") — trying the repository"; pkg=""; fi
  fi
  local src=${pkg:-openh264}
  if rpm -q noopenh264 >/dev/null 2>&1; then dnf -y -q swap noopenh264 "$src" || return 1
  else dnf -y -q install "$src" || return 1; fi
  systemctl restart guacd 2>/dev/null || true
}

# Записи сеансов: пишет guacd, сервис читает через группу (setgid — новые файлы с группой сервиса)
# и удаляет по сроку и объёму (запись в каталог группе — 2770; сами файлы guacd — 0640, сервис их не меняет).
recordings_dir_ensure() {
  id guacd >/dev/null 2>&1 || return 0
  install -d -o guacd -g "$SVC_USER" -m 2770 "$REC_DIR"
  chown guacd:"$SVC_USER" "$REC_DIR"; chmod 2770 "$REC_DIR"
  if have_cmd semanage && have_cmd restorecon; then restorecon -R "$REC_DIR" 2>/dev/null || true; fi
}

# PowerShell 7 (Ф5): RPM из комплекта релиза (deploy/vendor), сеть не нужна. Целостность — SHA-256 всего релиза.
pwsh_ensure() {
  have_cmd pwsh && return 0
  local pkg="" f
  for f in "${1:-}"/deploy/vendor/powershell-*.rh."$(uname -m)".rpm; do [[ -f $f ]] && pkg=$f; done
  [[ -n $pkg ]] || { warn "PowerShell: no package in the release for $(uname -m)"; return 1; }
  log "PowerShell: $(basename "$pkg")"
  dnf -y -q install "$pkg"
}

# Консоль PowerShell в браузере: служебная учётка ОС с оболочкой pwsh; guacd входит в неё по SSH на 127.0.0.1
# ключом сервиса (authorized_keys: только с localhost, без проброса портов и агента). Пароля у учётки нет.
# Модули (PowerCLI) ставит сервис в PS_MOD_DIR, учётка их читает. Телеметрия и проверка обновлений pwsh — выключены.
PS_USER=linuxops-ps
PS_HOME=/home/linuxops-ps
PS_MOD_DIR=/var/lib/linuxops-web-ps
PS_KEY=$ETC_DIR/ps_ed25519
PS_SHELL=/usr/local/libexec/linuxops-ps-shell
ps_account_ensure() {
  have_cmd pwsh || return 0
  install -d -m 755 "$(dirname "$PS_SHELL")"
  cat >"$PS_SHELL" <<'SH'
#!/bin/sh
# Оболочка консоли PowerShell LinuxOps Web: pwsh без телеметрии и проверки обновлений.
export POWERSHELL_TELEMETRY_OPTOUT=1 POWERSHELL_UPDATECHECK=Off DOTNET_CLI_TELEMETRY_OPTOUT=1
exec /usr/bin/pwsh -NoLogo "$@"
SH
  chmod 755 "$PS_SHELL"
  grep -qx "$PS_SHELL" /etc/shells || echo "$PS_SHELL" >>/etc/shells
  id "$PS_USER" >/dev/null 2>&1 || useradd -r -m -d "$PS_HOME" -s "$PS_SHELL" -c "LinuxOps Web PowerShell console" "$PS_USER"
  usermod -s "$PS_SHELL" "$PS_USER"
  # без пароля, но не «заблокирована»: sshd пускает только по ключу
  usermod -p '*' "$PS_USER"
  if [[ ! -f $PS_KEY ]]; then ssh-keygen -q -t ed25519 -N '' -C linuxops-web-ps -f "$PS_KEY"; fi
  chown "$SVC_USER:$SVC_USER" "$PS_KEY" "$PS_KEY.pub"; chmod 600 "$PS_KEY"; chmod 644 "$PS_KEY.pub"
  install -d -o "$PS_USER" -g "$PS_USER" -m 700 "$PS_HOME/.ssh"
  printf 'from="127.0.0.1,::1",restrict,pty %s\n' "$(cat "$PS_KEY.pub")" >"$PS_HOME/.ssh/authorized_keys"
  chown "$PS_USER:$PS_USER" "$PS_HOME/.ssh/authorized_keys"; chmod 600 "$PS_HOME/.ssh/authorized_keys"
  install -d -o "$SVC_USER" -g "$PS_USER" -m 2750 "$PS_MOD_DIR" "$PS_MOD_DIR/Modules"
  install -d -o "$PS_USER" -g "$PS_USER" -m 700 "$PS_HOME/.config" "$PS_HOME/.config/powershell"
  cat >"$PS_HOME/.config/powershell/Microsoft.PowerShell_profile.ps1" <<'PS1'
# LinuxOps Web: модули, поставленные из ⚙ (PowerCLI), — первыми в пути поиска.
$env:PSModulePath = '/var/lib/linuxops-web-ps/Modules' + [IO.Path]::PathSeparator + $env:PSModulePath
if (Get-Module -ListAvailable -Name VMware.VimAutomation.Core) {
    Write-Host 'PowerCLI есть: Connect-VIServer -Server <vcenter>' -ForegroundColor DarkGray
} else {
    Write-Host 'PowerCLI не установлен: шестерёнка (настройки LinuxOps Web) → «PowerShell и PowerCLI».' -ForegroundColor DarkGray
}
PS1
  chown "$PS_USER:$PS_USER" "$PS_HOME/.config/powershell/Microsoft.PowerShell_profile.ps1"
  # PowerCLI: без программы сбора данных VMware (CEIP) по умолчанию. Файл уже есть — выбор админа не трогаем.
  local cli_dir="$PS_HOME/.local/share/VMware/PowerCLI"
  if [[ ! -f $cli_dir/PowerCLI_Settings.xml ]]; then
    install -d -o "$PS_USER" -g "$PS_USER" -m 700 "$PS_HOME/.local" "$PS_HOME/.local/share" "$PS_HOME/.local/share/VMware" "$cli_dir"
    printf '<Settings><Setting Name="ParticipateInCEIP" Value="False" /></Settings>' >"$cli_dir/PowerCLI_Settings.xml"
    chown "$PS_USER:$PS_USER" "$cli_dir/PowerCLI_Settings.xml"; chmod 600 "$cli_dir/PowerCLI_Settings.xml"
  fi
  if have_cmd restorecon; then restorecon -R "$PS_HOME" "$PS_MOD_DIR" "$PS_SHELL" 2>/dev/null || true; fi
  local allow; allow=$(sshd -T 2>/dev/null | awk '$1=="allowusers"{ $1=""; print }')
  if [[ -n $allow && " $allow " != *" $PS_USER "* ]]; then warn "sshd AllowUsers does not include $PS_USER — PowerShell console will not connect"; fi
}

# Сделать релиз текущим: юнит из него, перезапуск, проверка. Не поднялся — назад на прежний.
# activate_release DIR [norollback]
activate_release() {
  local dir="$1" old="" oldprev=""
  if [[ -L $CURRENT_LINK ]]; then old=$(readlink -f "$CURRENT_LINK"); fi
  oldprev=$(cat "$PREVIOUS_FILE" 2>/dev/null || true)
  systemctl stop "$UNIT" 2>/dev/null || true
  ln -sfn "$dir" "$CURRENT_LINK"
  if [[ -n "$old" && "$old" != "$dir" ]]; then echo "$old" >"$PREVIOUS_FILE"; fi
  recordings_dir_ensure
  # SELinux: релиз распакован во временный каталог и унёс метку user_tmp_t — служба шла бы в init_t.
  # По штатным меткам (usr_t, var_lib_t, etc_t) — в unconfined_service_t, как любая служба из /opt.
  if have_cmd restorecon; then restorecon -R "$dir" "$DATA_DIR" "$ETC_DIR" 2>/dev/null || true; fi
  install -m 644 "$dir/deploy/linuxops-web.service" "$UNIT_FILE"
  systemctl daemon-reload
  systemctl enable "$UNIT" >/dev/null 2>&1
  systemctl start "$UNIT"
  if health_check 40; then
    log "running: $(basename "$dir")"
    logger -t linuxops-web "служба: версия $(basename "$dir") запущена${old:+ (была $(basename "$old"))}" 2>/dev/null || true
    return 0
  fi
  warn "service did not answer on $LOCAL_URL"
  if [[ "${2:-}" != norollback && -n "$old" && "$old" != "$dir" && -d "$old" ]]; then
    warn "rolling back to $(basename "$old")"
    activate_release "$old" norollback || true
    # «Предыдущая» — та же, что до неудачной попытки, а не сломанная версия.
    if [[ -n "$oldprev" && "$oldprev" != "$dir" ]]; then echo "$oldprev" >"$PREVIOUS_FILE"; else rm -f "$PREVIOUS_FILE"; fi
    logger -t linuxops-web "служба: версия $(basename "$dir") не запустилась, возврат на $(basename "$old")" 2>/dev/null || true
    return 1
  fi
  return 1
}

# Оставить текущий, предыдущий и ещё один свежий релиз.
prune_releases() {
  local d n=0 cur prev
  cur=$(readlink -f "$CURRENT_LINK" 2>/dev/null || true)
  prev=$(cat "$PREVIOUS_FILE" 2>/dev/null || true)
  for d in $(ls -1dt "$RELEASES_DIR"/*/ 2>/dev/null); do
    d=${d%/}
    if [[ "$d" == "$cur" || "$d" == "$prev" ]]; then continue; fi
    n=$((n + 1))
    ((n <= 1)) && continue
    rm -rf "$d"
  done
}
