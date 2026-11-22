#!/usr/bin/env bash
# Хранилище записей сеансов (0.7.4): SMB, NFS или отдельный диск, смонтированные в $ARCH_DIR.
# Служба (без прав root, NoNewPrivileges) кладёт запрос в $STORAGE_DIR/request (JSON) — path-юнит
# linuxops-web-storage.path запускает linuxops-web-storage.service (root): `linuxops-web storage apply`.
# Запрос читается (только обычный файл службы, не ссылка) и сразу удаляется, каждое поле проверяется по шаблону.
# Итог — $RESULT_DIR/result: каталог root (служба только читает) — root не пишет туда, где служба может подложить ссылку.
# Учётка SMB — в /etc/linuxops-web (root:служба 0750, файл root 0600).
# Монтирование — строкой в /etc/fstab с пометкой «# linuxops-web-archive» (переживает перезагрузку; nofail — сервер грузится и без хранилища).
# guacd пишет как раньше в буфер $REC_DIR, готовые записи переносит в хранилище сама служба.
# shellcheck disable=SC2034

ARCH_DIR=/var/lib/linuxops-web-archive
STORAGE_DIR=$DATA_DIR/storage
RESULT_DIR=/run/linuxops-web-storage
SMB_CRED=$ETC_DIR/smb-archive.cred
FSTAB_MARK="# linuxops-web-archive"
STORAGE_UNIT=linuxops-web-storage

# Каталоги и юниты (установка и обновление)
storage_ensure() {
  local dir="$1"
  # служба могла заменить каталог запросов ссылкой — убрать, создать заново
  [[ -L $STORAGE_DIR ]] && rm -f "$STORAGE_DIR"
  install -d -o "$SVC_USER" -g "$SVC_USER" -m 700 "$STORAGE_DIR"
  _st_result_dir
  # точка монтирования: пока ничего не смонтировано — root 0755, служба туда не пишет (проверяет, что это точка монтирования)
  [[ -d $ARCH_DIR ]] || install -d -o root -g root -m 755 "$ARCH_DIR"
  install -m 644 "$dir/deploy/$STORAGE_UNIT.service" "/etc/systemd/system/$STORAGE_UNIT.service"
  install -m 644 "$dir/deploy/$STORAGE_UNIT.path" "/etc/systemd/system/$STORAGE_UNIT.path"
  systemctl daemon-reload
  systemctl enable --now "$STORAGE_UNIT.path" >/dev/null 2>&1 || warn "cannot enable $STORAGE_UNIT.path"
}

storage_remove_units() {
  systemctl disable --now "$STORAGE_UNIT.path" >/dev/null 2>&1 || true
  rm -f "/etc/systemd/system/$STORAGE_UNIT.service" "/etc/systemd/system/$STORAGE_UNIT.path"
  systemctl daemon-reload 2>/dev/null || true
}

storage_line() { grep -F "$FSTAB_MARK" /etc/fstab 2>/dev/null | head -1 || true; }

# Состояние для status: что подключено и смонтировано ли
storage_status() {
  local l; l=$(storage_line)
  if [[ -z $l ]]; then echo "local buffer only ($REC_DIR)"; return; fi
  local src type; src=$(awk '{print $1}' <<<"$l"); type=$(awk '{print $3}' <<<"$l")
  if mountpoint -q "$ARCH_DIR"; then
    echo "$type $src -> $ARCH_DIR, mounted, free $(df -h --output=avail "$ARCH_DIR" | tail -1 | tr -d ' ')"
  else
    echo "$type $src -> $ARCH_DIR, NOT MOUNTED (recordings wait in $REC_DIR)"
  fi
}

_st_result_dir() { # /run — после перезагрузки пусто; root:служба 0750
  [[ -L $RESULT_DIR ]] && rm -f "$RESULT_DIR"
  install -d -o root -g "$SVC_USER" -m 750 "$RESULT_DIR"
  chown root:"$SVC_USER" "$RESULT_DIR"; chmod 750 "$RESULT_DIR"
}

