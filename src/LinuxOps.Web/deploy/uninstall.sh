#!/usr/bin/env bash
# Удаление LinuxOps Web.
#   uninstall.sh           — служба, программа, nginx-конфиг, команда меню. Данные, ключ и сертификат остаются.
#   uninstall.sh --purge   — ещё данные, мастер-ключ, сертификат и пользователь (два подтверждения).
# Бэкапы в /var/backups/linuxops-web не удаляются никогда.
# --yes — без вопросов (для автоматизации).
set -euo pipefail
# shellcheck source=lib/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

PURGE=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --purge) PURGE=1 ;;
    --yes) export LINUXOPS_WEB_ASSUME_YES=1 ;;
    -h|--help) sed -n '2,6p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done
need_root
cd /tmp

confirm "Uninstall LinuxOps Web $(current_version)?" || die "cancelled"
if ((PURGE)); then
  confirm "PURGE: delete data $DATA_DIR, master key and certificate? This cannot be undone" || die "cancelled"
  confirm "Really delete all LinuxOps Web data? Backups in $BACKUP_DIR stay" || die "cancelled"
fi

systemctl disable --now "$UNIT" 2>/dev/null || true
rm -f "$UNIT_FILE"
systemctl daemon-reload
rm -f "$NGINX_CONF"
systemctl reload nginx 2>/dev/null || true
rm -f "$CLI" /usr/local/bin/linuxops-web
rm -rf "$APP_DIR"
logger -t linuxops-web "служба удалена$( ((PURGE)) && echo " вместе с данными")" 2>/dev/null || true
log "removed program, service, nginx config and menu"

if ((PURGE)); then
  rm -rf "$DATA_DIR" "$ETC_DIR" "$PKI_DIR"
  userdel "$SVC_USER" 2>/dev/null || true
  log "removed data, master key, certificate and user $SVC_USER"
else
  log "kept: $DATA_DIR, $ETC_DIR (master key, release source), $PKI_DIR (certificate)"
fi
log "backups kept: $BACKUP_DIR"
