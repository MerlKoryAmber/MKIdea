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

[[ "$RAW_BASE" == https://* ]] || die "LINUXOPS_WEB_RAW_BASE must be https://"
[[ -z "$TOKEN" || "$TOKEN" =~ ^[A-Za-z0-9_.:-]{1,512}$ ]] || die "LINUXOPS_WEB_TOKEN has unexpected characters"
auth_line() { if [[ "$RAW_BASE" == */-/raw/* ]]; then printf 'PRIVATE-TOKEN: %s\n' "$TOKEN"; else printf 'Authorization: Bearer %s\n' "$TOKEN"; fi; }
url_host() { local u=${1#https://}; u=${u%%/*}; u=${u##*@}; printf '%s' "${u,,}"; }
# Только https; токен — из файла (не виден в списке процессов) и только хосту источника: при переходе на другой хост
# (LFS → media.githubusercontent.com) — без токена. CODE — последний HTTP-код.
CODE=""
fetch() { # fetch URL FILE [max-time]
  local url=$1 host0 hf w rc i
  host0=$(url_host "$url")
  for i in 1 2 3 4 5 6 7 8; do
    [[ $url == https://* ]] || return 1
    local args=(-sS --retry 2 --proto =https --max-time "${3:-60}" -o "$2" -w '%{http_code} %{redirect_url}')
    hf=""
    if [[ -n "$TOKEN" && $(url_host "$url") == "$host0" ]]; then hf=$(mktemp); chmod 600 "$hf"; auth_line >"$hf"; args+=(-H "@$hf"); fi
    rc=0; w=$(curl "${args[@]}" "$url") || rc=$?
    [[ -n $hf ]] && rm -f "$hf"
    CODE=${w%% *}
    [[ $rc -eq 0 ]] || return "$rc"
    case $CODE in 2??) return 0 ;; 30[12378]) url=${w#* }; [[ -n $url ]] || return 22 ;; *) return 22 ;; esac
  done
  return 47
}

TMP=$(mktemp -d /var/tmp/linuxops-web-get.XXXXXX)
trap 'rm -rf "$TMP"' EXIT

if [[ -z "$VERSION" ]]; then
  if ! fetch "$RAW_BASE/LATEST" "$TMP/LATEST"; then
    code=$CODE
    [[ "$code" == 404 ]] && die "nothing published in $RAW_BASE (no LATEST): build with package-linuxops-web.ps1 -Publish and push; a private repo also answers 404 without a token"
    die "cannot read $RAW_BASE/LATEST (HTTP ${code:-none}: no network or no access)"
  fi
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
