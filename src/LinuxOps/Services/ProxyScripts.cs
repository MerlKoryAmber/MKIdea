namespace LinuxOps.Services;

/// <summary>
/// Прокси на хостах Linux: apt / dnf·yum / zypper и (по желанию) окружение. Пишем только в свои файлы и помеченные блоки,
/// перед первой правкой файла — копия *.linuxops.bak. Параметры приходят переменными окружения (Proxy, NoProxy, Env),
/// LINUXOPS_DRY=1 — только показать, что будет сделано.
/// </summary>
public static class ProxyScripts
{
    private const string Lib = """
        set -u
        MARK_B='# LinuxOps proxy BEGIN'
        MARK_E='# LinuxOps proxy END'
        APT_FILE=/etc/apt/apt.conf.d/95linuxops-proxy
        PROFILE_FILE=/etc/profile.d/linuxops-proxy.sh
        ENV_FILE=/etc/environment
        SUSE_FILE=/etc/sysconfig/proxy
        DRY="${LINUXOPS_DRY:-}"
        CHANGED=0
        fail() { echo "ОШИБКА: $*"; exit 1; }
        pm() {
          if command -v apt-get >/dev/null 2>&1; then echo apt
          elif command -v dnf >/dev/null 2>&1 || command -v yum >/dev/null 2>&1; then echo yum
          elif command -v zypper >/dev/null 2>&1; then echo zypper
          else echo none; fi
        }
        yum_conf() { if [ -f /etc/dnf/dnf.conf ]; then echo /etc/dnf/dnf.conf; elif [ -f /etc/yum.conf ]; then echo /etc/yum.conf; fi; }
        backup() { [ -n "$DRY" ] && return 0; if [ -f "$1" ] && [ ! -e "$1.linuxops.bak" ]; then cp -p "$1" "$1.linuxops.bak"; fi; return 0; }
        note() { if [ -n "$DRY" ]; then echo "  будет: $*"; else echo "  $*"; fi; CHANGED=1; }
        # Записать файл целиком (содержимое — stdin).
        put() { if [ -n "$DRY" ]; then note "записать $1:"; sed 's/^/      /'; else cat > "$1" && chmod 644 "$1" && note "записан $1"; fi; }
        drop() { if [ -f "$1" ]; then if [ -n "$DRY" ]; then note "удалить $1"; else rm -f "$1" && note "удалён $1"; fi; fi; }
        strip_block() {
          [ -f "$1" ] || return 0
          grep -qxF "$MARK_B" "$1" || return 0
          if [ -n "$DRY" ]; then note "убрать блок LinuxOps из $1"; else sed -i "/^$MARK_B\$/,/^$MARK_E\$/d" "$1" && note "убран блок LinuxOps из $1"; fi
        }
        # Убрать всё, что прописал LinuxOps. $1=keep-foreign — отключённые чужие proxy= в dnf/yum не возвращать (сейчас пропишем свой).
        clean_all() {
          drop "$APT_FILE"
          c=$(yum_conf)
          if [ -n "$c" ]; then
            strip_block "$c"
            if [ "${1:-}" != keep-foreign ] && grep -q '^#LinuxOps-disabled: ' "$c"; then
              if [ -n "$DRY" ]; then note "вернуть прежние proxy-настройки в $c"; else sed -i 's/^#LinuxOps-disabled: //' "$c" && note "возвращены прежние proxy-настройки в $c"; fi
            fi
          fi
          if [ "${1:-}" != keep-foreign ] && [ -f "$SUSE_FILE.linuxops.bak" ]; then
            if [ -n "$DRY" ]; then note "вернуть $SUSE_FILE из копии"; else cp -p "$SUSE_FILE.linuxops.bak" "$SUSE_FILE" && rm -f "$SUSE_FILE.linuxops.bak" && note "возвращён $SUSE_FILE из копии"; fi
          fi
          drop "$PROFILE_FILE"
          strip_block "$ENV_FILE"
        }
        # Обновить списки пакетов — проверка, что репозитории доступны (через прокси, если он прописан).
        refresh() {
          case "$(pm)" in
            apt)
              out=$(DEBIAN_FRONTEND=noninteractive apt-get update 2>&1); rc=$?
              if [ $rc -eq 0 ] && printf '%s\n' "$out" | grep -qE '^(E:|W: (Failed to fetch|Some index files failed))'; then rc=1; fi ;;
            yum)
              if command -v dnf >/dev/null 2>&1; then out=$(dnf -q makecache --refresh --setopt=retries=1 --setopt=timeout=15 2>&1); rc=$?
              else out=$(yum -q makecache --setopt=retries=1 --setopt=timeout=15 2>&1); rc=$?; fi ;;
            zypper)
              out=$(zypper --non-interactive refresh 2>&1); rc=$? ;;
            *) out="нет apt, dnf, yum или zypper"; rc=1 ;;
          esac
          if [ $rc -ne 0 ]; then printf '%s\n' "$out" | grep -v '^\s*$' | tail -n 6 | sed 's/^/    /'; fi
          return $rc
        }

        """;

