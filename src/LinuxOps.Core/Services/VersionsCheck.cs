using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LinuxOps.Services;

/// <summary>Строка таблицы «Версии ПО»: продукт на хосте или в контейнере.</summary>
public sealed class VersionItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private bool _isChecked;

    /// <summary>Отмечено к «Обновить отмеченное» (только если <see cref="CanUpdate"/>).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    /// <summary>Можно обновить из таблицы: пакет хоста, для которого репозиторий предлагает новую версию.</summary>
    public bool CanUpdate => Container.Length == 0 && FromPackage && Update.Length > 0;

    /// <summary>Пусто — сам хост, иначе имя контейнера.</summary>
    public string Container { get; init; } = "";
    public string Image { get; set; } = "";
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>Путь к исполняемому файлу, «env:NGINX_VERSION» (переменная образа) или «tag» (тег образа).</summary>
    public string Path { get; init; } = "";
    public string Package { get; init; } = "";
    public string PackageVersion { get; init; } = "";

    /// <summary>Доступная версия пакета из репозитория (только хост).</summary>
    public string Update { get; set; } = "";
    public bool SecurityUpdate { get; set; }

    /// <summary>bad — обновление безопасности или ветка без поддержки; warn — есть обновление; ok — актуально; none — нет данных.</summary>
    public string Severity { get; set; } = "none";
    public string StateText { get; set; } = "";
    /// <summary>Пояснение мелким шрифтом (поддержка ветки у разработчика и т. п.).</summary>
    public string Note { get; set; } = "";

    public string WhereText => Container.Length == 0 ? "хост" : Container;
    public string ProductText => VersionsCheck.ProductName(Product);

    public string SourceText => Path switch
    {
        "tag" => "тег образа",
        var p when p.StartsWith("env:", StringComparison.Ordinal) => $"переменная образа {p[4..]}",
        _ => Package.Length > 0 ? $"пакет {Package} {PackageVersion}".TrimEnd() : $"файл {Path}"
    };

    public bool FromPackage => Package.Length > 0;

    /// <summary>rpm: эпоха («(none)» — нет), исходный пакет (*.src.rpm), производитель; dpkg: исходный пакет.</summary>
    public string PackageEpoch { get; init; } = "";
    public string SourcePackage { get; init; } = "";
    public string Vendor { get; init; } = "";

    // ---------- сверка CVE (CveCheck) ----------

    /// <summary>now — закрыть сейчас, plan — в план, ok — срочных нет, none — не сверялось; пусто — сверку не запускали.</summary>
    public string CveLevel { get; private set; } = "";
    public List<CveHit> Cves { get; private set; } = new();
    /// <summary>Почему не сверялось или что не удалось.</summary>
    public string CveNote { get; private set; } = "";
    /// <summary>С какой базой сверялось.</summary>
    public string CveSource { get; set; } = "";

    public void SetCve(string level, List<CveHit> hits, string note)
    {
        CveLevel = level;
        Cves = hits;
        CveNote = note;
    }

    /// <summary>Плашка — только когда есть что закрыть сейчас.</summary>
    public string CveText => CveLevel == "now" ? $"{Cves.Count} CVE" : "";

    /// <summary>Под плашкой — самая опасная.</summary>
    public string CveTop => CveLevel == "now" && Cves.Count > 0 ? Cves[0].Id : "";

    /// <summary>По строке на CVE; не сверялось — почему.</summary>
    public string CveTooltip => CveLevel switch
    {
        "now" => string.Join("\n", Cves.Select(c => c.Line)),
        "none" when CveNote.Length > 0 => "CVE не сверялись: " + CveNote,
        _ => ""
    };
}

public sealed class ContainerInfo
{
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public string Runtime { get; init; } = "";
    /// <summary>exec — проверено изнутри; env — по переменным образа; tag — по тегу; none — не определено.</summary>
    public string Mode { get; init; } = "";
}

public sealed class VersionReport
{
    public string Host { get; set; } = "";
    public string PackageManager { get; set; } = "";
    /// <summary>Дистрибутив публикует пометки безопасности (apt — по репозиторию -security, dnf/yum — updateinfo).</summary>
    public bool SecurityInfo { get; set; }
    /// <summary>Обновления пакетов не проверены (нет сети до репозитория и т. п.).</summary>
    public string UpdateError { get; set; } = "";
    public List<VersionItem> Items { get; } = new();
    public List<ContainerInfo> Containers { get; } = new();
    public int StoppedContainers { get; set; }
    public List<string> ContainerErrors { get; } = new();

    public int BadCount => Items.Count(i => i.Severity == "bad" || i.CveLevel == "now");
    public int WarnCount => Items.Count(i => i.Severity == "warn");

    /// <summary>Дистрибутив по месту: "" — хост, иначе имя контейнера → «centos 9».</summary>
    public Dictionary<string, string> Os { get; } = new(StringComparer.Ordinal);

    public bool CveDone { get; set; }
    public string CveErrors { get; set; } = "";
    public DateTime? CveStale { get; set; }
    public int CveSeconds { get; set; }
    /// <summary>Сверку CVE отключили галкой.</summary>
    public bool CveOff { get; set; }

