#!/usr/bin/env bash
# LinuxOps Web — меню управления на сервере (шаблон: 2fa docs/patterns/cli-menu-linux.md).
# Без аргументов — меню; с аргументом — сразу действие (linuxops-web help).
# Подписи на английском: на tty1 кириллицы часто нет.
set -euo pipefail
DEPLOY="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
# shellcheck source=lib/common.sh
source "$DEPLOY/lib/common.sh"

LINE="————————————————————————————————"
_pause() { _tty_ok && { read -r -p "Press Enter to continue..." _ </dev/tty || true; }; return 0; }

# --- lifecycle ---
cmd_update() { bash "$DEPLOY/update.sh" "$@"; }
cmd_update_version() { local v; ask v "Version (e.g. 0.2.1)"; [[ -n "$v" ]] || die "no version"; bash "$DEPLOY/update.sh" --version "$v"; }
cmd_update_file() { local f; ask f "Path to LinuxOps.Web-<ver>-linux-x64.tar.gz"; [[ -n "$f" ]] || die "no file"; bash "$DEPLOY/update.sh" --file "$f"; }
cmd_rollback() {
  local prev; prev=$(cat "$PREVIOUS_FILE" 2>/dev/null || true)
  [[ -n "$prev" && -d "$prev" ]] || die "no previous version to roll back to"
  confirm "Roll back $(current_version) -> $(basename "$prev")? Database is not rolled back" || die "cancelled"
  activate_release "$prev"
}
cmd_uninstall() { bash "$DEPLOY/uninstall.sh"; }
cmd_purge() { bash "$DEPLOY/uninstall.sh" --purge; }

# --- access ---
cmd_git_token() {
  release_conf_load
  echo "  Release source: $PROVIDER  $API_URL  $PROJECT  (edit: $RELEASE_CONF)"
  local t; ask_secret t "Read-only token (empty = remove token)"
  if [[ -z "$t" ]]; then rm -f "$TOKEN_FILE"; log "token removed"; return 0; fi
  local old=""; [[ -f $TOKEN_FILE ]] && old=$(cat "$TOKEN_FILE")
  ( umask 077; printf '%s\n' "$t" >"$TOKEN_FILE" )
  if release_probe; then
    log "token OK: $PROJECT is reachable"
    logger -t linuxops-web "токен источника релизов изменён" 2>/dev/null || true
  else
    if [[ -n "$old" ]]; then ( umask 077; printf '%s\n' "$old" >"$TOKEN_FILE" ); else rm -f "$TOKEN_FILE"; fi
    die "token rejected or $API_URL unreachable — previous token kept"
  fi
}