    public static string Set => Lib + """
        P="${Proxy:-}"; NP="${NoProxy:-}"; E="${Env:-да}"
        P="${P%/}"
        [ -n "$P" ] || fail "укажите адрес прокси, например http://proxy.corp.local:3128"
        printf '%s' "$P" | grep -Eq '^https?://[A-Za-z0-9._-]+(:[0-9]{1,5})?$' || fail "адрес прокси — http(s)://имя:порт, без логина, пароля и пути: $P"
        if [ -n "$NP" ]; then printf '%s' "$NP" | grep -Eq '^[A-Za-z0-9.,:*/_ -]+$' || fail "исключения — имена, IP и сети через запятую: $NP"; fi
        NP=$(printf '%s' "$NP" | tr -d ' ')
        case "$E" in да|Да|ДА|yes|Yes|y|1|true) E=1 ;; *) E=0 ;; esac
        PM=$(pm)
        [ "$PM" != none ] || fail "нет apt, dnf, yum или zypper"
        echo "Прокси: $P · исключения: ${NP:-нет} · менеджер пакетов: $PM · окружение: $([ $E = 1 ] && echo да || echo нет)"
        clean_all keep-foreign
        case "$PM" in
          apt)
            {
              echo "// LinuxOps proxy — убрать: действие «Прокси: убрать»"
              echo "Acquire::http::Proxy \"$P\";"
              echo "Acquire::https::Proxy \"$P\";"
              # Исключения apt понимает только по имени хоста: Acquire::http::Proxy::<хост> "DIRECT".
              set -f
              for h in $(printf '%s' "$NP" | tr ',' ' '); do
                case "$h" in *[/*:]*|.*|'') ;; *) echo "Acquire::http::Proxy::$h \"DIRECT\";"; echo "Acquire::https::Proxy::$h \"DIRECT\";" ;; esac
              done
              set +f
            } | put "$APT_FILE" ;;
          yum)
            c=$(yum_conf); [ -n "$c" ] || fail "нет /etc/dnf/dnf.conf и /etc/yum.conf"
            backup "$c"
            # Чужие proxy= / proxy_* в [main] — отключаем (два proxy= dnf не любит); «Прокси: убрать» вернёт их.
            if awk '/^\[/{s=($0=="[main]")} s && /^[ \t]*proxy(_[a-z_]*)?[ \t]*=/{f=1} END{exit !f}' "$c"; then
              if [ -n "$DRY" ]; then note "отключить имеющиеся proxy-настройки в [main] $c"
              else sed -i '/^\[main\]/,/^\[/{s/^\([ \t]*proxy\(_[a-z_]*\)\{0,1\}[ \t]*=.*\)$/#LinuxOps-disabled: \1/}' "$c" && note "отключены прежние proxy-настройки в $c"; fi
            fi
            if [ -n "$DRY" ]; then note "в [main] $c: proxy=$P"
            elif grep -q '^\[main\]' "$c"; then sed -i "/^\[main\]/a $MARK_B\nproxy=$P\n$MARK_E" "$c" && note "в [main] $c: proxy=$P"
            else printf '\n[main]\n%s\nproxy=%s\n%s\n' "$MARK_B" "$P" "$MARK_E" >> "$c" && note "в $c добавлен [main] с proxy=$P"; fi
            [ -z "$NP" ] || echo "  dnf/yum исключений не понимают — для внутреннего репозитория укажите proxy=_none_ в его .repo" ;;
          zypper)
            backup "$SUSE_FILE"
            setkv() {
              if [ -n "$DRY" ]; then note "$SUSE_FILE: $1=\"$2\""; return 0; fi
              touch "$SUSE_FILE"
              if grep -q "^$1=" "$SUSE_FILE"; then sed -i "s|^$1=.*|$1=\"$2\"|" "$SUSE_FILE"; else echo "$1=\"$2\"" >> "$SUSE_FILE"; fi
              note "$SUSE_FILE: $1=\"$2\""
            }
            setkv PROXY_ENABLED yes; setkv HTTP_PROXY "$P"; setkv HTTPS_PROXY "$P"; setkv NO_PROXY "${NP:-localhost,127.0.0.1}" ;;
        esac
        if [ $E = 1 ]; then
          {
            echo "# LinuxOps proxy — убрать: действие «Прокси: убрать»"
            echo "export http_proxy=\"$P\" https_proxy=\"$P\" HTTP_PROXY=\"$P\" HTTPS_PROXY=\"$P\""
            if [ -n "$NP" ]; then echo "export no_proxy=\"$NP\" NO_PROXY=\"$NP\""; fi
          } | put "$PROFILE_FILE"
          backup "$ENV_FILE"
          if [ -n "$DRY" ]; then note "добавить в $ENV_FILE блок LinuxOps (http_proxy, https_proxy${NP:+, no_proxy})"
          else
            {
              echo "$MARK_B"
              echo "http_proxy=\"$P\""; echo "https_proxy=\"$P\""; echo "HTTP_PROXY=\"$P\""; echo "HTTPS_PROXY=\"$P\""
              if [ -n "$NP" ]; then echo "no_proxy=\"$NP\""; echo "NO_PROXY=\"$NP\""; fi
              echo "$MARK_E"
            } >> "$ENV_FILE" && note "добавлен блок LinuxOps в $ENV_FILE"
          fi
        fi
        if [ -n "$DRY" ]; then echo "Итого (dry-run): будет прописан прокси $P — ничего не изменено"; exit 0; fi
        echo "Проверка: обновление списков пакетов через прокси…"
        if refresh; then echo "Итого: прокси $P прописан ($PM$([ $E = 1 ] && echo ', окружение')), списки пакетов обновились"
        else fail "прокси $P прописан, но списки пакетов через него не обновились — проверьте адрес; убрать — «Прокси: убрать»"; fi
        """;

