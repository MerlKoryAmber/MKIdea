using System.Text.RegularExpressions;
using LinuxOps.Models;

namespace LinuxOps.Services;

public enum AccessMode
{
    Lock,
    RevokeKey,
    Delete,
    Unlock
}

public sealed class AccessRequest
{
    public required string Login { get; init; }
    public required AccessMode Mode { get; init; }
    /// <summary>Base64 ключей для «Отозвать ключ» (по одному в строке).</summary>
    public string KeyBlobs { get; init; } = "";
}

/// <summary>Что нашлось на хосте до изменений (режим check).</summary>
public sealed class HostAccessState
{
    public required HostConfig Host { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, string> Map { get; init; } = new();

    public bool Exists => Get("exists") == "yes";
    public string Guard => Get("guard") ?? "none";
    public bool LockedByUs => Get("locked") == "linuxops";
    public int Keys => Int("keys");
    public int HasKey => Int("haskey") + Int("haskey_locked");
    public int Procs => Int("procs");
    public string SudoGroups => Get("sudo_groups") ?? "";
    public bool SudoFile => Get("sudo_file") == "yes";
    public string SudoOther => Get("sudo_other") ?? "";

    public string? Get(string key) => Map.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
    private int Int(string key) => int.TryParse(Get(key), out var n) ? n : 0;
}

/// <summary>
/// «Закрыть доступ» и «Открыть доступ»: отозвать ключ, заблокировать (обратимо), удалить учётную запись с архивом дома.
/// root, системные учётки и учётка, под которой LinuxOps входит на хост, не трогаются — проверка и на сервере.
/// </summary>
public static class UserAccess
{
    private static readonly Regex LoginRx = new(@"^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyRx = new(
        @"(?:^|\s)(?:ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-\S+)\s+([A-Za-z0-9+/]{40,}={0,2})(?:\s|$)",
        RegexOptions.CultureInvariant);

    public static string? ValidateLogin(string login)
        => LoginRx.IsMatch(login.Trim()) ? null : "Логин: латиница, с цифры нельзя, до 32 символов (a-z 0-9 _ -).";

    /// <summary>Base64-часть ключей из вставленного текста (по ней ищется строка в authorized_keys).</summary>
    public static IReadOnlyList<string> KeyBlobs(string? text)
        => (text ?? "").Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => KeyRx.Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

    public static string ModeTitle(AccessMode mode) => mode switch
    {
        AccessMode.Lock => "Заблокировать",
        AccessMode.RevokeKey => "Отозвать ключ",
        AccessMode.Delete => "Удалить учётную запись",
        _ => "Открыть доступ"
    };