cmd_cert() {
  local sub="${1:-}"
  if [[ -z "$sub" ]]; then
    echo -e "
  ${green}Certificate${plain}
$LINE
  ${green} 1.${plain} Show current
  ${green} 2.${plain} New certificate from local CA
  ${green} 3.${plain} Import PEM (certificate + key)
  ${green} 4.${plain} Import PFX / P12
  ${green} 5.${plain} Create CSR for corporate CA
  ${green} 6.${plain} Install certificate signed for the CSR
$LINE"
    ask sub "Select [1-6]"
    case "$sub" in 1) sub=show ;; 2) sub=local ;; 3) sub=pem ;; 4) sub=pfx ;; 5) sub=csr ;; 6) sub=signed ;; *) return 0 ;; esac
  fi
  case "$sub" in
    show) cert_show ;;
    local)
      confirm "Issue a new certificate from the local CA (names: $(san_list))?" || die "cancelled"
      cert_issue_local; cert_show ;;
    pem)
      local c k ch; ask c "Certificate file (PEM)"; ask k "Private key file (PEM)"; ask ch "Chain file (empty = none)"
      [[ -f "$c" && -f "$k" ]] || die "file not found"
      local tmp; tmp=$(mktemp); cat "$c" >"$tmp"; [[ -n "$ch" ]] && cat "$ch" >>"$tmp"
      cert_put "$tmp" "$k"; rm -f "$tmp"; log "certificate installed"; cert_show ;;
    pfx)
      local f tmp; ask f "PFX file"; [[ -f "$f" ]] || die "file not found"
      tmp=$(mktemp -d)
      local pw; ask_secret pw "PFX password"
      # Пароль — через окружение, не в argv. Старые PFX (RC2/3DES) открываются только с -legacy.
      _p12() { PFXPASS="$pw" openssl pkcs12 -in "$f" -passin env:PFXPASS "$@" 2>/dev/null || PFXPASS="$pw" openssl pkcs12 -legacy -in "$f" -passin env:PFXPASS "$@" 2>/dev/null; }
      _p12 -clcerts -nokeys -out "$tmp/leaf.pem" || { rm -rf "$tmp"; die "cannot open PFX (wrong password?)"; }
      _p12 -cacerts -nokeys -out "$tmp/chain.pem" || true
      _p12 -nocerts -nodes -out "$tmp/k.pem" || { rm -rf "$tmp"; die "no private key in PFX"; }
      # Сертификат сервера — первым, за ним цепочка.
      { openssl x509 -in "$tmp/leaf.pem"; grep -q BEGIN "$tmp/chain.pem" 2>/dev/null && cat "$tmp/chain.pem"; } >"$tmp/full.pem"
      cert_put "$tmp/full.pem" "$tmp/k.pem"; rm -rf "$tmp"; log "certificate installed"; cert_show ;;
    csr)
      local extra; ask extra "Extra names, comma separated (e.g. DNS:linuxops.corp.local), empty = none"
      install -d -m 700 "$PKI_DIR"
      openssl req -new -newkey rsa:3072 -nodes -keyout "$PKI_DIR/pending.key" -out "$PKI_DIR/pending.csr" \
        -subj "/CN=$(hostname -f 2>/dev/null || hostname)" -addext "subjectAltName=$(san_list "$extra")" \
        -addext "extendedKeyUsage=serverAuth" 2>/dev/null
      chmod 600 "$PKI_DIR/pending.key"
      log "CSR: $PKI_DIR/pending.csr (key stays on this server)"
      cat "$PKI_DIR/pending.csr" ;;
    signed)
      [[ -f $PKI_DIR/pending.key ]] || die "no pending CSR — create it first"
      local c ch tmp; ask c "Signed certificate (PEM)"; ask ch "Chain file (empty = none)"
      [[ -f "$c" ]] || die "file not found"
      tmp=$(mktemp); cat "$c" >"$tmp"; [[ -n "$ch" ]] && cat "$ch" >>"$tmp"
      cert_put "$tmp" "$PKI_DIR/pending.key"; rm -f "$tmp" "$PKI_DIR/pending.key" "$PKI_DIR/pending.csr"
      log "certificate installed"; cert_show ;;
    *) die "cert: show | local | pem | pfx | csr | signed" ;;
  esac
  logger -t linuxops-web "сертификат: $sub" 2>/dev/null || true
}

# --- runtime ---
cmd_status() {
  release_conf_load
  echo "  Version:  $(current_version)   previous: $(previous_version)"
  echo "  Service:  $(systemctl is-active "$UNIT" 2>/dev/null || true)   nginx: $(systemctl is-active nginx 2>/dev/null || true)   guacd: $(systemctl is-active guacd 2>/dev/null || true)"
  if curl -fs -o /dev/null --max-time 3 "$LOCAL_URL"; then echo "  Health:   OK ($LOCAL_URL)"; else echo "  Health:   NO ANSWER ($LOCAL_URL)"; fi
  echo "  Data:     $DATA_DIR ($(du -sh "$DATA_DIR" 2>/dev/null | cut -f1))"
  echo "  Releases: $PROVIDER $PROJECT, token: $([[ -f $TOKEN_FILE ]] && echo set || echo none)"
  echo "  Certificate:"; cert_show
}
cmd_start() { systemctl start "$UNIT"; health_check 40 && log "started" || die "no answer, see logs"; }
cmd_stop() { confirm "Stop LinuxOps Web?" || die "cancelled"; systemctl stop "$UNIT"; log "stopped"; }
cmd_restart() { confirm "Restart LinuxOps Web? Open browser sessions reconnect" || die "cancelled"; systemctl restart "$UNIT"; health_check 40 && log "restarted" || die "no answer, see logs"; }
cmd_logs() {
  local s="${1:-}" f=()
  if [[ -z "$s" ]]; then
    echo -e "  ${green}1.${plain} Service  ${green}2.${plain} Audit  ${green}3.${plain} nginx errors  ${green}4.${plain} guacd"
    ask s "Select [1-4]"
  fi
  [[ -t 1 ]] && f=(-f)
  case "$s" in
    1|service) journalctl -u "$UNIT" -n 100 --no-pager "${f[@]}" ;;
    2|audit) journalctl -t linuxops-web -n 100 --no-pager "${f[@]}" ;;
    3|nginx) if [[ -t 1 ]]; then tail -n 100 -f /var/log/nginx/error.log; else tail -n 100 /var/log/nginx/error.log; fi ;;
    4|guacd) journalctl -u guacd -n 100 --no-pager "${f[@]}" ;;
    *) die "logs: service | audit | nginx | guacd" ;;
  esac
}