    public string CveSummary
    {
        get
        {
            if (CveOff || !CveDone) return "";
            // «Закрыть сейчас: nginx — 1, PHP — 4, nginx в web — 4»; php и php-fpm из одной сборки — одной записью
            var now = Items.Where(i => i.CveLevel == "now")
                .GroupBy(i => (i.Container, Cves: string.Join(",", i.Cves.Select(c => c.Id))))
                .Select(g => $"{string.Join("/", g.Select(i => i.ProductText).Distinct())}{(g.Key.Container.Length > 0 ? $" в {g.Key.Container}" : "")} — {g.First().Cves.Count}")
                .ToList();
            var text = now.Count > 0 ? "Закрыть сейчас: " + string.Join(", ", now) : "CVE: срочного нет";
            if (CveErrors.Length > 0 || CveStale is not null) text += " (не все базы ответили)";
            return text;
        }
    }

    /// <summary>Сколько разных CVE «закрыть сейчас» (на хосте и в каждом контейнере отдельно; PHP и PHP-FPM с общими CVE — один раз).</summary>
    public int CveNowCount => Items.Where(i => i.CveLevel == "now").SelectMany(i => i.Cves.Select(c => (i.Container, c.Id))).Distinct().Count();

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var host = Items.Where(i => i.Container.Length == 0).Select(i => $"{i.ProductText} {i.Version}").Distinct().ToList();
            parts.Add(host.Count > 0 ? string.Join(", ", host.Take(4)) + (host.Count > 4 ? $" и ещё {host.Count - 4}" : "") : "на хосте ничего из списка");
            if (Containers.Count > 0) parts.Add($"контейнеров {Containers.Count}");
            var nowProducts = Items.Count(i => i.CveLevel == "now");
            if (nowProducts > 0) parts.Add($"CVE закрыть сейчас: {CveNowCount} (продуктов: {nowProducts})");
            if (BadCount > 0) parts.Add($"внимание: {BadCount}");
            else if (WarnCount > 0) parts.Add($"обновлений: {WarnCount}");
            else parts.Add("замечаний нет");
            return string.Join(" · ", parts);
        }
    }

    public string ContainersText
    {
        get
        {
            var sb = new StringBuilder();
            if (Containers.Count == 0 && ContainerErrors.Count == 0)
                sb.Append("Контейнеров не найдено (docker / podman).");
            else if (Containers.Count > 0)
                sb.Append($"Контейнеры: {Containers.Count} работающих ({string.Join(", ", Containers.Select(c => c.Runtime).Distinct())})");
            if (StoppedContainers > 0) sb.Append($", остановленных {StoppedContainers} — не проверялись");
            var noExec = Containers.Where(c => c.Mode != "exec").Select(c => c.Name).ToList();
            if (noExec.Count > 0) sb.Append($". Без оболочки внутри, версии по образу: {string.Join(", ", noExec)}");
            foreach (var e in ContainerErrors) sb.Append($"\n{e}");
            return sb.ToString().Trim();
        }
    }

    /// <summary>Как получены списки обновлений: net (хост сам), tunnel (через ПК оператора), cache (кэш хоста), none.</summary>
    public string UpdatePath { get; set; } = "";
    public DateTime? CacheTime { get; set; }
    /// <summary>Почему не ответили репозитории (при откате на кэш или неудаче).</summary>
    public string NetError { get; set; } = "";
    /// <summary>Туннель через ПК просили, но он не поднялся — почему (заполняет PlanRunner).</summary>
    public string TunnelNote { get; set; } = "";
    /// <summary>Сколько соединений прошло через ПК (-1 — туннель не поднимался) и куда.</summary>
    public int TunnelConnections { get; set; } = -1;
    public string TunnelLog { get; set; } = "";

    public string UpdatesText
    {
        get
        {
            var tunnel = TunnelNote.Length > 0 ? $"Туннель через {RunPlace.Here} не поднят: {TunnelNote}. " : "";
            if (UpdatePath == "tunnel" && TunnelConnections == 0)
                return $"Через {RunPlace.Here} соединений не было — менеджер пакетов ответил по своим спискам ({PackageManagerText}{Lists}).";
            if (PackageManager == "none") return tunnel + "Менеджер пакетов не найден — обновления не проверялись.";
            if (UpdateError.Length > 0)
                return tunnel + (NetError.Length > 0
                    ? $"Обновления пакетов не проверены: репозитории не ответили ({NetError}), кэша на хосте нет."
                    : $"Обновления пакетов не проверены: {UpdateError}");
            var sec = SecurityInfo ? ", с пометками безопасности." : "; дистрибутив не публикует пометки безопасности, показано любое обновление.";
            return tunnel + UpdatePath switch
            {
                "cache" => $"Репозитории не ответили{(NetError.Length > 0 ? $" ({NetError})" : "")} — обновления по кэшу хоста"
                           + (CacheTime is { } t ? $" от {t:dd.MM.yyyy HH:mm}" : "") + $" ({PackageManagerText}){sec}",
                "tunnel" => $"Обновления пакетов — по репозиториям через {RunPlace.Here} (туннель SSH, {PackageManagerText}{Lists}){sec}",
                _ => $"Обновления пакетов — по репозиториям ({PackageManagerText}{Lists}){sec}"
            };
        }
    }

    private string Lists => CacheTime is { } t ? $", списки от {t:dd.MM.yyyy HH:mm}" : "";

    private string PackageManagerText =>PackageManager == "yum" ? "dnf / yum" : PackageManager;
}

