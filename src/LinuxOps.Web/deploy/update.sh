#!/usr/bin/env bash
# Обновление LinuxOps Web.
#   update.sh                    — последний релиз из источника (/etc/linuxops-web/release.conf)
#   update.sh --version 0.2.1    — конкретная версия из источника
#   update.sh --file X.tar.gz    — из архива на сервере
# Архив проверяется по .sha256 (если есть), распаковывается и ставится его же install.sh --upgrade.
# Служба не поднялась — автоматически назад на прежнюю версию.
set -euo pipefail
# shellcheck source=lib/common.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/common.sh"

FILE="" WANT="" FORCE=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --file) FILE="${2:?}"; shift ;;
    --version) WANT="${2:?}"; shift ;;
    --force) FORCE=1 ;;
    -h|--help) sed -n '2,7p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
  shift
done
need_root
# путь к архиву — до cd: относительный путь иначе указал бы в /tmp
if [[ -n "$FILE" ]]; then FILE=$(realpath -e -- "$FILE") || die "no file: $FILE"; fi
[[ -z "$WANT" || "$WANT" =~ ^[0-9]+(\.[0-9]+){2,3}$ ]] || die "bad version: $WANT (expected like 0.7.5)"
cd /tmp

TMP=$(mktemp -d /var/tmp/linuxops-web-update.XXXXXX)
trap 'rm -rf "$TMP"' EXIT

if [[ -n "$FILE" ]]; then
  [[ -f "$FILE" ]] || die "no file: $FILE"
  cp "$FILE" "$TMP/release.tar.gz"
  [[ -f "$FILE.sha256" ]] && cp "$FILE.sha256" "$TMP/release.tar.gz.sha256"
else
  release_conf_load
  row=$(release_find "$WANT") || exit 1
  [[ -n "$row" ]] || die "no release ${WANT:+$WANT }found in $(release_where) ($PROVIDER)"
  IFS=$'\t' read -r tag url sha <<<"$row"
  [[ -n "$url" ]] || die "release $tag has no *$ASSET_SUFFIX asset"
  ver=${tag#"$TAG_PREFIX"}
  if [[ "$ver" == "$(current_version)" && $FORCE -eq 0 ]]; then
    log "already on $ver (use --force to reinstall)"; exit 0
  fi
  log "downloading $tag"
  release_download "$url" "$TMP/release.tar.gz" || die "download failed"
  [[ -n "$sha" ]] && { release_download "$sha" "$TMP/release.tar.gz.sha256" || die "checksum download failed"; }
fi

if [[ -f "$TMP/release.tar.gz.sha256" ]]; then
  want=$(awk '{print $1; exit}' "$TMP/release.tar.gz.sha256")
  got=$(sha256sum "$TMP/release.tar.gz" | awk '{print $1}')
  [[ "$want" == "$got" ]] || die "checksum mismatch: archive is damaged or substituted"
  log "checksum OK"
else
  warn "no .sha256 next to the archive — integrity not checked"
fi

mkdir "$TMP/x"
tar -xzf "$TMP/release.tar.gz" -C "$TMP/x"
bin=$(find "$TMP/x" -maxdepth 2 -name LinuxOps.Web -type f | head -1)
[[ -n "$bin" ]] || die "archive has no LinuxOps.Web — not a LinuxOps Web release"
dir=$(dirname "$bin")
[[ -f "$dir/deploy/install.sh" ]] || die "archive has no deploy/install.sh"
VER=$(release_version "$dir")
[[ "$VER" =~ ^[0-9]+(\.[0-9]+){2,3}$ ]] || die "bad version in archive: $VER"
# Каталог релиза называем по версии, чтобы откат и список версий читались.
[[ "$(basename "$dir")" == "$VER" ]] || { mv "$dir" "$TMP/x/$VER"; dir="$TMP/x/$VER"; }
log "installing $VER (was $(current_version))"
bash "$dir/deploy/install.sh" --upgrade