_st_result() { # _st_result ID OK MESSAGE [EXTRA_JSON]
  _st_result_dir
  local tmp; tmp=$(mktemp "$RESULT_DIR/.result.XXXXXX")
  jq -n --arg id "$1" --argjson ok "$2" --arg msg "$3" --argjson extra "${4:-{\}}" \
    '{id:$id, ok:$ok, message:$msg, at:(now|todate)} + $extra' >"$tmp" \
    || printf '{"id":"%s","ok":false,"message":"internal error"}\n' "$1" >"$tmp"
  chown root:"$SVC_USER" "$tmp"; chmod 640 "$tmp"
  mv -f "$tmp" "$RESULT_DIR/result"
}

_st_pkg() { # пакет для монтирования — из репозиториев ОС, если ещё нет
  rpm -q "$1" >/dev/null 2>&1 && return 0
  log "installing $1"
  dnf -y -q install "$1" >/dev/null 2>&1 || return 1
}

_st_write_test() { # запись и чтение тестового файла от имени службы
  local f="$ARCH_DIR/.linuxops-web-test"
  runuser -u "$SVC_USER" -- sh -c "echo ok >'$f' && test \"\$(cat '$f')\" = ok && rm -f '$f'" 2>/dev/null
}

_st_umount() {
  # зависшее сетевое хранилище: не ждать бесконечно — 20 с, потом принудительно, потом «ленивое»
  if timeout 20 mountpoint -q "$ARCH_DIR"; then timeout 20 umount "$ARCH_DIR" 2>/dev/null || timeout 20 umount -f "$ARCH_DIR" 2>/dev/null || umount -l "$ARCH_DIR" 2>/dev/null || return 1; fi
  return 0
}

_st_fstab_set() { # _st_fstab_set "LINE" | "" — заменить свою строку
  local tmp; tmp=$(mktemp /etc/fstab.lo.XXXXXX)
  grep -vF "$FSTAB_MARK" /etc/fstab >"$tmp" || true
  [[ -n ${1:-} ]] && printf '%s %s\n' "$1" "$FSTAB_MARK" >>"$tmp"
  chmod 644 "$tmp"; chcon --reference=/etc/fstab "$tmp" 2>/dev/null || true
  mv -f "$tmp" /etc/fstab
  systemctl daemon-reload 2>/dev/null || true
}

# Свободные диски: целый диск, без разделов, без подписей ФС, не смонтирован, не системный
_st_free_disks() {
  local all name path
  all=$(lsblk -J -d -b -o NAME,SIZE,TYPE,MODEL)
  local root_disk; root_disk=$(lsblk -no PKNAME "$(findmnt -no SOURCE /)" 2>/dev/null | head -1 || true)
  for name in $(jq -r '.blockdevices[] | select(.type == "disk") | .name' <<<"$all"); do
    [[ $name =~ ^[a-z0-9]+$ ]] || continue
    path=/dev/$name
    [[ $name == "$root_disk" ]] && continue
    [[ $(lsblk -n -o NAME "$path" | wc -l) -eq 1 ]] || continue
    [[ -z $(lsblk -n -o MOUNTPOINTS "$path" | tr -d ' \n') ]] || continue
    blkid -p "$path" >/dev/null 2>&1 && continue   # есть подпись (ФС, LVM, RAID) — не трогаем
    jq -c --arg n "$name" '.blockdevices[] | select(.name == $n) | {name, path:("/dev/" + .name), size:(.size|tonumber), model:(.model // "" | gsub("^\\s+|\\s+$"; ""))}' <<<"$all"
  done | jq -s '{disks: .}'
}

# Не вышло подключить новое — вернуть прежнее (строку fstab, файл учётки SMB, монтирование): рабочее хранилище не теряем.
_st_kept() { [[ -n ${ST_PREV:-} ]] && echo " Previous storage kept."; return 0; }

_st_back() {
  _st_umount || true
  if [[ -f $SMB_CRED.prev ]]; then mv -f "$SMB_CRED.prev" "$SMB_CRED"; else rm -f "$SMB_CRED"; fi
  _st_fstab_set "${ST_PREV:-}"
  [[ -n ${ST_PREV:-} ]] && { mount "$ARCH_DIR" 2>/dev/null || warn "previous storage did not mount back"; }
  return 0
}