# --- data ---
cmd_backup() {
  have_cmd sqlite3 || die "sqlite3 is required (dnf install sqlite)"
  local stamp dir; stamp=$(TZ=Europe/Moscow date +%Y%m%d-%H%M%S); dir=$BACKUP_DIR/$stamp
  install -d -m 700 "$BACKUP_DIR" "$dir"
  # База — через .backup (служба может писать в это время), остальное — как есть.
  sqlite3 "$DATA_DIR/linuxops-web.db" ".backup '$dir/linuxops-web.db'"
  tar -czf "$dir/data.tar.gz" -C "$DATA_DIR" --exclude='linuxops-web.db*' .
  tar -czf "$dir/etc.tar.gz" -C / "${ETC_DIR#/}" "${PKI_DIR#/}" 2>/dev/null || tar -czf "$dir/etc.tar.gz" -C / "${ETC_DIR#/}"
  echo "$(current_version)" >"$dir/VERSION"
  chmod 600 "$dir"/*
  log "backup: $dir"
  warn "the backup contains the master key: with it the database opens all stored passwords and keys. Copy it off-server only encrypted"
  logger -t linuxops-web "бэкап: $dir" 2>/dev/null || true
}
cmd_restore() {
  local list=() i n s dir
  mapfile -t list < <(ls -1 "$BACKUP_DIR" 2>/dev/null | grep -E '^[0-9]{8}-[0-9]{6}$' | sort -r)
  ((${#list[@]})) || die "no backups in $BACKUP_DIR"
  for i in "${!list[@]}"; do printf "  ${green}%2d.${plain} %s  (version %s)\n" $((i + 1)) "${list[$i]}" "$(cat "$BACKUP_DIR/${list[$i]}/VERSION" 2>/dev/null || echo ?)"; done
  if [[ -n "${1:-}" ]]; then s="$1"; else ask n "Select backup [1-${#list[@]}]"; [[ "$n" =~ ^[0-9]+$ ]] && ((n >= 1 && n <= ${#list[@]})) || die "cancelled"; s=${list[$((n - 1))]}; fi
  dir=$BACKUP_DIR/$s; [[ -f $dir/linuxops-web.db ]] || die "no such backup: $s"
  confirm "Restore $s over current data? Current data is saved first" || die "cancelled"
  confirm "Really replace database and master key with backup $s?" || die "cancelled"
  local restore_cert=0; confirm "Also restore the certificate from the backup?" && restore_cert=1
  LINUXOPS_WEB_ASSUME_YES=1 cmd_backup >/dev/null 2>&1 && log "current data saved as a backup first"
  systemctl stop "$UNIT"
  rm -f "$DATA_DIR"/linuxops-web.db*
  install -o "$SVC_USER" -g "$SVC_USER" -m 600 "$dir/linuxops-web.db" "$DATA_DIR/linuxops-web.db"
  tar -xzf "$dir/data.tar.gz" -C "$DATA_DIR"
  if ((restore_cert)); then tar -xzf "$dir/etc.tar.gz" -C /; else tar -xzf "$dir/etc.tar.gz" -C / "${ETC_DIR#/}"; fi
  chown -R "$SVC_USER:$SVC_USER" "$DATA_DIR" "$ETC_DIR"
  chown root:root "$RELEASE_CONF" "$TOKEN_FILE" 2>/dev/null || true
  ((restore_cert)) && systemctl reload nginx 2>/dev/null || true
  systemctl start "$UNIT"
  health_check 40 && log "restored $s" || die "service does not answer after restore, see logs"
  logger -t linuxops-web "восстановление из бэкапа $s" 2>/dev/null || true
}

# --- info ---
cmd_url() {
  local ip; ip=$(server_ips | head -1)
  echo "  https://$(hostname -f 2>/dev/null || hostname)/"
  [[ -n "$ip" ]] && echo "  https://$ip/"
  if curl -fsk -o /dev/null --max-time 5 https://127.0.0.1/; then echo "  Health: OK"; else echo "  Health: NO ANSWER"; fi
}

cmd_help() {
  cat <<EOF
Usage: linuxops-web [command]   (no command = menu)
  update [--version V | --file F | --force]   rollback   uninstall   purge
  git-token   cert [show|local|pem|pfx|csr|signed]
  status   start   stop   restart   logs [service|audit|nginx|guacd]
  backup   restore [STAMP]   url   help
EOF
}

show_menu() {
  echo -e "
  ${green}LinuxOps Web${plain} $(current_version)  ($APP_DIR)
$LINE
  ${green} 0.${plain} Exit
$LINE
  ${green} 1.${plain} Update (latest release)
  ${green} 2.${plain} Update to version
  ${green} 3.${plain} Update from file
  ${green} 4.${plain} Rollback to previous version
  ${green} 5.${plain} Uninstall (keep data)
  ${green} 6.${plain} Uninstall + data (PURGE)
$LINE
  ${green} 7.${plain} Release source token (git)
  ${green} 8.${plain} Certificate
$LINE
  ${green} 9.${plain} Status
  ${green}10.${plain} Start
  ${green}11.${plain} Stop
  ${green}12.${plain} Restart
  ${green}13.${plain} Logs
$LINE
  ${green}14.${plain} Backup
  ${green}15.${plain} Restore from backup
  ${green}16.${plain} URL / health
$LINE"
}

run() { ( "$@" ) || true; _pause; }   # ошибка пункта не роняет меню

run_menu() {
  local num
  while true; do
    show_menu
    ask num "Please enter your selection [0-16]"
    case "$num" in
      0|"") exit 0 ;;
      1) run cmd_update ;; 2) run cmd_update_version ;; 3) run cmd_update_file ;; 4) run cmd_rollback ;;
      5) run cmd_uninstall; [[ -L $CURRENT_LINK ]] || exit 0 ;;
      6) run cmd_purge; [[ -L $CURRENT_LINK ]] || exit 0 ;;
      7) run cmd_git_token ;; 8) run cmd_cert ;;
      9) run cmd_status ;; 10) run cmd_start ;; 11) run cmd_stop ;; 12) run cmd_restart ;; 13) run cmd_logs ;;
      14) run cmd_backup ;; 15) run cmd_restore ;; 16) run cmd_url ;;
      *) echo "Please enter the correct number [0-16]" ;;
    esac
  done
}

need_root
case "${1:-}" in
  "") run_menu ;;
  update) shift; cmd_update "$@" ;;
  rollback) cmd_rollback ;;
  uninstall) cmd_uninstall ;;
  purge) cmd_purge ;;
  git-token) cmd_git_token ;;
  cert) shift; cmd_cert "${1:-}" ;;
  status) cmd_status ;;
  start) cmd_start ;;
  stop) cmd_stop ;;
  restart) cmd_restart ;;
  logs) shift; cmd_logs "${1:-}" ;;
  backup) cmd_backup ;;
  restore) shift; cmd_restore "${1:-}" ;;
  url) cmd_url ;;
  help|-h|--help) cmd_help ;;
  *) echo "Unknown command: $1"; cmd_help; exit 1 ;;
esac