/// <summary>
/// «Версии ПО»: nginx, Apache, PHP, OpenSSL, OpenSSH, MySQL/MariaDB, PostgreSQL, Redis, Node.js, Java, Python — на хосте
/// и в работающих контейнерах docker / podman. Пакеты хоста сверяются с репозиторием (дистрибутивы переносят исправления
/// в старые номера версий), остальное — со сроками поддержки веток у разработчиков (<see cref="Eol"/>).
/// </summary>
public static class VersionsCheck
{
    public const string ActionId = "versions-check";
    /// <summary>Параметр-галка: списки обновлений через ПК оператора (туннель SSH). По умолчанию «нет».</summary>
    public const string ViaPcParam = "UPD_VIA_PC";

    public static string ProductName(string p) => p switch
    {
        "nginx" => "nginx",
        "apache" => "Apache httpd",
        "php" => "PHP",
        "php-fpm" => "PHP-FPM",
        "openssl" => "OpenSSL",
        "openssh" => "OpenSSH",
        "mysql" => "MySQL",
        "mariadb" => "MariaDB",
        "postgresql" => "PostgreSQL",
        "redis" => "Redis",
        "node" => "Node.js",
        "java" => "Java",
        "python" => "Python",
        _ => p
    };

    /// <summary>
    /// Скрипт. Тело — в функции main с stdin из /dev/null: скрипт сам приходит через stdin (bash -s), и команда,
    /// читающая stdin, иначе съела бы его остаток. Проба версий (PROBE) — POSIX sh: та же выполняется в контейнерах
    /// (там часто busybox). Ничего не устанавливает и не меняет; dnf/apt обновляют только списки пакетов.
    /// Строки вывода (TAB): PM, V (версия), U (обновление пакета), SEC, UPATH (откуда списки), UERR, CT (контейнер), CTSTOP, CTERR.
    /// Окружение: UPD_BUDGET — секунд на сеть (60), UPD_PROXY — прокси только для этой проверки (туннель через ПК).
    /// </summary>
    public const string Script = """
        main() {
        PROBE=$(cat <<'PROBE_EOF'
        PATH="$PATH:/usr/sbin:/usr/local/sbin:/sbin:/usr/local/bin:/usr/libexec"
        seen=" "
        own() {
          f=$(readlink -f "$1" 2>/dev/null || echo "$1")
          if command -v rpm >/dev/null 2>&1; then
            r=$(rpm -qf --qf '%{NAME}\t%{VERSION}-%{RELEASE}\t%{EPOCH}\t%{SOURCERPM}\t%{VENDOR}\n' "$f" 2>/dev/null | head -n1)
            case "$r" in *"not owned"*|"") ;; *) printf '%s' "$r"; return ;; esac
          fi
          if command -v dpkg-query >/dev/null 2>&1; then
            p=$(dpkg -S "$f" 2>/dev/null | head -n1 | cut -d: -f1)
            if [ -z "$p" ] && [ "$f" != "$1" ]; then p=$(dpkg -S "$1" 2>/dev/null | head -n1 | cut -d: -f1); fi
            if [ -n "$p" ]; then printf '%s\t%s\t\t%s' "$p" "$(dpkg-query -W -f='${Version}' "$p" 2>/dev/null)" "$(dpkg-query -W -f='${source:Package}' "$p" 2>/dev/null)"; return; fi
          fi
          if command -v apk >/dev/null 2>&1; then
            o=$(apk info -W "$f" 2>/dev/null | sed -n 's/.* is owned by //p' | head -n1)
            if [ -n "$o" ]; then printf '%s\t' "$o"; return; fi
          fi
          printf '\t'
        }
        emit() {
          [ -n "$2" ] || return 0
          r=$(readlink -f "$3" 2>/dev/null || echo "$3")
          case "$seen" in *" $1:$r "*) return 0 ;; esac
          seen="$seen$1:$r "
          printf 'V\t\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$(own "$3")"
        }
        b() { command -v "$1" 2>/dev/null; }
        [ -r /etc/os-release ] && ( . /etc/os-release 2>/dev/null; printf 'O\t\t%s %s\n' "$ID" "$VERSION_ID" )
        if f=$(b nginx); then emit nginx "$("$f" -v 2>&1 | sed -n 's#.*nginx/\([0-9][0-9.]*\).*#\1#p' | head -n1)" "$f"; fi
        for n in httpd apache2; do
          if f=$(b $n); then emit apache "$("$f" -v 2>/dev/null | sed -n 's#.*Apache/\([0-9][0-9.]*\).*#\1#p' | head -n1)" "$f"; fi
        done
        for f in $(b php) /usr/bin/php[0-9]* /usr/local/bin/php /opt/remi/php*/root/usr/bin/php \
                 $(b php-fpm) /usr/sbin/php-fpm* /usr/local/sbin/php-fpm /opt/remi/php*/root/usr/sbin/php-fpm; do
          [ -x "$f" ] || continue
          case "${f##*/}" in php-fpm*) n=php-fpm ;; *) n=php ;; esac
          emit $n "$("$f" -v 2>/dev/null | sed -n '1s/^PHP \([0-9][0-9.]*\).*/\1/p')" "$f"
        done
        if f=$(b openssl); then emit openssl "$("$f" version 2>/dev/null | sed -n 's/^OpenSSL \([0-9][0-9a-z.]*\).*/\1/p')" "$f"; fi
        if f=$(b sshd) || f=$(b ssh); then
          s=$(b ssh); [ -n "$s" ] && emit openssh "$("$s" -V 2>&1 | sed -n 's/^OpenSSH_\([0-9][0-9.p]*\).*/\1/p' | head -n1)" "$f"
        fi
        for f in $(b mariadbd) $(b mysqld) /usr/libexec/mariadbd /usr/libexec/mysqld; do
          [ -x "$f" ] || continue
          o=$("$f" --version 2>/dev/null | head -n1)
          case "$o" in *MariaDB*) n=mariadb ;; *) n=mysql ;; esac
          emit $n "$(printf '%s' "$o" | sed -n 's/.*Ver \([0-9][0-9.]*\).*/\1/p')" "$f"
        done
        for f in $(b postgres) /usr/pgsql-*/bin/postgres /usr/lib/postgresql/*/bin/postgres; do
          [ -x "$f" ] || continue
          emit postgresql "$("$f" --version 2>/dev/null | sed -n 's/.*PostgreSQL) \([0-9][0-9.]*\).*/\1/p')" "$f"
        done
        if f=$(b redis-server); then emit redis "$("$f" --version 2>/dev/null | sed -n 's/.*v=\([0-9][0-9.]*\).*/\1/p')" "$f"; fi
        if f=$(b node); then emit node "$("$f" --version 2>/dev/null | sed -n 's/^v\([0-9][0-9.]*\).*/\1/p')" "$f"; fi
        if f=$(b java); then emit java "$("$f" -version 2>&1 | sed -n '1s/.*version "\([^"]*\)".*/\1/p')" "$f"; fi
        for f in $(b python3) $(b python) $(b python2); do
          emit python "$("$f" --version 2>&1 | sed -n 's/^Python \([0-9][0-9.]*\).*/\1/p')" "$f"
        done
        exit 0
        PROBE_EOF
        )

        # --- хост ---
        PM=none
        if command -v dnf >/dev/null 2>&1; then PM=dnf
        elif command -v yum >/dev/null 2>&1; then PM=yum
        elif command -v apt-get >/dev/null 2>&1; then PM=apt
        elif command -v zypper >/dev/null 2>&1; then PM=zypper
        fi
        printf 'PM\t%s\n' "$PM"
        HOSTV=$(sh -c "$PROBE" 2>/dev/null)
        [ -n "$HOSTV" ] && printf '%s\n' "$HOSTV"
        PKGS=$(printf '%s\n' "$HOSTV" | awk -F'\t' '$1=="V" && $6!="" {print $6}' | sort -u | tr '\n' ' ')

        # --- обновления пакетов хоста: только списки, ничего не ставится ---
        # Сеть — не дольше UPD_BUDGET с (недоступный прокси иначе держит dnf минутами); не вышло — кэш хоста.
        # UPD_PROXY — прокси на время этой команды (туннель через ПК); настройки хоста не меняются.
        # UPATH: путь (net | tunnel | cache | none), время кэша (epoch), причина, если сеть не ответила.
        if [ -n "$PKGS" ] && [ "$PM" != none ]; then
          UB=${UPD_BUDGET:-60}; T=""; command -v timeout >/dev/null 2>&1 && T="timeout -k 5 $UB"
          TC=""; command -v timeout >/dev/null 2>&1 && TC="timeout -k 5 60"
          via=net; [ -n "$UPD_PROXY" ] && via=tunnel
          why=""
          last() { printf '%s\n' "$1" | grep -v '^[[:space:]]*$' | tail -n1; }
          newest() { find "$@" -name "$NEWEST" -printf '%T@\n' 2>/dev/null | sort -n | tail -n1 | cut -d. -f1; }
          case "$PM" in
            dnf|yum)
              set -- -q --setopt=timeout=10 --setopt=retries=1
              # через ПК — списки заново (иначе dnf ответит по неустаревшему кэшу, мимо туннеля)
              [ -n "$UPD_PROXY" ] && set -- "$@" --setopt=proxy="$UPD_PROXY" --refresh
              NEWEST=repomd.xml
              out=$($T $PM "$@" check-update $PKGS 2>&1); rc=$?
              if [ $rc -ne 0 ] && [ $rc -ne 100 ]; then
                if [ $rc -ge 124 ]; then why="нет ответа за $UB с"; else why=$(last "$out"); fi
                out=$($TC $PM -q -C check-update $PKGS 2>&1); rc=$?
                if [ $rc -eq 0 ] || [ $rc -eq 100 ]; then
                  printf 'UPATH\tcache\t%s\t%s\n' "$(newest /var/cache/dnf /var/cache/yum /var/cache/libdnf5)" "$why"
                fi
              else
                printf 'UPATH\t%s\t%s\t\n' "$via" "$(newest /var/cache/dnf /var/cache/yum /var/cache/libdnf5)"
              fi
              if [ $rc -ne 0 ] && [ $rc -ne 100 ]; then
                printf 'UPATH\tnone\t\t%s\n' "$why"
                printf 'UERR\t%s\n' "$(last "$out")"
              else
                printf '%s\n' "$out" | awk 'NF==3 && $1 ~ /\./ {n=$1; sub(/\.[^.]+$/,"",n); printf "U\t%s\t%s\n", n, $2}'
                # метаданные уже получены (или есть только кэш) — сеть больше не трогаем
                sec=$($TC $PM -q -C updateinfo list --updates --security 2>/dev/null | awk 'NF>=3 {print $3}')
                if [ -n "$($TC $PM -q -C updateinfo list --all --security 2>/dev/null | head -n1)" ]; then printf 'SEC\t1\n'; else printf 'SEC\t0\n'; fi
                printf '%s\n' "$sec" | sed -E 's/\.[^.]+$//; s/-[^-]+-[^-]+$//' | sort -u | while read -r n; do
                  [ -n "$n" ] && printf 'S\t%s\n' "$n"
                done
              fi ;;
            apt)
              set -- -qq -o Acquire::http::Timeout=10 -o Acquire::https::Timeout=10 -o Acquire::Retries=0
              [ -n "$UPD_PROXY" ] && set -- "$@" -o Acquire::http::Proxy="$UPD_PROXY" -o Acquire::https::Proxy="$UPD_PROXY"
              NEWEST='*_Packages'
              out=$(DEBIAN_FRONTEND=noninteractive $T apt-get "$@" update 2>&1); rc=$?
              if [ $rc -eq 0 ]; then
                printf 'UPATH\t%s\t%s\t\n' "$via" "$(newest /var/lib/apt/lists)"
              else
                if [ $rc -ge 124 ]; then why="нет ответа за $UB с"; else why=$(last "$out"); fi
                if ls /var/lib/apt/lists/*_Packages >/dev/null 2>&1; then
                  printf 'UPATH\tcache\t%s\t%s\n' "$(newest /var/lib/apt/lists)" "$why"
                else
                  printf 'UPATH\tnone\t\t%s\n' "$why"; printf 'UERR\t%s\n' "$why"
                fi
              fi
              printf 'SEC\t1\n'
              apt list --upgradable 2>/dev/null | awk -F'[/ ]' -v pk=" $PKGS " '$1 != "" && index(pk, " " $1 " ") {
                printf "U\t%s\t%s\n", $1, $3; if ($2 ~ /-security/) printf "S\t%s\n", $1 }' ;;
            zypper)
              if [ -n "$UPD_PROXY" ]; then
                out=$(http_proxy="$UPD_PROXY" https_proxy="$UPD_PROXY" HTTP_PROXY="$UPD_PROXY" HTTPS_PROXY="$UPD_PROXY" $T zypper -n -q lu 2>&1); rc=$?
              else
                out=$($T zypper -n -q lu 2>&1); rc=$?
              fi
              NEWEST=repomd.xml
              if [ $rc -eq 0 ]; then
                printf 'UPATH\t%s\t%s\t\n' "$via" "$(newest /var/cache/zypp/raw)"
              else
                if [ $rc -ge 124 ]; then why="нет ответа за $UB с"; else why=$(last "$out"); fi
                out=$($TC zypper -n -q --no-refresh lu 2>&1); rc=$?
                if [ $rc -eq 0 ]; then
                  printf 'UPATH\tcache\t%s\t%s\n' "$(newest /var/cache/zypp/raw)" "$why"
                else
                  printf 'UPATH\tnone\t\t%s\n' "$why"; printf 'UERR\t%s\n' "$(last "$out")"
                fi
              fi
              printf 'SEC\t0\n'
              printf '%s\n' "$out" | awk -F'|' -v pk=" $PKGS " '{gsub(/ /,"",$3); gsub(/ /,"",$5); if ($3 != "" && index(pk, " " $3 " ")) printf "U\t%s\t%s\n", $3, $5}' ;;
          esac
        fi

        # --- контейнеры: docker и podman, только работающие; внутрь — та же проба (только чтение) ---
        TO=""; command -v timeout >/dev/null 2>&1 && TO="timeout 30"
        for rt in docker podman; do
          command -v $rt >/dev/null 2>&1 || continue
          # docker может быть обёрткой podman — не проверять дважды
          if [ $rt = docker ] && docker --version 2>/dev/null | grep -qi podman; then continue; fi
          if ! list=$($TO $rt ps --format '{{.ID}}|{{.Names}}|{{.Image}}' 2>&1); then
            printf 'CTERR\t%s\t%s\n' "$rt" "$(printf '%s\n' "$list" | head -n1)"; continue
          fi
          stopped=$($rt ps -aq --filter status=exited 2>/dev/null | wc -l)
          [ "$stopped" -gt 0 ] 2>/dev/null && printf 'CTSTOP\t%s\n' "$stopped"
          printf '%s\n' "$list" | while IFS='|' read -r id name image; do
            [ -n "$id" ] || continue
            out=$($TO $rt exec "$id" sh -c "$PROBE" 2>/dev/null </dev/null); rc=$?
            if [ $rc -eq 0 ]; then
              printf 'CT\t%s\t%s\t%s\texec\n' "$name" "$image" "$rt"
              printf '%s\n' "$out" | awk -F'\t' -v OFS='\t' -v n="$name" '$1=="V" || $1=="O" {$2=n; print}'
              continue
            fi
            # нет оболочки (distroless, scratch) — версии из переменных образа, затем из тега
            envs=$($rt inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$id" 2>/dev/null </dev/null)
            vl=$(printf '%s\n' "$envs" | awk -F= -v OFS='\t' -v n="$name" '
              function v(s) { sub(/^[0-9]+:/, "", s); sub(/[-+~].*$/, "", s); return s }
              $1=="NGINX_VERSION" {print "V", n, "nginx", v($2), "env:" $1}
              $1=="HTTPD_VERSION" {print "V", n, "apache", v($2), "env:" $1}
              $1=="PHP_VERSION" {print "V", n, "php", v($2), "env:" $1}
              $1=="NODE_VERSION" {print "V", n, "node", v($2), "env:" $1}
              $1=="REDIS_VERSION" {print "V", n, "redis", v($2), "env:" $1}
              $1=="PG_VERSION" {print "V", n, "postgresql", v($2), "env:" $1}
              $1=="MARIADB_VERSION" {print "V", n, "mariadb", v($2), "env:" $1}
              $1=="MYSQL_VERSION" {print "V", n, "mysql", v($2), "env:" $1}
              $1=="JAVA_VERSION" {print "V", n, "java", v($2), "env:" $1}
              $1=="PYTHON_VERSION" {print "V", n, "python", v($2), "env:" $1}')
            if [ -n "$vl" ]; then
              printf 'CT\t%s\t%s\t%s\tenv\n' "$name" "$image" "$rt"; printf '%s\n' "$vl"; continue
            fi
            # реестр с портом (host:5000/nginx:1.25) — имя и тег только из последней части пути
            last=${image%%@*}; last=${last##*/}; repo=${last%%:*}; tag=${last#"$repo"}; tag=${tag#:}
            case "$repo" in
              nginx|httpd|php|node|redis|postgres|mariadb|mysql|python)
                case "$tag" in [0-9]*)
                  p=$repo; [ $p = httpd ] && p=apache; [ $p = postgres ] && p=postgresql
                  printf 'CT\t%s\t%s\t%s\ttag\n' "$name" "$image" "$rt"
                  printf 'V\t%s\t%s\t%s\ttag\n' "$name" "$p" "${tag%%-*}"; continue ;;
                esac ;;
            esac
            printf 'CT\t%s\t%s\t%s\tnone\n' "$name" "$image" "$rt"
          done
        done
        }
        main </dev/null
        """;