_st_mount_common() { # после mount: права, проверка записи
  local id="$1" what="$2"
  if ! mountpoint -q "$ARCH_DIR"; then _st_back; _st_result "$id" false "mount failed: $what.$(_st_kept)"; return 1; fi
  chown "$SVC_USER:$SVC_USER" "$ARCH_DIR" 2>/dev/null || true
  chmod 750 "$ARCH_DIR" 2>/dev/null || true
  if ! _st_write_test; then
    local hint=""
    [[ $what == nfs* ]] && hint=" On the NFS server allow writing for uid $(id -u "$SVC_USER") (or all_squash,anonuid=$(id -u "$SVC_USER"),anongid=$(id -g "$SVC_USER"))."
    _st_back
    _st_result "$id" false "mounted, but the service cannot write there.$hint$(_st_kept)"
    return 1
  fi
  return 0
}

cmd_storage_apply() {
  local req="$STORAGE_DIR/request" body id op type
  [[ -L $STORAGE_DIR || ! -d $STORAGE_DIR ]] && exit 0
  [[ -e $req || -L $req ]] || exit 0
  # запрос — сначала к себе (каталог root): дальше служба его не подменит; ссылка, FIFO, чужой файл — отбросить
  _st_result_dir
  local mine="$RESULT_DIR/.request"
  rm -f "$mine"; mv -f -- "$req" "$mine" 2>/dev/null || { rm -f -- "$req"; exit 0; }
  if [[ -L $mine || ! -f $mine || $(stat -c %u "$mine") != "$(id -u "$SVC_USER")" ]]; then rm -f "$mine"; exit 0; fi
  body=$(head -c 65536 "$mine"); rm -f "$mine"
  id=$(jq -r '.id // ""' <<<"$body" 2>/dev/null || true)
  [[ $id =~ ^[a-f0-9]{32}$ ]] || { _st_result "" false "bad request"; return 0; }
  op=$(jq -r '.op // ""' <<<"$body"); type=$(jq -r '.type // ""' <<<"$body")
  logger -t linuxops-web "хранилище записей: запрос $op $type" 2>/dev/null || true

  case "$op" in
    disks) _st_result "$id" true "" "$(_st_free_disks)" ;;
    unmount)
      _st_umount || { _st_result "$id" false "cannot unmount $ARCH_DIR (busy?)"; return 0; }
      _st_fstab_set ""; rm -f "$SMB_CRED"
      install -d -o root -g root -m 755 "$ARCH_DIR"; chown root:root "$ARCH_DIR"; chmod 755 "$ARCH_DIR"
      _st_result "$id" true "disconnected" ;;
    mount)
      local src opts line
      # любая неожиданная ошибка дальше (set -e) — вернуть прежнее и ответить, а не оставить полусостояние
      set -E
      trap 'trap - ERR; _st_back; _st_result "$id" false "helper error (line $LINENO)$(_st_kept)"; exit 0' ERR
      # прежнее хранилище — на случай неудачи
      ST_PREV=$(storage_line | sed "s| $FSTAB_MARK$||")
      rm -f "$SMB_CRED.prev"; [[ -f $SMB_CRED && ! -L $SMB_CRED ]] && install -o root -g root -m 600 "$SMB_CRED" "$SMB_CRED.prev"
      src=$(jq -r '.source // ""' <<<"$body")
      case "$type" in
        smb)
          [[ $src =~ ^//[A-Za-z0-9._-]+/[^[:space:],\'\"\\]+$ ]] || { _st_result "$id" false "SMB path: //server/share[/folder]"; return 0; }
          local user dom pass vers
          user=$(jq -r '.user // ""' <<<"$body"); dom=$(jq -r '.domain // ""' <<<"$body"); pass=$(jq -r '.password // ""' <<<"$body")
          [[ $user =~ ^[A-Za-z0-9._@-]{1,64}$ ]] || { _st_result "$id" false "SMB user: letters, digits, . _ @ -"; return 0; }
          [[ -z $dom || $dom =~ ^[A-Za-z0-9._-]{1,64}$ ]] || { _st_result "$id" false "SMB domain: letters, digits, . _ -"; return 0; }
          [[ $pass =~ ^[[:print:]]{1,256}$ ]] || { _st_result "$id" false "SMB password: printable characters, 1 to 256"; return 0; }
          _st_pkg cifs-utils || { _st_result "$id" false "cannot install cifs-utils (OS repositories)"; return 0; }
          _st_umount || { _st_result "$id" false "cannot unmount the previous storage"; return 0; }
          # во временный файл в /etc/linuxops-web (каталог root) и mv — не по ссылке и не частично записанный
          local ct; ct=$(mktemp "$ETC_DIR/.smb.XXXXXX")
          { printf 'username=%s\n' "$user"; printf 'password=%s\n' "$pass"; [[ -n $dom ]] && printf 'domain=%s\n' "$dom"; } >"$ct"
          chown root:root "$ct"; chmod 600 "$ct"; mv -f "$ct" "$SMB_CRED"
          opts="credentials=$SMB_CRED,uid=$(id -u "$SVC_USER"),gid=$(id -g "$SVC_USER"),file_mode=0640,dir_mode=0750,soft,nosuid,nodev,noexec,_netdev,nofail,x-systemd.mount-timeout=30"
          line="$src $ARCH_DIR cifs $opts 0 0" ;;
        nfs)
          [[ $src =~ ^[A-Za-z0-9._-]+:/[^[:space:],\'\"\\]*$ ]] || { _st_result "$id" false "NFS path: server:/export[/folder]"; return 0; }
          _st_pkg nfs-utils || { _st_result "$id" false "cannot install nfs-utils (OS repositories)"; return 0; }
          _st_umount || { _st_result "$id" false "cannot unmount the previous storage"; return 0; }
          # soft: пропал сервер — ошибка, а не зависание службы (перенос сверяет размер, недописанное не удаляет из буфера)
          opts="_netdev,nofail,soft,timeo=100,retrans=2,nosuid,nodev,noexec,x-systemd.mount-timeout=30"
          line="$src $ARCH_DIR nfs $opts 0 0" ;;
        disk)
          local confirm uuid name
          confirm=$(jq -r '.confirm // ""' <<<"$body")
          [[ $src =~ ^/dev/(sd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme[0-9]+n[0-9]+)$ ]] || { _st_result "$id" false "bad disk name"; return 0; }
          name=${src#/dev/}
          [[ $confirm == "$name" ]] || { _st_result "$id" false "confirmation does not match the disk name"; return 0; }
          _st_free_disks | jq -e --arg p "$src" '.disks[] | select(.path == $p)' >/dev/null \
            || { _st_result "$id" false "$src is not free (partitions, file system, mounted or system disk) — not touched"; return 0; }
          _st_umount || { _st_result "$id" false "cannot unmount the previous storage"; return 0; }
          log "mkfs.xfs $src"
          mkfs.xfs -q -L lo-rec "$src" >/dev/null 2>&1 || { _st_back; _st_result "$id" false "mkfs.xfs $src failed"; return 0; }
          udevadm settle 2>/dev/null || true
          uuid=$(blkid -s UUID -o value "$src" 2>/dev/null || true)
          [[ -n $uuid ]] || { _st_back; _st_result "$id" false "no UUID after mkfs"; return 0; }
          logger -t linuxops-web "хранилище записей: диск $src размечен (xfs, UUID=$uuid)" 2>/dev/null || true
          line="UUID=$uuid $ARCH_DIR xfs defaults,nofail,nosuid,nodev,noexec 0 0" ;;
        *) _st_result "$id" false "unknown storage type"; return 0 ;;
      esac
      install -d -m 755 "$ARCH_DIR" || true
      _st_fstab_set "$line"
      local err
      if ! err=$(mount "$ARCH_DIR" 2>&1); then
        _st_back
        _st_result "$id" false "mount failed: ${err:0:300}.$(_st_kept)"; return 0
      fi
      _st_mount_common "$id" "$type $src" || return 0
      # SELinux: служба — unconfined_service_t, метка точки монтирования не мешает; локальный диск — штатная метка var_lib_t
      [[ $type == disk ]] && { restorecon -R "$ARCH_DIR" 2>/dev/null || true; }
      rm -f "$SMB_CRED.prev"; [[ $type == smb ]] || rm -f "$SMB_CRED"
      logger -t linuxops-web "хранилище записей: подключено $type $src" 2>/dev/null || true
      _st_result "$id" true "connected: $(storage_status)" ;;
    *) _st_result "$id" false "unknown operation" ;;
  esac
}