    public static async Task<IReadOnlyList<HostAccessState>> CheckAsync(
        IReadOnlyList<HostConfig> hosts, AccessRequest request, CancellationToken cancellationToken)
    {
        var gate = new SemaphoreSlim(4);
        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await Task.Run(() => CheckOne(host, request, cancellationToken), cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private static HostAccessState CheckOne(HostConfig host, AccessRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var ssh = SshRunner.Connect(host);
            var result = Run(ssh, host, request, "check", 60, cancellationToken);
            if (result.ExitCode != 0)
                return new HostAccessState { Host = host, Error = First(result.StdErr) ?? First(result.StdOut) ?? $"exit {result.ExitCode}" };
            return new HostAccessState { Host = host, Map = Parse(result.StdOut) };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HostAccessState { Host = host, Error = ex.Message };
        }
    }

    /// <summary>Можно ли выполнять режим на этом хосте и что будет сделано.</summary>
    public static (bool Applicable, string Text) Plan(HostAccessState s, AccessMode mode)
    {
        if (s.Error is not null) return (false, "ошибка: " + s.Error);
        if (!s.Exists) return (false, "пользователя нет");
        switch (s.Guard)
        {
            case "root": return (false, "root — не трогаем");
            case "system": return (false, $"системная учётная запись (uid {s.Get("uid")}) — не трогаем");
            case "self": return (false, "под ним LinuxOps входит на этот хост — не трогаем");
        }

        var parts = new List<string>();
        switch (mode)
        {
            case AccessMode.Lock:
                if (s.LockedByUs) return (s.Procs > 0, s.Procs > 0 ? $"уже заблокирован; завершить процессы ({s.Procs})" : "уже заблокирован");
                parts.Add("закрыть вход по паролю и ключу");
                if (s.Keys > 0) parts.Add($"ключи ({s.Keys}) — в сторону");
                if (s.SudoGroups.Length > 0) parts.Add("убрать из " + s.SudoGroups);
                if (s.SudoFile) parts.Add("убрать файл sudoers LinuxOps");
                if (s.Procs > 0) parts.Add($"завершить процессы ({s.Procs})");
                return (true, string.Join(", ", parts) + Warnings(s, mode));
            case AccessMode.RevokeKey:
                if (s.HasKey == 0) return (false, $"этого ключа нет (ключей у него: {s.Keys})");
                return (true, $"убрать ключ ({s.HasKey} {Lines(s.HasKey)}), учётная запись остаётся");
            case AccessMode.Delete:
                parts.Add(s.Get("home_own") == "yes"
                    ? $"{s.Get("home")} — в архив /root/linuxops-removed, затем удалить"
                    : "домашняя папка не его — не трогать");
                parts.Add("удалить учётную запись");
                if (s.Procs > 0) parts.Add($"завершить процессы ({s.Procs})");
                if (s.Get("crontab") == "yes") parts.Add("удалить crontab");
                return (true, string.Join(", ", parts) + Warnings(s, mode));
            default:
                if (!s.LockedByUs)
                    return (false, s.Get("locked") == "expired" ? "срок учётной записи истёк, но блокировал не LinuxOps — не трогаю" : "не заблокирован");
                parts.Add("вернуть пароль и срок");
                if (s.Get("saved_keys") is { } k && k != "0") parts.Add($"ключи ({k})");
                if (s.Get("saved_groups") is { } g) parts.Add(g);
                if (s.Get("saved_sudoers") == "yes") parts.Add("файл sudoers");
                return (true, string.Join(", ", parts));
        }
    }

    private static string Warnings(HostAccessState s, AccessMode mode)
    {
        var w = new List<string>();
        if (s.SudoOther.Length > 0)
            w.Add(mode == AccessMode.Lock
                ? $"правила sudo в {s.SudoOther} не трогаю (при истёкшей учётке не сработают)"
                : $"правила sudo в {s.SudoOther} останутся — уберите вручную");
        if (s.Get("only_sudo") == "yes" && (s.SudoGroups.Length > 0 || s.SudoFile))
            w.Add("кроме него sudo ни у кого нет (root остаётся)");
        return w.Count == 0 ? "" : " · внимание: " + string.Join("; ", w);
    }

    /// <summary>Состояние сейчас — для столбца «Сейчас».</summary>
    public static string Now(HostAccessState s)
    {
        if (s.Error is not null || !s.Exists) return "";
        var parts = new List<string>
        {
            s.LockedByUs ? "заблокирован" : s.Get("locked") == "expired" ? "срок истёк" : "активен",
            $"ключей {s.Keys}"
        };
        var sudo = new List<string>();
        if (s.SudoGroups.Length > 0) sudo.Add(s.SudoGroups);
        if (s.SudoFile) sudo.Add("файл");
        if (s.SudoOther.Length > 0) sudo.Add("правила");
        if (sudo.Count > 0) parts.Add("sudo: " + string.Join(", ", sudo));
        if (s.Procs > 0) parts.Add($"процессов {s.Procs}");
        return string.Join(" · ", parts);
    }

    public static async Task ApplyAsync(
        IReadOnlyList<HostConfig> hosts, AccessRequest request, IProgress<PlanProgress> progress, CancellationToken cancellationToken)
    {
        var gate = new SemaphoreSlim(4);
        var n = 0;
        var tasks = hosts.Select(async host =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() => ApplyOne(host, request, progress, cancellationToken), cancellationToken);
            }
            finally
            {
                var done = Interlocked.Increment(ref n);
                progress.Report(new PlanProgress { Status = $"хост {done} из {hosts.Count}" });
                gate.Release();
            }
        });
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                   || (ex is AggregateException ae && ae.InnerExceptions.All(e => e is OperationCanceledException)))
        {
            throw new OperationCanceledException();
        }
    }

    private static void ApplyOne(HostConfig host, AccessRequest request, IProgress<PlanProgress> progress, CancellationToken cancellationToken)
    {
        var title = $"{host.Title} · {request.Login}";
        try
        {
            using var ssh = SshRunner.Connect(host);
            var mode = request.Mode switch
            {
                AccessMode.Lock => "lock",
                AccessMode.RevokeKey => "revoke",
                AccessMode.Delete => "delete",
                _ => "unlock"
            };
            var result = Run(ssh, host, request, mode, request.Mode == AccessMode.Delete ? 900 : 120, cancellationToken);
            var ok = result.ExitCode == 0;
            var map = Parse(result.StdOut);
            var line = ok ? Done(request.Mode, map) : First(result.StdErr) ?? First(result.StdOut) ?? $"exit {result.ExitCode}";
            var status = !ok ? "FAIL" : map.GetValueOrDefault("exists") == "no" ? "SKIP" : "OK";
            progress.Report(new PlanProgress
            {
                Row = PlanRunner.MakeRow(status, title, line, (result.StdOut + "\n" + result.StdErr).Trim(), ok)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress.Report(new PlanProgress { Row = PlanRunner.MakeRow("FAIL", title, ex.Message, ex.ToString(), ok: false) });
        }
    }

    private static string Done(AccessMode mode, Dictionary<string, string> m)
    {
        string V(string k) => m.GetValueOrDefault(k) ?? "";
        if (V("exists") == "no") return "пользователя нет";
        var parts = new List<string>();
        switch (mode)
        {
            case AccessMode.Lock:
                parts.Add(V("already") == "yes" ? "уже был заблокирован" : "заблокирован");
                if (V("moved_keys") is { Length: > 0 } k && k != "0") parts.Add($"ключи ({k}) убраны");
                if (V("removed_groups").Length > 0) parts.Add("убран из " + V("removed_groups"));
                if (V("moved_sudoers") == "yes") parts.Add("файл sudoers убран");
                if (V("killed") is { Length: > 0 } p && p != "0") parts.Add($"завершено процессов: {p}");
                break;
            case AccessMode.RevokeKey:
                parts.Add($"ключ отозван ({V("removed")} {Lines(int.TryParse(V("removed"), out var r) ? r : 0)})");
                break;
            case AccessMode.Delete:
                parts.Add("учётная запись удалена");
                if (V("archive").Length > 0) parts.Add($"архив {V("archive")} ({V("archive_size")})");
                if (V("home_left") == "yes") parts.Add("домашняя папка не его — оставлена");
                break;
            default:
                parts.Add("доступ открыт");
                if (V("restored_keys") is { Length: > 0 } rk && rk != "0") parts.Add($"ключи ({rk})");
                if (V("restored_groups").Length > 0) parts.Add(V("restored_groups"));
                if (V("restored_sudoers") == "yes") parts.Add("файл sudoers");
                if (V("password") == "none") parts.Add("пароля нет — вход только по ключу");
                break;
        }

        return string.Join(", ", parts);
    }

    private static string Lines(int n) => n % 10 == 1 && n % 100 != 11 ? "строка" : n % 10 is >= 2 and <= 4 && (n % 100 < 10 || n % 100 >= 20) ? "строки" : "строк";

    private static ActionResult Run(SshRunner ssh, HostConfig host, AccessRequest request, string mode, int timeout, CancellationToken cancellationToken)
    {
        var action = new ActionDefinition { Id = "user-access", Privilege = "sudo/root", TimeoutSeconds = timeout };
        var env = new Dictionary<string, string>
        {
            ["LINUXOPS_USER"] = request.Login,
            ["LINUXOPS_MODE"] = mode,
            ["LINUXOPS_SELF"] = host.Username,
            ["LINUXOPS_KEYS"] = request.KeyBlobs
        };
        return ssh.Run("user-access-" + mode, Script, timeout, SshRunner.NeedsSudo(host, action), host, env, cancellationToken);
    }

    private static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf('=');
            if (i > 0 && Regex.IsMatch(line[..i], "^[a-z_]+$")) map[line[..i]] = line[(i + 1)..];
        }

        return map;
    }

    private static string? First(string text)
        => text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>
    /// Один скрипт на все режимы. Блокировка обратима: исходный пароль (хэш), срок, ключи, группы и файл sudoers
    /// LinuxOps лежат в /var/lib/linuxops/locked/&lt;логин&gt; (только root), «Открыть доступ» возвращает их как были.
    /// </summary>
    private const string Script = """
        set -uo pipefail
        login="$LINUXOPS_USER"; mode="$LINUXOPS_MODE"; self="${LINUXOPS_SELF:-}"; blobs="${LINUXOPS_KEYS:-}"
        die() { echo "$1" >&2; exit 1; }
        case "$login" in ''|*[!a-z0-9_-]*) die "недопустимый логин" ;; esac
        state=/var/lib/linuxops/locked; st="$state/$login"
        ourfile="/etc/sudoers.d/linuxops-$login"
        if ! ent=$(getent passwd "$login"); then echo "exists=no"; exit 0; fi
        IFS=: read -r _ _ uid gid _ home _ <<<"$ent"
        echo "exists=yes"; echo "uid=$uid"; echo "home=$home"
        guard=none
        if [ "$uid" -eq 0 ] || [ "$login" = root ]; then guard=root
        elif [ "$uid" -lt 1000 ] || [ "$uid" -ge 65534 ]; then guard=system
        elif [ "$login" = "$self" ]; then guard=self; fi
        echo "guard=$guard"
        ak="$home/.ssh/authorized_keys"
        keyre='(^|[[:space:]])(ssh-ed25519|ssh-rsa|ecdsa-sha2-[^[:space:]]+|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-[^[:space:]]+)[[:space:]]'
        count_keys() { if [ -f "$1" ]; then grep -Ec "$keyre" "$1" 2>/dev/null || true; else echo 0; fi; }
        count_blobs() {
          local f="$1" n=0 c b
          [ -f "$f" ] || { echo 0; return; }
          while IFS= read -r b; do
            [ -z "$b" ] && continue
            c=$(grep -cF -- "$b" "$f" 2>/dev/null || true); n=$((n + ${c:-0}))
          done <<<"$blobs"
          echo "$n"
        }
        echo "keys=$(count_keys "$ak")"
        echo "haskey=$(count_blobs "$ak")"
        [ -f "$st/authorized_keys" ] && echo "haskey_locked=$(count_blobs "$st/authorized_keys")"
        today=$(( $(date +%s) / 86400 ))
        exp=$(getent shadow "$login" | cut -d: -f8)
        if [ -d "$st" ]; then echo "locked=linuxops"
        elif [ -n "$exp" ] && [ "$exp" -le "$today" ] 2>/dev/null; then echo "locked=expired"
        else echo "locked=no"; fi
        sgroups=$(id -nG "$login" 2>/dev/null | tr ' ' '\n' | grep -Ex 'wheel|sudo|admin' | paste -sd, - || true)
        echo "sudo_groups=$sgroups"
        [ -f "$ourfile" ] && echo "sudo_file=yes"
        other=$(grep -lsE "^[[:space:]]*${login}[[:space:]]" /etc/sudoers /etc/sudoers.d/* 2>/dev/null | grep -vxF "$ourfile" | paste -sd, - || true)
        echo "sudo_other=$other"
        others=$( { getent group wheel sudo admin | cut -d: -f4 | tr ',' '\n'; ls /etc/sudoers.d 2>/dev/null | sed -n 's/^linuxops-//p'; } | grep -v '^$' | grep -vx "$login" | sort -u | wc -l)
        [ "$others" -eq 0 ] && echo "only_sudo=yes"
        procs=$(pgrep -u "$uid" 2>/dev/null | wc -l)
        echo "procs=$procs"
        crontab -l -u "$login" >/dev/null 2>&1 && echo "crontab=yes"
        owner=$(stat -c %U "$home" 2>/dev/null || true)
        if [ -d "$home" ] && [ "$home" != / ] && [ "$owner" = "$login" ]; then echo "home_own=yes"; fi
        if [ -d "$st" ]; then
          echo "saved_keys=$(count_keys "$st/authorized_keys")"
          [ -s "$st/groups" ] && echo "saved_groups=$(paste -sd, "$st/groups")"
          [ -f "$st/sudoers" ] && echo "saved_sudoers=yes"
        fi
        [ "$mode" = check ] && exit 0

        case "$guard" in
          root) die "root не трогаем" ;;
          system) die "системная учётная запись (uid $uid) — не трогаем" ;;
          self) die "под $login LinuxOps входит на этот хост — не трогаем" ;;
        esac

        kill_all() {
          local n
          n=$(pgrep -u "$uid" 2>/dev/null | wc -l)
          if [ "$n" -gt 0 ]; then pkill -KILL -u "$uid" 2>/dev/null; sleep 1; fi
          echo "killed=$n"
        }

        case "$mode" in
        revoke)
          [ -n "$blobs" ] || die "не указан ключ"
          removed=0
          for f in "$ak" "$st/authorized_keys"; do
            [ -f "$f" ] || continue
            before=$(count_blobs "$f")
            [ "$before" -gt 0 ] || continue
            tmp=$(mktemp) || die "mktemp"
            grep -vF -f <(printf '%s\n' "$blobs" | grep -v '^$') -- "$f" > "$tmp" || true
            cat "$tmp" > "$f" || { rm -f "$tmp"; die "не записан $f"; }
            rm -f "$tmp"
            removed=$((removed + before - $(count_blobs "$f")))
          done
          echo "removed=$removed"
          ;;
        lock)
          if [ -d "$st" ]; then
            echo "already=yes"
            usermod -L "$login" >/dev/null 2>&1 || true
            chage -E 0 "$login" || die "chage -E 0"
            kill_all
            exit 0
          fi
          mkdir -p -m 700 "$state" || die "не создан $state"
          mkdir -m 700 "$st" || die "не создан $st"
          getent shadow "$login" | cut -d: -f2 > "$st/password" || die "не прочитан пароль"
          printf '%s\n' "$exp" > "$st/expire"
          : > "$st/groups"
          chmod 600 "$st/password" "$st/expire" "$st/groups"
          usermod -L "$login" || die "usermod -L"
          chage -E 0 "$login" || die "chage -E 0"
          moved=0
          if [ -f "$ak" ]; then
            moved=$(count_keys "$ak")
            mv -f "$ak" "$st/authorized_keys" || die "не перенесён authorized_keys"
            chown root:root "$st/authorized_keys"; chmod 600 "$st/authorized_keys"
          fi
          echo "moved_keys=$moved"
          for g in $(id -nG "$login" | tr ' ' '\n' | grep -Ex 'wheel|sudo|admin'); do
            gpasswd -d "$login" "$g" >/dev/null || die "не убран из группы $g"
            echo "$g" >> "$st/groups"
          done
          echo "removed_groups=$(paste -sd, "$st/groups")"
          if [ -f "$ourfile" ]; then mv -f "$ourfile" "$st/sudoers" || die "не убран $ourfile"; echo "moved_sudoers=yes"; fi
          kill_all
          ;;
        unlock)
          [ -d "$st" ] || die "не заблокирован LinuxOps — возвращать нечего"
          pw=$(cat "$st/password")
          if [ -z "$pw" ]; then pw='!'; fi
          usermod -p "$pw" "$login" || die "не возвращён пароль"
          case "$pw" in '!'*|'*'*) echo "password=none" ;; *) echo "password=restored" ;; esac
          e=$(cat "$st/expire" 2>/dev/null || true)
          if [ -n "$e" ]; then chage -E "$e" "$login"; else chage -E -1 "$login"; fi || die "не возвращён срок"
          if [ -f "$st/authorized_keys" ]; then
            install -d -m 700 -o "$uid" -g "$gid" "$home/.ssh" || die "не создан $home/.ssh"
            cat "$st/authorized_keys" >> "$ak" || die "не возвращены ключи"
            chown "$uid:$gid" "$ak"; chmod 600 "$ak"
            command -v restorecon >/dev/null 2>&1 && restorecon -R "$home/.ssh" >/dev/null 2>&1
            echo "restored_keys=$(count_keys "$ak")"
          fi
          if [ -s "$st/groups" ]; then
            while IFS= read -r g; do [ -n "$g" ] && { usermod -aG "$g" "$login" || die "не возвращена группа $g"; }; done < "$st/groups"
            echo "restored_groups=$(paste -sd, "$st/groups")"
          fi
          if [ -f "$st/sudoers" ]; then
            mv -f "$st/sudoers" "$ourfile" || die "не возвращён $ourfile"
            chmod 440 "$ourfile"
            if command -v visudo >/dev/null 2>&1 && ! visudo -c >/dev/null 2>&1; then
              mv -f "$ourfile" "$st/sudoers"; die "sudoers не прошёл visudo -c — файл не возвращён"
            fi
            echo "restored_sudoers=yes"
          fi
          rm -rf "$st"
          ;;
        delete)
          crontab -r -u "$login" >/dev/null 2>&1 || true
          kill_all
          own=no
          if [ -d "$home" ] && [ "$home" != / ] && [ "$owner" = "$login" ]; then own=yes; fi
          if [ "$own" = yes ]; then
            mkdir -p -m 700 /root/linuxops-removed || die "не создан /root/linuxops-removed"
            archive="/root/linuxops-removed/$login-$(date +%Y%m%d-%H%M%S).tar.gz"
            tar -czf "$archive" -C "$(dirname "$home")" "$(basename "$home")" 2>/dev/null || { rm -f "$archive"; die "архив не создан — удаление отменено"; }
            chmod 600 "$archive"
            echo "archive=$archive"; echo "archive_size=$(du -h "$archive" | cut -f1)"
            userdel -r "$login" >/dev/null 2>&1 || true
          else
            userdel "$login" >/dev/null 2>&1 || true
            echo "home_left=yes"
          fi
          getent passwd "$login" >/dev/null && die "userdel не удалил $login (процессы ещё живы?)"
          if [ "$own" = yes ] && [ -d "$home" ]; then rm -rf --one-file-system -- "$home"; fi
          rm -f "$ourfile"; rm -rf "$st"
          echo "deleted=yes"
          ;;
        *) die "неизвестный режим $mode" ;;
        esac
        """;
}
