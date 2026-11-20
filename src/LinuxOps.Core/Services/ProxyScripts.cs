namespace LinuxOps.Services;

/// <summary>
/// Прокси на хостах Linux: apt / dnf·yum / zypper и (по желанию) окружение.
/// «Прописать» заменяет чужой прокси (например, зашитый в шаблон ВМ): чужие строки в конфигах отключаются пометкой
/// LinuxOps-disabled, чужие скрипты profile.d не правятся — наш zz-linuxops-proxy.sh выполняется последним и их перекрывает.
/// «Убрать» удаляет своё и возвращает отключённое. Перед первой правкой файла — копия *.linuxops.bak.
/// Параметры — переменными окружения (Proxy, NoProxy, Env), LINUXOPS_DRY=1 — только показать.
/// </summary>
public static class ProxyScripts
{
    private const string Lib = """
        set -u
        MARK_B='# LinuxOps proxy BEGIN'
        MARK_E='# LinuxOps proxy END'
        DIS='#LinuxOps-disabled: '
        ADIS='//LinuxOps-disabled: '
        APT_FILE=/etc/apt/apt.conf.d/95linuxops-proxy
        PROFILE_FILE=/etc/profile.d/zz-linuxops-proxy.sh
        OLD_PROFILE=/etc/profile.d/linuxops-proxy.sh
        ENV_FILE=/etc/environment
        SUSE_FILE=/etc/sysconfig/proxy
        # Строки с прокси (ERE, для grep/sed): окружение, apt, dnf/yum (кроме proxy=_none_ — это «напрямую»).
        RE_ENV='^[[:space:]]*(export[[:space:]]+)?[A-Za-z_]*_(proxy|PROXY)='
        # apt: только общий прокси (Acquire::http::Proxy "…"); исключения Acquire::http::Proxy::<хост> "DIRECT" не трогаем.
        RE_APT='^[[:space:]]*Acquire::(http|https|ftp)::Proxy[[:space:]]+"'
        RE_YUM='^[[:space:]]*proxy([[:space:]]*=[[:space:]]*[^_[:space:]]|_[a-z_]+[[:space:]]*=)'
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
        apt_files() { for f in /etc/apt/apt.conf /etc/apt/apt.conf.d/*; do [ -f "$f" ] && [ "$f" != "$APT_FILE" ] && case "$f" in *.linuxops.bak) ;; *) echo "$f" ;; esac; done; }
        repo_files() { for f in /etc/yum.repos.d/*.repo; do [ -f "$f" ] && echo "$f"; done; }
        # Копия перед первой правкой: рядом с файлом (*.linuxops.bak); для apt.conf.d — в /var/backups/linuxops (apt читает все файлы папки).
        bak_of() { case "$1" in /etc/apt/apt.conf.d/*) echo "/var/backups/linuxops/apt.conf.d.${1##*/}" ;; *) echo "$1.linuxops.bak" ;; esac; }
        backup() {
          [ -n "$DRY" ] && return 0
          b=$(bak_of "$1")
          if [ -f "$1" ] && [ ! -e "$b" ]; then mkdir -p "${b%/*}" && cp -p "$1" "$b"; fi
          return 0
        }
        note() { if [ -n "$DRY" ]; then echo "  будет: $*"; else echo "  $*"; fi; CHANGED=1; }
        # Записать файл целиком (содержимое — stdin).
        put() { if [ -n "$DRY" ]; then note "записать $1:"; sed 's/^/      /'; else cat > "$1" && chmod 644 "$1" && note "записан $1"; fi; }
        drop() { if [ -f "$1" ]; then if [ -n "$DRY" ]; then note "удалить $1"; else rm -f "$1" && note "удалён $1"; fi; fi; }
        strip_block() {
          [ -f "$1" ] || return 0
          grep -qxF "$MARK_B" "$1" || return 0
          if [ -n "$DRY" ]; then note "убрать блок LinuxOps из $1"; else sed -i "/^$MARK_B\$/,/^$MARK_E\$/d" "$1" && note "убран блок LinuxOps из $1"; fi
        }
        # Отключить чужие строки: $1 файл, $2 ERE (с ^), $3 пометка. Вне блока LinuxOps — наш блок к этому моменту уже убран.
        disable_lines() {
          [ -f "$1" ] || return 0
          n=$(grep -cE "$2" "$1"); [ "${n:-0}" -gt 0 ] || return 0
          if [ -n "$DRY" ]; then note "отключить чужой прокси в $1 (строк: $n)"; return 0; fi
          backup "$1"
          sed -i -E "s~$2~$3&~" "$1" && note "отключён чужой прокси в $1 (строк: $n)"
        }
        # Вернуть отключённое: $1 файл, $2 пометка.
        restore_lines() {
          [ -f "$1" ] || return 0
          grep -qF "$2" "$1" || return 0
          if [ -n "$DRY" ]; then note "вернуть прежний прокси в $1"; return 0; fi
          sed -i "s~^$2~~" "$1" && note "возвращён прежний прокси в $1"
        }
        restore_env() { restore_lines "$ENV_FILE" "$DIS"; }
        # Убрать всё, что прописал LinuxOps. $1=keep-foreign — отключённое чужое не возвращать (сейчас пропишем своё).
        clean_all() {
          drop "$APT_FILE"
          c=$(yum_conf)
          [ -z "$c" ] || strip_block "$c"
          drop "$PROFILE_FILE"; drop "$OLD_PROFILE"
          strip_block "$ENV_FILE"
          [ "${1:-}" != keep-foreign ] || return 0
          for f in $(apt_files); do restore_lines "$f" "$ADIS"; done
          [ -z "$c" ] || restore_lines "$c" "$DIS"
          for f in $(repo_files); do restore_lines "$f" "$DIS"; done
          restore_env
          if [ -f "$SUSE_FILE.linuxops.bak" ]; then
            if [ -n "$DRY" ]; then note "вернуть $SUSE_FILE из копии"; else cp -p "$SUSE_FILE.linuxops.bak" "$SUSE_FILE" && rm -f "$SUSE_FILE.linuxops.bak" && note "возвращён $SUSE_FILE из копии"; fi
          fi
        }
        # Скрипты profile.d, где задаётся прокси (кроме нашего).
        foreign_profiles() { for f in /etc/profile.d/*.sh; do [ -f "$f" ] && [ "$f" != "$PROFILE_FILE" ] && grep -qE "$RE_ENV" "$f" && echo "$f"; done; }
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
          if [ $rc -ne 0 ]; then printf '%s\n' "$out" | grep -v '^[[:space:]]*$' | tail -n 6 | sed 's/^/    /'; fi
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
            for f in $(apt_files); do disable_lines "$f" "$RE_APT" "$ADIS"; done
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
            # Чужой прокси — в [main] и в .repo (у репозитория он главнее [main]); proxy=_none_ («напрямую») не трогаем.
            disable_lines "$c" "$RE_YUM" "$DIS"
            for f in $(repo_files); do disable_lines "$f" "$RE_YUM" "$DIS"; done
            backup "$c"
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
          disable_lines "$ENV_FILE" "$RE_ENV" "$DIS"
          # Чужие скрипты profile.d не правим (в них бывает логика) — наш zz-… выполняется последним и перекрывает их.
          {
            echo "# LinuxOps proxy — убрать: действие «Прокси: убрать». Выполняется последним и перекрывает чужие настройки."
            for v in http_proxy https_proxy ftp_proxy HTTP_PROXY HTTPS_PROXY FTP_PROXY; do echo "export $v=\"$P\""; done
            if [ -n "$NP" ]; then echo "export no_proxy=\"$NP\""; echo "export NO_PROXY=\"$NP\""
            else echo "unset no_proxy NO_PROXY"; fi
            echo "unset all_proxy ALL_PROXY"
          } | put "$PROFILE_FILE"
          for f in $(foreign_profiles); do echo "  чужой $f перекрыт нашим $PROFILE_FILE (выполняется позже), сам файл не тронут"; done
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
        else
          # Окружение не наше: возвращаем отключённое раньше и предупреждаем о чужом.
          restore_env
          for f in $(foreign_profiles); do echo "  окружение не трогали: в $f остаётся чужой прокси"; done
          if grep -qE "$RE_ENV" "$ENV_FILE" 2>/dev/null; then echo "  окружение не трогали: в $ENV_FILE остаётся чужой прокси"; fi
        fi
        if [ -n "$DRY" ]; then echo "Итого (dry-run): будет прописан прокси $P — ничего не изменено"; exit 0; fi
        echo "Проверка: обновление списков пакетов через прокси…"
        if refresh; then echo "Итого: прокси $P прописан ($PM$([ $E = 1 ] && echo ', окружение')), списки пакетов обновились"
        else fail "прокси $P прописан, но списки пакетов через него не обновились — проверьте адрес; убрать — «Прокси: убрать»"; fi
        """;

