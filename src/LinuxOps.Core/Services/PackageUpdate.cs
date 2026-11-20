using System.Text;
using LinuxOps.Models;

namespace LinuxOps.Services;

/// <summary>Пакет в предпросмотре или итоге: раздел (обновление, установка, удаление…), имя, версия.</summary>
public sealed record PackageChange(string Section, string Name, string Version)
{
    public string SectionText => Section switch
    {
        "Upgrading" or "Upgrade" => "обновление",
        "Installing" or "Install" => "установка",
        "Installing dependencies" or "Installing weak dependencies" => "зависимость",
        "Removing" or "Removing dependent packages" or "Remove" => "удаление",
        "Downgrading" or "Downgrade" => "откат версии",
        "Reinstalling" => "переустановка",
        _ => Section
    };

    public bool IsRemoval => SectionText == "удаление";
}

/// <summary>Разбор «Обновить отмеченное»: предпросмотр (что поставится) или итог (версии после, службы на перезапуск).</summary>
public sealed class PackageUpdateReport
{
    public string Host { get; set; } = "";
    public string PackageManager { get; set; } = "";
    public bool Preview { get; set; }
    /// <summary>Пакеты к обновлению: отмеченные и пакеты из той же сборки (php-common к php-fpm, openssl-libs к openssl).</summary>
    public List<string> Packages { get; } = new();
    public List<PackageChange> Changes { get; } = new();
    public bool NothingToDo { get; set; }
    public int ExitCode { get; set; }
    public string Error { get; set; } = "";
    /// <summary>Версии после установки.</summary>
    public List<(string Name, string Version)> Installed { get; } = new();
    /// <summary>Службы, которые держат старые (удалённые) файлы — им нужен перезапуск.</summary>
    public List<string> RestartServices { get; } = new();
    public string TunnelNote { get; set; } = "";
    public string TunnelLog { get; set; } = "";

    public int RemovalCount => Changes.Count(c => c.IsRemoval);

    public string Summary
    {
        get
        {
            if (Error.Length > 0) return Error;
            if (NothingToDo) return "обновлять нечего — пакеты уже свежие";
            if (Preview)
            {
                var n = Changes.Count(c => !c.IsRemoval);
                var s = $"поставится пакетов: {n}";
                if (RemovalCount > 0) s += $", удалится: {RemovalCount}";
                return s;
            }
            var t = ExitCode == 0 ? $"обновлено пакетов: {Installed.Count}" : $"ошибка установки (код {ExitCode})";
            if (RestartServices.Count > 0) t += $" · перезапуск нужен: {string.Join(", ", RestartServices.Select(ShortUnit))}";
            return t;
        }
    }

    public static string ShortUnit(string unit) => unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^8] : unit;

    public string Format()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"host: {Host}");
        sb.AppendLine((Preview ? "Предпросмотр: " : "Итог: ") + Summary);
        sb.AppendLine($"Пакеты: {string.Join(" ", Packages)}");
        if (Changes.Count > 0)
        {
            sb.AppendLine();
            foreach (var g in Changes.GroupBy(c => c.SectionText))
            {
                sb.AppendLine($"{char.ToUpperInvariant(g.Key[0])}{g.Key[1..]}:");
                foreach (var c in g) sb.AppendLine($"  {c.Name} {c.Version}".TrimEnd());
            }
        }
        if (Installed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Версии после установки:");
            foreach (var (n, v) in Installed) sb.AppendLine($"  {n} {v}");
        }
        if (!Preview)
        {
            sb.AppendLine();
            sb.AppendLine(RestartServices.Count > 0
                ? $"Держат старые файлы (нужен перезапуск): {string.Join(", ", RestartServices)}"
                : "Служб со старыми файлами не найдено.");
        }
        return sb.ToString();
    }
}

/// <summary>Результат перезапуска служб.</summary>
public sealed class RestartReport
{
    public string Host { get; set; } = "";
    public List<(string Unit, string State, string Note)> Services { get; } = new();
    public int Failed => Services.Count(s => s.State != "ok");

    public string Summary => Services.Count == 0
        ? "службы не перезапускались"
        : string.Join(", ", Services.Select(s => PackageUpdateReport.ShortUnit(s.Unit) + (s.State == "ok" ? "" : s.State == "skip" ? " — пропущена" : " — ошибка")));