    public static VersionReport? Parse(string stdout)
    {
        VersionReport? r = null;
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var security = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in stdout.Replace("\r", "").Split('\n'))
        {
            var p = raw.Split('\t');
            switch (p[0])
            {
                case "PM" when p.Length >= 2:
                    r = new VersionReport { PackageManager = p[1] };
                    break;
                case "V" when r is not null && p.Length >= 4 && p[3].Length > 0:
                    r.Items.Add(new VersionItem
                    {
                        Container = p[1],
                        Product = p[2],
                        Version = p[3],
                        Path = p.Length > 4 ? p[4] : "",
                        Package = p.Length > 5 ? p[5] : "",
                        PackageVersion = p.Length > 6 ? p[6] : "",
                        PackageEpoch = p.Length > 7 ? p[7] : "",
                        SourcePackage = p.Length > 8 ? p[8] : "",
                        Vendor = p.Length > 9 ? p[9] : ""
                    });
                    break;
                case "O" when r is not null && p.Length >= 3:
                    r.Os[p[1]] = p[2].Trim();
                    break;
                case "U" when p.Length >= 3 && p[1].Length > 0:
                    updates[p[1]] = p[2];
                    break;
                case "S" when p.Length >= 2:
                    security.Add(p[1]);
                    break;
                case "SEC" when r is not null && p.Length >= 2:
                    r.SecurityInfo = p[1] == "1";
                    break;
                case "UPATH" when r is not null && p.Length >= 2:
                    r.UpdatePath = p[1];
                    if (p.Length > 2 && long.TryParse(p[2].Trim(), out var epoch) && epoch > 0)
                        r.CacheTime = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
                    if (p.Length > 3 && p[3].Trim().Length > 0) r.NetError = p[3].Trim();
                    break;
                case "UERR" when r is not null && p.Length >= 2:
                    r.UpdateError = p[1].Length > 0 ? p[1] : "менеджер пакетов вернул ошибку";
                    break;
                case "CT" when r is not null && p.Length >= 5:
                    r.Containers.Add(new ContainerInfo { Name = p[1], Image = p[2], Runtime = p[3], Mode = p[4] });
                    break;
                case "CTSTOP" when r is not null && p.Length >= 2 && int.TryParse(p[1].Trim(), out var n):
                    r.StoppedContainers += n;
                    break;
                case "CTERR" when r is not null && p.Length >= 3:
                    r.ContainerErrors.Add($"{p[1]}: {p[2]}");
                    break;
            }
        }

