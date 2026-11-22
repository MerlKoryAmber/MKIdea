#!/usr/bin/env bash
# Установка LinuxOps Web одной командой — скачать последний релиз из репозитория, сверить сумму и поставить:
#   curl -fsSL https://raw.githubusercontent.com/MerlKoryAmber/MKIdea/feature/linuxops-web/src/LinuxOps.Web/deploy/get.sh | sudo bash
# Версия и ключи install.sh — после «-s --»:
#   curl -fsSL …/get.sh | sudo bash -s -- --version 0.7.3 --no-firewall
# Свой источник (внутренний GitLab) и токен на чтение (закрытый репозиторий) — через окружение:
#   curl -fsSL …/get.sh | sudo LINUXOPS_WEB_RAW_BASE=https://gitlab.local/grp/mkidea/-/raw/main/releases/linuxops-web \
#        LINUXOPS_WEB_TOKEN=… bash
# Уже установлен — обновление из меню: sudo linuxops-web → Update. Проверить источник без установки — --check.
set -euo pipefail

RAW_BASE="${LINUXOPS_WEB_RAW_BASE:-https://github.com/MerlKoryAmber/MKIdea/raw/feature/linuxops-web/releases/linuxops-web}"
TOKEN="${LINUXOPS_WEB_TOKEN:-}"
VERSION=""
CHECK=0
ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="${2:?}"; shift ;;
    --check) CHECK=1 ;;   # только скачать и сверить сумму, не ставить
    -h|--help) sed -n '2,9p' "$0" 2>/dev/null || true; exit 0 ;;
    *) ARGS+=("$1") ;;
  esac
  shift
done

say() { echo -e "\033[0;32m[linuxops-web]\033[0m $*"; }
die() { echo -e "\033[0;31m[linuxops-web] ERROR:\033[0m $*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "run as root: curl … | sudo bash"
for c in curl tar sha256sum; do command -v "$c" >/dev/null || die "$c is required"; done
if [[ $CHECK -eq 0 && ( -e /opt/linuxops-web/current || -n "$(command -v linuxops-web 2>/dev/null)" ) ]]; then
  die "LinuxOps Web is already installed — update: sudo linuxops-web update"
fi

H=()
if [[ -n "$TOKEN" ]]; then
  if [[ "$RAW_BASE" == */-/raw/* ]]; then H=(-H "PRIVATE-TOKEN: $TOKEN"); else H=(-H "Authorization: Bearer $TOKEN"); fi
fi
fetch() { curl -fSL --retry 2 --max-time "${3:-60}" "${H[@]}" -o "$2" "$1"; }

TMP=$(mktemp -d /var/tmp/linuxops-web-get.XXXXXX)
trap 'rm -rf "$TMP"' EXIT

if [[ -z "$VERSION" ]]; then
  fetch "$RAW_BASE/LATEST" "$TMP/LATEST" || die "cannot read $RAW_BASE/LATEST (no network or no access)"
  VERSION=$(tr -d ' \r\n' <"$TMP/LATEST")
fi
[[ "$VERSION" =~ ^[0-9]+(\.[0-9]+){2,3}$ ]] || die "bad version: $VERSION"
NAME="LinuxOps.Web-$VERSION-linux-x64.tar.gz"

say "downloading $NAME"
fetch "$RAW_BASE/$NAME.sha256" "$TMP/$NAME.sha256" || die "no release $VERSION in $RAW_BASE"
fetch "$RAW_BASE/$NAME" "$TMP/$NAME" 900 || die "download failed"
want=$(awk '{print $1; exit}' "$TMP/$NAME.sha256")
got=$(sha256sum "$TMP/$NAME" | awk '{print $1}')
# LFS: без git-lfs на стороне хранилища вместо архива приходит короткий файл-указатель — сумма не сойдётся
[[ "$want" == "$got" ]] || die "checksum mismatch: archive is damaged or substituted ($(stat -c %s "$TMP/$NAME") bytes)"
say "checksum OK"
if [[ $CHECK -eq 1 ]]; then say "check only: $NAME from $RAW_BASE is OK"; exit 0; fi

tar -xzf "$TMP/$NAME" -C "$TMP"
DIR="$TMP/LinuxOps.Web-$VERSION"
[[ -x "$DIR/deploy/install.sh" || -f "$DIR/deploy/install.sh" ]] || die "archive has no deploy/install.sh"
# источник релизов — тот же, откуда поставили: дальше меню Update берёт отсюда
export LINUXOPS_WEB_RAW_BASE="$RAW_BASE"
bash "$DIR/deploy/install.sh" "${ARGS[@]}"
if [[ -n "$TOKEN" ]]; then ( umask 077; printf '%s\n' "$TOKEN" >/etc/linuxops-web/git-token ); say "token saved for updates"; fi
say "next: set the local admin password — sudo linuxops-web → 9"