    public string Format()
    {
        var sb = new StringBuilder($"host: {Host}\n");
        foreach (var (u, st, note) in Services)
            sb.AppendLine($"  {u}: {(st == "ok" ? "перезапущена" : st == "skip" ? "пропущена" : "ошибка")}{(note.Length > 0 ? " — " + note : "")}");
        return sb.ToString();
    }
}

/// <summary>
/// «Обновить отмеченное» из таблицы «Версии ПО»: только выбранные пакеты (и пакеты той же сборки), не всё подряд.
/// Предпросмотр — dnf --assumeno / apt-get -s / zypper --dry-run; установка — после подтверждения. Службы со старыми
/// файлами только находятся (/proc/*/maps, «(deleted)»), перезапуск — отдельным действием после согласия.
/// Действия в каталоге не показываются — запускаются из таблицы.
/// </summary>
public static class PackageUpdate
{
    public const string ActionId = "update-selected";
    public const string RestartId = "restart-services";
    public const string PkgsParam = "UPD_PKGS";
    public const string SvcsParam = "UPD_SVCS";

    /// <summary>Службы «наших» продуктов — в окне перезапуска отмечены сразу; прочие — по желанию.</summary>
    public static bool IsProductService(string unit)
    {
        var u = PackageUpdateReport.ShortUnit(unit);
        return u is "nginx" or "httpd" or "apache2" or "sshd" or "ssh" or "mariadb" or "mysqld" or "mysql" or "redis" or "redis-server"
               || u.StartsWith("php", StringComparison.Ordinal) && u.Contains("fpm", StringComparison.Ordinal)
               || u.StartsWith("postgresql", StringComparison.Ordinal)
               || u.StartsWith("redis@", StringComparison.Ordinal);
    }

    public static ActionDefinition Definition(IEnumerable<string> packages, bool viaPc) => new()
    {
        Id = ActionId,
        Name = "Обновить отмеченные пакеты",
        Description = "Пакеты из таблицы «Версии ПО» и пакеты той же сборки",
        BuiltIn = true,
        Destructive = true,
        TimeoutSeconds = 1800,
        Distros = new List<string> { "any" },
        Parameters = new List<ActionParam>
        {
            new() { Name = PkgsParam, Default = string.Join(" ", packages) },
            new() { Name = VersionsCheck.ViaPcParam, Kind = "bool", Default = viaPc ? "да" : "нет" }
        }
    };

    public static ActionDefinition RestartDefinition(IEnumerable<string> units) => new()
    {
        Id = RestartId,
        Name = "Перезапустить службы",
        Description = "systemctl restart; sshd — только если sshd -t прошёл",
        BuiltIn = true,
        Destructive = true,
        TimeoutSeconds = 300,
        Distros = new List<string> { "any" },
        Parameters = new List<ActionParam> { new() { Name = SvcsParam, Default = string.Join(" ", units) } }
    };

    /// <summary>Службы, у процессов которых в памяти удалённые (заменённые обновлением) файлы.</summary>
    private const string StaleServices = """
        stale() {
          for m in /proc/[0-9]*/maps; do
            # только заменённые программы и библиотеки (/dev/zero, memfd и временные файлы — не в счёт)
            grep -qE ' /(usr|lib|lib64|bin|sbin|opt)/[^ ]* \(deleted\)$' "$m" 2>/dev/null || continue
            p=${m#/proc/}; p=${p%/maps}
            u=$(ps -o unit= -p "$p" 2>/dev/null | tr -d ' ')
            case "$u" in user@*|*.scope|"") ;; *.service) printf '%s\n' "$u" ;; esac
          done | sort -u | while read -r u; do printf 'RESTART\t%s\n' "$u"; done
        }
        """;