        if (r is null) return null;
        foreach (var item in r.Items)
        {
            item.Image = r.Containers.FirstOrDefault(c => c.Name == item.Container)?.Image ?? "";
            if (item.Container.Length == 0 && item.Package.Length > 0 && updates.TryGetValue(item.Package, out var upd))
            {
                item.Update = upd;
                item.SecurityUpdate = security.Contains(item.Package);
            }
            Assess(r, item);
        }

        return r;
    }

    /// <summary>
    /// Пакет хоста — по репозиторию: обновление безопасности (bad), обновление (warn), актуально (ok); поддержка ветки
    /// у разработчика — только пояснением (исправления приходят от дистрибутива). Остальное (контейнеры, собранное
    /// вручную) — по сроку поддержки ветки.
    /// </summary>
    private static void Assess(VersionReport r, VersionItem i)
    {
        var eol = Eol.Lookup(i.Product, i.Version);
        if (i.Container.Length == 0 && i.FromPackage && r.PackageManager != "none")
        {
            if (r.UpdateError.Length > 0)
            {
                i.Severity = "none";
                i.StateText = "обновления не проверены";
            }
            else if (i.Update.Length > 0)
            {
                i.Severity = i.SecurityUpdate ? "bad" : "warn";
                i.StateText = (i.SecurityUpdate ? "обновление безопасности " : "есть обновление ") + i.Update;
            }
            else
            {
                i.Severity = "ok";
                i.StateText = "актуально в репозитории";
            }
            if (eol.Ended) i.Note = $"ветка {eol.Branch} у разработчика закрыта {eol.DateText}; исправления — от дистрибутива";
            return;
        }

        if (eol.Ended)
        {
            i.Severity = "bad";
            i.StateText = $"ветка {eol.Branch} без поддержки с {eol.DateText}";
        }
        else if (eol.Known)
        {
            i.Severity = "ok";
            i.StateText = eol.Date is null ? $"ветка {eol.Branch} поддерживается" : $"ветка {eol.Branch} поддерживается до {eol.DateText}";
        }
        else
        {
            i.Severity = "none";
            i.StateText = "нет данных о поддержке ветки";
        }
        i.Note = i.Container.Length > 0
            ? "в контейнере: обновляется пересборкой образа"
            : "не из пакета: обновляется вручную";
    }

    public static string Format(string host, VersionReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"host: {host}");
        sb.AppendLine(r.UpdatesText);
        if (r.TunnelLog.Length > 0) sb.AppendLine(r.TunnelLog);
        if (r.CveSummary.Length > 0) sb.AppendLine(r.CveSummary);
        sb.AppendLine();
        foreach (var g in r.Items.GroupBy(i => i.Container))
        {
            var ct = r.Containers.FirstOrDefault(c => c.Name == g.Key);
            sb.AppendLine(g.Key.Length == 0 ? "Хост:" : $"Контейнер {g.Key} ({ct?.Image}, {ct?.Runtime}):");
            foreach (var i in g)
            {
                sb.AppendLine($"  {i.ProductText} {i.Version} — {i.StateText}");
                sb.AppendLine($"      {i.SourceText}" + (i.Note.Length > 0 ? $"; {i.Note}" : ""));
                if (i.CveLevel == "now")
                    foreach (var c in i.Cves) sb.AppendLine("      ! " + c.Line);
            }
            sb.AppendLine();
        }
        if (r.Items.Count == 0) sb.AppendLine("Ничего из списка не найдено.").AppendLine();
        sb.AppendLine(r.ContainersText);
        return sb.ToString();
    }
}