    public static string Unset => Lib + """
        clean_all
        if [ $CHANGED = 0 ]; then echo "Итого: настроек прокси от LinuxOps нет — менять нечего"; exit 0; fi
        if [ -n "$DRY" ]; then echo "Итого (dry-run): будут убраны настройки прокси LinuxOps — ничего не изменено"; exit 0; fi
        echo "Итого: настройки прокси LinuxOps убраны, чужие не трогались"
        """;

    /// <summary>
    /// Только чтение. Строки для таблицы LinuxOps (разделитель TAB): PM, R (где · значение · кто: lo / off / other), CUR, REPO;
    /// последняя строка «Итого: …» — для людей и превью.
    /// </summary>
    public static string Check => Lib + """
        # $1 файл, $2 подпись, $3 регулярное выражение строки, $4 кто, если не в блоке LinuxOps (other по умолчанию).
        rows_of() {
          [ -f "$1" ] || return 0
          awk -v where="$2" -v re="$3" -v def="${4:-other}" -v mb="$MARK_B" -v me="$MARK_E" '
            $0 == mb { b = 1; next }
            $0 == me { b = 0; next }
            /^\[/ { sec = " " $0 }
            /^#LinuxOps-disabled: / { v = substr($0, 21); if (v ~ re) printf "R\t%s%s\t%s\toff\n", where, sec, v; next }
            /^[ \t]*(#|\/\/)/ { next }
            $0 ~ re { v = $0; sub(/^[ \t]+/, "", v); printf "R\t%s%s\t%s\t%s\n", where, sec, v, (b ? "lo" : def) }
          ' "$1"
        }
        PM=$(pm)
        printf 'PM\t%s\n' "$PM"
        CUR=""; URL=""
        case "$PM" in
          apt)
            for f in /etc/apt/apt.conf /etc/apt/apt.conf.d/*; do
              [ -f "$f" ] || continue
              o=other; [ "$f" = "$APT_FILE" ] && o=lo
              rows_of "$f" "apt · $f" 'Acquire::https?::Proxy' "$o"
            done
            CUR=$(grep -rhsE '^[^/#]*Acquire::http::Proxy +"' /etc/apt/apt.conf /etc/apt/apt.conf.d/ 2>/dev/null | grep -v DIRECT | head -n1 | sed -E 's/.*"([^"]*)".*/\1/')
            URL=$(grep -rhsE '^(deb |URIs:)' /etc/apt/sources.list /etc/apt/sources.list.d/ 2>/dev/null | grep -oE 'https?://[^ ]+' | head -n1) ;;
          yum)
            c=$(yum_conf)
            if [ -n "$c" ]; then rows_of "$c" "dnf · $c" '^[ \t]*proxy'; fi
            for f in /etc/yum.repos.d/*.repo; do rows_of "$f" "dnf · $f" '^[ \t]*proxy'; done
            CUR=$(awk '/^\[/{s=($0=="[main]")} s && /^[ \t]*proxy[ \t]*=/{sub(/^[ \t]*proxy[ \t]*=[ \t]*/,""); print; exit}' "${c:-/dev/null}")
            URL=$(grep -hsE '^(baseurl|mirrorlist|metalink)[ \t]*=' /etc/yum.repos.d/*.repo 2>/dev/null | grep -oE 'https?://[^ ]+' | head -n1) ;;
          zypper)
            o=other; [ -f "$SUSE_FILE.linuxops.bak" ] && o=lo
            rows_of "$SUSE_FILE" "zypper · $SUSE_FILE" '^(PROXY_ENABLED|HTTP_PROXY|HTTPS_PROXY|NO_PROXY)=' "$o"
            if grep -q '^PROXY_ENABLED="yes"' "$SUSE_FILE" 2>/dev/null; then CUR=$(sed -n 's/^HTTP_PROXY="\(.*\)"$/\1/p' "$SUSE_FILE" | head -n1); fi
            URL=$(grep -hs '^baseurl=' /etc/zypp/repos.d/*.repo 2>/dev/null | grep -oE 'https?://[^ ]+' | head -n1) ;;
        esac
        rows_of "$ENV_FILE" "$ENV_FILE" '_proxy=|_PROXY='
        rows_of "$PROFILE_FILE" "$PROFILE_FILE" '_proxy=|_PROXY=' lo
        for f in /etc/profile.d/*.sh; do [ "$f" = "$PROFILE_FILE" ] || rows_of "$f" "$f" '_proxy=|_PROXY='; done
        printf 'CUR\t%s\n' "$CUR"
        BASE=$(printf '%s' "$URL" | sed -E 's#^(https?://[^/]+).*#\1/#')
        RES="репозиторий не найден"; code=""
        if [ -n "$BASE" ]; then
          if command -v curl >/dev/null 2>&1; then
            if [ -n "$CUR" ]; then code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 -x "$CUR" "$BASE")
            else code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 --noproxy '*' "$BASE"); fi
            code=${code:-000}
            if [ "$code" = 000 ]; then RES="$BASE недоступен"; else RES="$BASE доступен (HTTP $code)"; fi
          else code=nocurl; RES="нет curl — доступность $BASE не проверена"; fi
        fi
        printf 'REPO\t%s\t%s\n' "$BASE" "$code"
        echo "Итого: прокси ${CUR:-не задан} · $RES"
        """;
}