    public const string Script = $$"""
        main() {
        {{StaleServices}}
        PM=none
        if command -v dnf >/dev/null 2>&1; then PM=dnf
        elif command -v yum >/dev/null 2>&1; then PM=yum
        elif command -v apt-get >/dev/null 2>&1; then PM=apt
        elif command -v zypper >/dev/null 2>&1; then PM=zypper
        fi
        printf 'PM\t%s\n' "$PM"
        [ "$PM" != none ] || { printf 'ERR\tменеджер пакетов не найден\n'; return 2; }

        # отмеченные + пакеты той же сборки (php-fpm → php-common, php-cli…; openssl → openssl-libs)
        ALL=""
        for p in $UPD_PKGS; do
          case "$p" in ""|-*|*[!A-Za-z0-9._+:-]*) continue ;; esac
          sib=""
          if [ "$PM" = apt ]; then
            s=$(dpkg-query -W -f='${source:Package}' "$p" 2>/dev/null)
            [ -n "$s" ] && sib=$(dpkg-query -W -f='${Package} ${source:Package} ${db:Status-Abbrev}\n' 2>/dev/null | awk -v s="$s" '$2==s && $3 ~ /^ii/ {print $1}')
          elif command -v rpm >/dev/null 2>&1; then
            s=$(rpm -q --qf '%{SOURCERPM}\n' "$p" 2>/dev/null | head -n1)
            case "$s" in *.rpm) sib=$(rpm -qa --qf '%{NAME} %{SOURCERPM}\n' 2>/dev/null | awk -v s="$s" '$2==s {print $1}') ;; esac
          fi
          ALL="$ALL $p $sib"
        done
        ALL=$(printf '%s\n' $ALL | sort -u | tr '\n' ' ')
        printf 'PKGS\t%s\n' "$ALL"
        [ -n "$(printf '%s' "$ALL" | tr -d ' ')" ] || { printf 'ERR\tпакеты не выбраны\n'; return 2; }

        set --
        case "$PM" in
          dnf|yum) [ -n "$UPD_PROXY" ] && set -- --setopt=proxy="$UPD_PROXY" ;;
          apt) [ -n "$UPD_PROXY" ] && set -- -o Acquire::http::Proxy="$UPD_PROXY" -o Acquire::https::Proxy="$UPD_PROXY" ;;
          zypper) [ -n "$UPD_PROXY" ] && export http_proxy="$UPD_PROXY" https_proxy="$UPD_PROXY" HTTP_PROXY="$UPD_PROXY" HTTPS_PROXY="$UPD_PROXY" ;;
        esac

        if [ "$LINUXOPS_DRY" = 1 ]; then
          case "$PM" in
            dnf|yum)
              out=$($PM "$@" upgrade --assumeno $ALL 2>&1); rc=$?
              printf '%s\n' "$out" | awk '
                /^Transaction Summary/ {sec=""; next}
                /^[A-Z][A-Za-z ]*:$/ {sec=$0; sub(/:$/, "", sec); next}
                sec != "" && NF == 1 && $1 !~ /^=+$/ {pend=$1; next}
                sec != "" && NF >= 4 { if (pend != "") { n=pend; v=$2; pend="" } else { n=$1; v=$3 }; printf "P\t%s\t%s\t%s\n", sec, n, v }'
              if printf '%s\n' "$out" | grep -q '^Nothing to do'; then printf 'NOTHING\n'
              elif ! printf '%s\n' "$out" | grep -q '^Transaction Summary'; then
                printf 'ERR\t%s\n' "$(printf '%s\n' "$out" | grep -v '^[[:space:]]*$' | tail -n1)"; printf 'RC\t%s\n' "$rc"; return 1
              fi ;;
            apt)
              out=$(apt-get "$@" -s install --only-upgrade $ALL 2>&1); rc=$?
              [ $rc -eq 0 ] || { printf 'ERR\t%s\n' "$(printf '%s\n' "$out" | grep -v '^[[:space:]]*$' | tail -n1)"; printf 'RC\t%s\n' "$rc"; return 1; }
              printf '%s\n' "$out" | awk '
                /^Inst / { v=$0; sub(/^[^(]*\(/, "", v); sub(/ .*/, "", v); printf "P\t%s\t%s\t%s\n", ($3 ~ /^\[/ ? "Upgrading" : "Installing"), $2, v }
                /^Remv / { printf "P\tRemoving\t%s\t\n", $2 }'
              printf '%s\n' "$out" | grep -q '^Inst ' || printf 'NOTHING\n' ;;
            zypper)
              out=$(zypper -n update --dry-run $ALL 2>&1); rc=$?
              [ $rc -eq 0 ] || { printf 'ERR\t%s\n' "$(printf '%s\n' "$out" | grep -v '^[[:space:]]*$' | tail -n1)"; printf 'RC\t%s\n' "$rc"; return 1; }
              printf '%s\n' "$out" | awk '
                /going to be upgraded:$/ {sec="Upgrading"; next}
                /going to be installed:$/ || /NEW package.*going to be installed:$/ {sec="Installing"; next}
                /going to be REMOVED:$/ {sec="Removing"; next}
                /^[^ ]/ || NF == 0 {sec=""; next}
                sec != "" { for (i = 1; i <= NF; i++) printf "P\t%s\t%s\t\n", sec, $i }'
              printf '%s\n' "$out" | grep -qi 'nothing to do' && printf 'NOTHING\n' ;;
          esac
          printf 'RC\t0\n'
          return 0
        fi

        case "$PM" in
          dnf|yum) $PM -y "$@" upgrade $ALL; rc=$? ;;
          apt) DEBIAN_FRONTEND=noninteractive apt-get -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold "$@" install --only-upgrade $ALL; rc=$? ;;
          zypper) zypper -n update $ALL; rc=$? ;;
        esac
        printf 'RC\t%s\n' "$rc"
        for p in $ALL; do
          if [ "$PM" = apt ]; then v=$(dpkg-query -W -f='${Version}' "$p" 2>/dev/null)
          else v=$(rpm -q --qf '%{VERSION}-%{RELEASE}' "$p" 2>/dev/null); fi
          [ -n "$v" ] && printf 'NOW\t%s\t%s\n' "$p" "$v"
        done
        stale
        return $rc
        }
        main </dev/null
        """;