/// <summary>
/// Сроки поддержки веток у разработчиков. Встроенные — на момент выпуска программы; поправить и дополнить можно
/// файлом LinuxOps.eol.json рядом с exe: {"php": {"8.5": "2029-12-31"}, "nginx": {"1.30": null}} — null = поддерживается,
/// дата — «гггг-мм-дд» или «гггг-мм».
/// </summary>
public static class Eol
{
    public readonly record struct Info(string Branch, bool Known, DateTime? Date)
    {
        public bool Ended => Known && Date is { } d && d < DateTime.Today;
        public string DateText => Date is not { } d ? ""
            : d == DateTime.MinValue ? "давно"
            : _monthOnly.Contains(d) ? d.ToString("MM.yyyy")
            : d.ToString("dd.MM.yyyy");
    }

    private static readonly HashSet<DateTime> _monthOnly = new();

    private static readonly Dictionary<string, Dictionary<string, string?>> Builtin = new()
    {
        ["php"] = new()
        {
            ["5.6"] = "2018-12-31", ["7.0"] = "2019-01-10", ["7.1"] = "2019-12-01", ["7.2"] = "2020-11-30", ["7.3"] = "2021-12-06",
            ["7.4"] = "2022-11-28", ["8.0"] = "2023-11-26", ["8.1"] = "2025-12-31", ["8.2"] = "2026-12-31", ["8.3"] = "2027-12-31",
            ["8.4"] = "2028-12-31", ["8.5"] = "2029-12-31"
        },
        // nginx: поддерживаются только текущие stable и mainline; ветка закрывается с выходом следующей stable.
        ["nginx"] = new()
        {
            ["1.20"] = "2022-05-24", ["1.21"] = "2022-05-24", ["1.22"] = "2023-04-11", ["1.23"] = "2023-04-11",
            ["1.24"] = "2024-04-23", ["1.25"] = "2024-04-23", ["1.26"] = "2025-04-23", ["1.27"] = "2025-04-23",
            ["1.28"] = "2026-04", ["1.29"] = "2026-04", ["1.30"] = null, ["1.31"] = null
        },
        ["apache"] = new() { ["2.2"] = "2017-07-11", ["2.4"] = null },
        ["openssl"] = new()
        {
            ["1.0"] = "2019-12-31", ["1.1"] = "2023-09-11", ["3.0"] = "2026-09-07", ["3.1"] = "2025-03-14", ["3.2"] = "2025-11-23",
            ["3.3"] = "2026-04-09", ["3.4"] = "2026-10-22", ["3.5"] = "2030-04-08", ["3.6"] = "2026-11-01"
        },
        ["mysql"] = new() { ["5.6"] = "2021-02-01", ["5.7"] = "2023-10-31", ["8.0"] = "2026-04-30", ["8.4"] = "2032-04-30" },
        ["mariadb"] = new()
        {
            ["10.3"] = "2023-05-25", ["10.4"] = "2024-06-18", ["10.5"] = "2025-06-24", ["10.6"] = "2026-07-06",
            ["10.11"] = "2028-02-16", ["11.0"] = "2024-06-06", ["11.1"] = "2024-08-21", ["11.2"] = "2024-11-21",
            ["11.3"] = "2025-05-29", ["11.4"] = "2029-05-29", ["11.8"] = "2028-06-04"
        },
        ["postgresql"] = new()
        {
            ["9.6"] = "2021-11-11", ["10"] = "2022-11-10", ["11"] = "2023-11-09", ["12"] = "2024-11-21", ["13"] = "2025-11-13",
            ["14"] = "2026-11-12", ["15"] = "2027-11-11", ["16"] = "2028-11-09", ["17"] = "2029-11-08", ["18"] = "2030-11-14"
        },
        ["node"] = new()
        {
            ["14"] = "2023-04-30", ["16"] = "2023-09-11", ["18"] = "2025-04-30", ["19"] = "2023-06-01", ["20"] = "2026-04-30",
            ["21"] = "2024-06-01", ["22"] = "2027-04-30", ["23"] = "2025-06-01", ["24"] = "2028-04-30"
        },
        ["python"] = new()
        {
            ["2.7"] = "2020-01-01", ["3.6"] = "2021-12-23", ["3.7"] = "2023-06-27", ["3.8"] = "2024-10-07", ["3.9"] = "2025-10-31",
            ["3.10"] = "2026-10", ["3.11"] = "2027-10", ["3.12"] = "2028-10", ["3.13"] = "2029-10", ["3.14"] = "2030-10"
        }
    };