    public static string Unset => Lib + """
        clean_all
        if [ $CHANGED = 0 ]; then echo "Итого: настроек прокси от LinuxOps нет — менять нечего"; exit 0; fi
        if [ -n "$DRY" ]; then echo "Итого (dry-run): будут убраны настройки прокси LinuxOps и возвращён прежний прокси — ничего не изменено"; exit 0; fi
        echo "Итого: настройки прокси LinuxOps убраны, прежние (если были) возвращены"
        """;

    /// <summary>
    /// Только чтение. Строки для таблицы LinuxOps (разделитель TAB): PM, R (где · значение · кто: lo / off / over / other), CUR, REPO;
    /// последняя строка «Итого: …» — для людей и превью. В окружении значения раскрываются ($PROXY_URL → адрес) чтением файла,
    /// без выполнения чужих скриптов.
    /// </summary>
    public static string Check => Lib + """
        # $1 файл, $2 подпись, $3 регулярка строки (awk), $4 кто, если не в блоке LinuxOps (other по умолчанию), $5 раскрывать переменные.
        rows_of() {
          [ -f "$1" ] || return 0
          awk -v where="$2" -v re="$3" -v def="${4:-other}" -v mb="$MARK_B" -v me="$MARK_E" -v expand="${5:-}" '
            BEGIN { q = sprintf("%c", 39) }
            function unq(s) {
              if (length(s) >= 2 && (substr(s, 1, 1) == "\"" || substr(s, 1, 1) == q)) s = substr(s, 2, length(s) - 2)
              return s
            }
            function expd(s,   out, name) {
              out = ""
              while (match(s, /\$\{?[A-Za-z_][A-Za-z0-9_]*\}?/)) {
                name = substr(s, RSTART, RLENGTH); gsub(/[${}]/, "", name)
                out = out substr(s, 1, RSTART - 1) ((name in v) ? v[name] : substr(s, RSTART, RLENGTH))
                s = substr(s, RSTART + RLENGTH)
              }
              return out s
            }
            function show(line, owner) { printf "R\t%s%s\t%s\t%s\n", where, sec, line, owner }
            # Присваивание в shell-файле: запомнить значение (раскрыв $VAR) и показать «имя=значение», если это прокси.
            function assign(line, owner, keep,   s, name, val) {
              s = line; sub(/^export[ \t]+/, "", s)
              name = s; sub(/=.*/, "", name)
              val = s; sub(/^[^=]*=/, "", val); val = expd(unq(val))
              if (keep) v[name] = val
              if (line ~ re) show(name "=" val, owner)
            }
            $0 == mb { b = 1; next }
            $0 == me { b = 0; next }
            /^\[/ { sec = " " $0 }
            /^(#|\/\/)LinuxOps-disabled: / {
              line = $0; sub(/^(#|\/\/)LinuxOps-disabled: /, "", line); sub(/^[ \t]+/, "", line)
              if (expand && line ~ /^(export[ \t]+)?[A-Za-z_][A-Za-z0-9_]*=/) assign(line, "off", 0)
              else if (line ~ re) show(line, "off")
              next
            }
            /^[ \t]*(#|\/\/)/ { next }
            {
              line = $0; sub(/^[ \t]+/, "", line)
              if (expand && line ~ /^(export[ \t]+)?[A-Za-z_][A-Za-z0-9_]*=/) assign(line, b ? "lo" : def, 1)
              else if (line ~ re) show(line, b ? "lo" : def)
            }
          ' "$1"
        }
        PM=$(pm)
        printf 'PM\t%s\n' "$PM"
        CUR=""; URL=""
        case "$PM" in
          apt)
            for f in $APT_FILE $(apt_files); do
              [ -f "$f" ] || continue
              o=other; [ "$f" = "$APT_FILE" ] && o=lo
              rows_of "$f" "apt · $f" 'Acquire::(http|https|ftp)::Proxy' "$o"
            done
            CUR=$(grep -rhsE '^[^/#]*Acquire::http::Proxy +"' /etc/apt/apt.conf /etc/apt/apt.conf.d/ 2>/dev/null | grep -v DIRECT | tail -n1 | sed -E 's/.*"([^"]*)".*/\1/')
            URL=$(grep -rhsE '^(deb |URIs:)' /etc/apt/sources.list /etc/apt/sources.list.d/ 2>/dev/null | grep -oE 'https?://[^ ]+' | head -n1) ;;
          yum)
            c=$(yum_conf)
            if [ -n "$c" ]; then rows_of "$c" "dnf · $c" '^[ \t]*proxy'; fi
            for f in $(repo_files); do rows_of "$f" "dnf · $f" '^[ \t]*proxy'; done
            CUR=$(awk '/^\[/{s=($0=="[main]")} s && /^[ \t]*proxy[ \t]*=/{sub(/^[ \t]*proxy[ \t]*=[ \t]*/,""); print; exit}' "${c:-/dev/null}")
            URL=$(grep -hsE '^(baseurl|mirrorlist|metalink)[ \t]*=' /etc/yum.repos.d/*.repo 2>/dev/null | grep -oE 'https?://[^ ]+' | head -n1) ;;
          zypper)
            o=other; [ -f "$SUSE_FILE.linuxops.bak" ] && o=lo
            rows_of "$SUSE_FILE" "zypper · $SUSE_FILE" '^(PROXY_ENABLED|HTTP_PROXY|HTTPS_PROXY|NO_PROXY)=' "$o"
            if grep -q '^PROXY_ENABLED="yes"' "$SUSE_FILE" 2>/dev/null; then CUR=$(sed -n 's/^HTTP_PROXY="\(.*\)"$/\1/p' "$SUSE_FILE" | head -n1); fi
            URL=$(grep -hs '^baseurl=' /etc/zypp/repos.d/*.repo 2>/dev/null | grep -oE 'https?://[^ ]+' | head -n1) ;;
        esac
        rows_of "$ENV_FILE" "$ENV_FILE" '_(proxy|PROXY)=' other 1
        # Чужие скрипты profile.d: если наш zz-… есть — они перекрыты (over).
        over=other; [ -f "$PROFILE_FILE" ] && over=over
        for f in /etc/profile.d/*.sh; do
          [ -f "$f" ] || continue
          if [ "$f" = "$PROFILE_FILE" ] || [ "$f" = "$OLD_PROFILE" ]; then rows_of "$f" "$f" '_(proxy|PROXY)=' lo 1
          else rows_of "$f" "$f" '_(proxy|PROXY)=' "$over" 1; fi
        done
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