    public const string RestartScript = """
        main() {
        for u in $UPD_SVCS; do
          case "$u" in ""|-*|*[!A-Za-z0-9@._:-]*) continue ;; esac
          case "$u" in
            sshd.service|ssh.service)
              s=$(command -v sshd 2>/dev/null || echo /usr/sbin/sshd)
              if ! e=$("$s" -t 2>&1); then
                printf 'SVC\t%s\tskip\tпроверка sshd -t не прошла: %s\n' "$u" "$(printf '%s\n' "$e" | tail -n1)"; continue
              fi ;;
          esac
          if out=$(systemctl restart "$u" 2>&1); then
            printf 'SVC\t%s\tok\t%s\n' "$u" "$(systemctl is-active "$u" 2>/dev/null)"
          else
            printf 'SVC\t%s\tfail\t%s\n' "$u" "$(printf '%s\n' "$out" | tail -n1)"
          fi
        done
        }
        main </dev/null
        """;

    public static PackageUpdateReport? Parse(string stdout, bool preview)
    {
        PackageUpdateReport? r = null;
        foreach (var raw in stdout.Replace("\r", "").Split('\n'))
        {
            var p = raw.Split('\t');
            switch (p[0])
            {
                case "PM" when p.Length >= 2:
                    r = new PackageUpdateReport { PackageManager = p[1], Preview = preview };
                    break;
                case "PKGS" when r is not null && p.Length >= 2:
                    r.Packages.AddRange(p[1].Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "P" when r is not null && p.Length >= 3 && p[2].Length > 0:
                    r.Changes.Add(new PackageChange(p[1], p[2], p.Length > 3 ? p[3] : ""));
                    break;
                case "NOTHING" when r is not null:
                    r.NothingToDo = true;
                    break;
                case "ERR" when r is not null && p.Length >= 2:
                    r.Error = p[1].Length > 0 ? p[1] : "ошибка менеджера пакетов";
                    break;
                case "RC" when r is not null && p.Length >= 2 && int.TryParse(p[1].Trim(), out var rc):
                    r.ExitCode = rc;
                    break;
                case "NOW" when r is not null && p.Length >= 3:
                    r.Installed.Add((p[1], p[2]));
                    break;
                case "RESTART" when r is not null && p.Length >= 2 && p[1].Length > 0:
                    r.RestartServices.Add(p[1]);
                    break;
            }
        }
        return r;
    }

    public static RestartReport ParseRestart(string stdout)
    {
        var r = new RestartReport();
        foreach (var raw in stdout.Replace("\r", "").Split('\n'))
        {
            var p = raw.Split('\t');
            if (p[0] == "SVC" && p.Length >= 3)
                r.Services.Add((p[1], p[2], p.Length > 3 && p[2] != "ok" ? p[3] : ""));
        }
        return r;
    }
}