    private static Dictionary<string, Dictionary<string, string?>>? _table;

    public static string OverridePath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(DefaultsStore.DefaultsPath) ?? AppContext.BaseDirectory, "LinuxOps.eol.json");

    private static Dictionary<string, Dictionary<string, string?>> Table()
    {
        if (_table is not null) return _table;
        var t = Builtin.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string?>(kv.Value));
        try
        {
            if (File.Exists(OverridePath)
                && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(File.ReadAllText(OverridePath)) is { } extra)
            {
                foreach (var (product, branches) in extra)
                {
                    if (!t.TryGetValue(product, out var dst)) t[product] = dst = new();
                    foreach (var (b, d) in branches) dst[b] = d;
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Файл сроков поддержки {OverridePath} не прочитан — используются встроенные", ex);
        }
        return _table = t;
    }

    /// <summary>Ветка: PHP/nginx/OpenSSL/MySQL/MariaDB/Apache — «мажор.минор»; PostgreSQL ≥ 10 и Node.js — мажор.</summary>
    public static string BranchOf(string product, string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return version;
        var major = parts[0];
        if (product is "node" || (product is "postgresql" && int.TryParse(major, out var pg) && pg >= 10)) return major;
        return parts.Length > 1 ? $"{major}.{new string(parts[1].TakeWhile(char.IsDigit).ToArray())}" : major;
    }

    public static Info Lookup(string product, string version)
    {
        if (product == "php-fpm") product = "php"; // одна и та же ветка PHP
        var branch = BranchOf(product, version);
        if (!Table().TryGetValue(product, out var branches) || branches.Count == 0) return new Info(branch, false, null);
        if (branches.TryGetValue(branch, out var exact)) return new Info(branch, true, ParseDate(exact));
        // Старее всех известных веток — давно без поддержки (дата самой старой известной).
        if (System.Version.TryParse(Norm(branch), out var bv))
        {
            var known = branches.Keys.Select(k => (k, v: System.Version.TryParse(Norm(k), out var x) ? x : null))
                .Where(x => x.v is not null).OrderBy(x => x.v).ToList();
            if (known.Count > 0 && bv < known[0].v) return new Info(branch, true, ParseDate(branches[known[0].k]) ?? DateTime.MinValue);
        }
        return new Info(branch, false, null);
    }

    private static string Norm(string b) => b.Contains('.') ? b : b + ".0";

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParseExact(s, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
        {
            // «гггг-мм» — точный день неизвестен: считаем с начала месяца, показываем месяц
            lock (_monthOnly) _monthOnly.Add(m);
            return m;
        }
        return null;
    }
}
