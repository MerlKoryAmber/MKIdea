#!/usr/bin/env bash
# Установка LinuxOps Web на AlmaLinux / RHEL 10 (и 9) из распакованного релиза.
#   sudo ./deploy/install.sh               — первая установка: пакеты, служба, nginx с TLS, файрвол, команда linuxops-web
#   sudo ./deploy/install.sh --upgrade     — из update.sh: только этот релиз, юнит, nginx, меню
# Ключи: --no-guacd (без шлюза сессий), --no-firewall (не трогать firewalld).
# Данные (/var/lib/linuxops-web) и мастер-ключ (/etc/linuxops-web) не трогает никогда.
set -euo pipefail
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/common.sh
source "$SRC/deploy/lib/common.sh"

UPGRADE=0 GUACD=1 FIREWALL=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --upgrade) UPGRADE=1 ;;
    --no-guacd) GUACD=0 ;;
    --no-firewall) FIREWALL=0 ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done

need_root
[[ -f "$SRC/LinuxOps.Web" ]] || die "no LinuxOps.Web in $SRC — run from an unpacked release"
VER=$(release_version "$SRC")
log "LinuxOps Web $VER from $SRC"

# --- пакеты ---
pkgs=(nginx openssl curl tar jq sqlite policycoreutils-python-utils)
((FIREWALL)) && pkgs+=(firewalld)
missing=()
for p in "${pkgs[@]}"; do rpm -q "$p" >/dev/null 2>&1 || missing+=("$p"); done
if ((${#missing[@]})); then
  log "packages: ${missing[*]}"
  dnf -y -q install "${missing[@]}"
fi
if ((GUACD)) && ! rpm -q guacd >/dev/null 2>&1; then
  if ((UPGRADE)); then :; else
    rpm -q epel-release >/dev/null 2>&1 || dnf -y -q install epel-release
    dnf -y -q install guacd libguac-client-ssh libguac-client-rdp && systemctl enable --now guacd >/dev/null 2>&1 \
      || warn "guacd not installed (EPEL unavailable?) — sessions (Ф3) will not work"
  fi
fi

# --- пользователь и каталоги ---
id "$SVC_USER" >/dev/null 2>&1 || useradd -r -s /sbin/nologin -d "$DATA_DIR" "$SVC_USER"
install -d -m 755 "$APP_DIR" "$RELEASES_DIR"
install -d -o "$SVC_USER" -g "$SVC_USER" -m 700 "$DATA_DIR" "$ETC_DIR"
install -d -m 700 "$BACKUP_DIR"
release_conf_default

# Старая раскладка (файлы прямо в /opt/linuxops-web, до 0.2.0) — убрать, данные не там.
if [[ -f "$APP_DIR/LinuxOps.Web" && ! -L "$CURRENT_LINK" ]]; then
  log "removing old flat layout in $APP_DIR"
  systemctl stop "$UNIT" 2>/dev/null || true
  find "$APP_DIR" -mindepth 1 -maxdepth 1 ! -name releases -exec rm -rf {} +
fi

# --- релиз ---
DST="$RELEASES_DIR/$VER"
if [[ "$SRC" != "$DST" ]]; then
  rm -rf "$DST.new"
  cp -a "$SRC" "$DST.new"
  if [[ "$(readlink -f "$CURRENT_LINK" 2>/dev/null)" == "$DST" ]]; then systemctl stop "$UNIT" 2>/dev/null || true; fi
  rm -rf "$DST"
  mv "$DST.new" "$DST"
fi
chown -R root:root "$DST"
chmod 755 "$DST/LinuxOps.Web" "$DST/deploy/"*.sh

# --- nginx и сертификат ---
cert_ensure
install -m 644 "$DST/deploy/nginx-linuxops-web.conf" "$NGINX_CONF"
# default_server — только у нашего сервера
sed -i -E 's/listen(\s+)80 default_server;/listen\180;/; s/listen(\s+)\[::\]:80 default_server;/listen\1[::]:80;/' /etc/nginx/nginx.conf
if [[ "$(getenforce 2>/dev/null)" != Disabled ]]; then setsebool -P httpd_can_network_connect 1 2>/dev/null || true; fi
nginx -t >/dev/null 2>&1 || { nginx -t; die "nginx config check failed"; }
systemctl enable nginx >/dev/null 2>&1
systemctl reload nginx 2>/dev/null || systemctl restart nginx

# --- файрвол ---
if ((FIREWALL)) && ! ((UPGRADE)); then
  systemctl enable --now firewalld >/dev/null 2>&1
  firewall-cmd -q --permanent --add-service=https --add-service=http
  firewall-cmd -q --reload
fi

# --- служба ---
ensure_cli
activate_release "$DST" || die "service failed to start, see: journalctl -u $UNIT -n 50"
prune_releases

ip=$(server_ips | head -1)
cat <<EOF

  ${green}LinuxOps Web $VER installed${plain}
  URL:      https://${ip:-$(hostname -f)}/
  Menu:     $CLI   (sudo linuxops-web)
  Data:     $DATA_DIR, key $ETC_DIR/master.key
EOF
cert_from_local_ca && echo "  Local CA: $PKI_DIR/ca/ca.crt — add to Trusted Root on admin PCs"
echo
