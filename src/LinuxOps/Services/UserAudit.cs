using System.Text;

namespace LinuxOps.Services;

public sealed class AuditKey
{
    public string Type { get; init; } = "";
    public string Bits { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string Comment { get; init; } = "";
    public string File { get; init; } = "";
    public bool Unreadable { get; init; }

    public string ShortFingerprint => Fingerprint.Length > 24 ? Fingerprint[..24] + "…" : Fingerprint;
}

public sealed class AuditUser
{
    public string Login { get; init; } = "";
    public int Uid { get; init; }
    public string Home { get; init; } = "";
    public string Shell { get; init; } = "";
    public string Groups { get; init; } = "";
    /// <summary>root | group:wheel | sudoers | пусто.</summary>
    public string Sudo { get; init; } = "";
    /// <summary>set | none | locked | ? (нет доступа к shadow).</summary>
    public string Password { get; init; } = "";
    public bool Expired { get; init; }
    public List<AuditKey> Keys { get; } = new();

    public bool HasSudo => Sudo.Length > 0;
    public string SudoText => Sudo switch
    {
        "root" => "root",
        "sudoers" => "sudo (sudoers)",
        var s when s.StartsWith("group:", StringComparison.Ordinal) => "sudo (" + s[6..] + ")",
        _ => ""
    };
    public bool IsLocked => Password == "locked" || Expired;
    public string LockText => Expired && Password == "locked" ? "заблокирован" : Expired ? "срок истёк" : Password == "locked" ? "пароль заблокирован" : "";
    public string PasswordText => Password switch { "set" => "пароль задан", "none" => "без пароля", "locked" => "пароль заблокирован", _ => "пароль: нет доступа" };
    public string Info => $"uid {Uid} · {Shell} · {Home}";
    public bool HasReadableKeys => Keys.Any(k => !k.Unreadable);
    public string KeysTitle
    {
        get
        {
            if (Keys.Count > 0 && Keys.All(k => k.Unreadable)) return "ключи: нет доступа (нужен root или sudo)";
            var n = Keys.Count(k => !k.Unreadable);
            return n switch { 0 => "ключей нет", 1 => "1 ключ", < 5 => $"{n} ключа", _ => $"{n} ключей" };
        }
    }
}

public sealed class UserAuditReport
{
    public List<AuditUser> Users { get; } = new();
    public List<string> SudoersFiles { get; } = new();

    public string Summary
    {
        get
        {
            var sudo = Users.Count(u => u.HasSudo);
            var keys = Users.Sum(u => u.Keys.Count(k => !k.Unreadable));
            var locked = Users.Count(u => u.IsLocked);
            return $"учёток {Users.Count} · sudo {sudo} · ключей {keys}" + (locked > 0 ? $" · заблокировано {locked}" : "");
        }
    }

    public string SudoersText => SudoersFiles.Count == 0 ? "/etc/sudoers.d: пусто" : "/etc/sudoers.d: " + string.Join(", ", SudoersFiles);
}

/// <summary>Аудит учёток: скрипт отдаёт строки с табами (U/K/S), здесь — разбор и текстовый вид.</summary>
public static class UserAudit
{
    public const string ActionId = "list-users";

    /// <summary>Учётки с UID 0 или 1000–65533, плюс любые с оболочкой из /etc/shells. Ключи — authorized_keys(2) в домашней папке.</summary>
    public const string Script = """
        export LC_ALL=C
        is_sudo_group() { case "$1" in wheel|sudo|admin) return 0;; esac; return 1; }
        getent passwd | while IFS=: read -r login _ uid _ _ home shell; do
          has_shell=0; grep -qxF -- "$shell" /etc/shells 2>/dev/null && has_shell=1
          human=0
          if [ "$uid" -eq 0 ] || { [ "$uid" -ge 1000 ] && [ "$uid" -lt 65534 ]; }; then human=1; fi
          [ "$human" -eq 1 ] || [ "$has_shell" -eq 1 ] || continue
          groups=$(id -nG "$login" 2>/dev/null)
          sudo=""
          for g in $groups; do if is_sudo_group "$g"; then sudo="group:$g"; break; fi; done
          if [ -z "$sudo" ] && grep -qsE "^[[:space:]]*$login[[:space:]]" /etc/sudoers /etc/sudoers.d/* 2>/dev/null; then sudo="sudoers"; fi
          [ "$uid" -eq 0 ] && sudo="root"
          sh=$(getent shadow "$login" 2>/dev/null)
          if [ -z "$sh" ]; then pw="?"; exp=""
          else
            hash=$(printf '%s' "$sh" | cut -d: -f2); exp=$(printf '%s' "$sh" | cut -d: -f8)
            case "$hash" in "!"|"!!"|"*"|"") pw="none";; "!"*) pw="locked";; *) pw="set";; esac
          fi
          expired=0
          if [ -n "$exp" ]; then today=$(( $(date +%s) / 86400 )); [ "$exp" -le "$today" ] && expired=1; fi
          printf 'U\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$login" "$uid" "$home" "$shell" "$(printf '%s' "$groups" | tr ' ' ',')" "$sudo" "$pw" "$expired"
          # Без прав на чужую домашнюю папку ключи не видны — это «нет доступа», а не «ключей нет».
          if [ ! -x "$home" ] || { [ -d "$home/.ssh" ] && [ ! -x "$home/.ssh" ]; }; then
            printf 'K\t%s\t%s\t-\t-\t-\t-\n' "$login" "$home/.ssh"; continue
          fi
          for f in "$home/.ssh/authorized_keys" "$home/.ssh/authorized_keys2"; do
            [ -e "$f" ] || continue
            if [ ! -r "$f" ]; then printf 'K\t%s\t%s\t-\t-\t-\t-\n' "$login" "$f"; continue; fi
            ssh-keygen -lf "$f" 2>/dev/null | while read -r bits fp rest; do
              printf 'K\t%s\t%s\t%s\t%s\t%s\n' "$login" "$f" "$bits" "$fp" "$rest"
            done
          done
        done
        for f in /etc/sudoers.d/*; do [ -e "$f" ] && printf 'S\t%s\n' "$(basename "$f")"; done
        exit 0
        """;

    public static UserAuditReport? Parse(string stdout)
    {
        var report = new UserAuditReport();
        var byLogin = new Dictionary<string, AuditUser>(StringComparer.Ordinal);
        foreach (var raw in stdout.Replace("\r", "").Split('\n'))
        {
            var p = raw.Split('\t');
            switch (p[0])
            {
                case "U" when p.Length >= 9:
                    var u = new AuditUser
                    {
                        Login = p[1],
                        Uid = int.TryParse(p[2], out var uid) ? uid : -1,
                        Home = p[3],
                        Shell = p[4],
                        Groups = p[5].Replace(",", ", "),
                        Sudo = p[6],
                        Password = p[7],
                        Expired = p[8] == "1"
                    };
                    report.Users.Add(u);
                    byLogin[u.Login] = u;
                    break;
                case "K" when p.Length >= 4 && byLogin.TryGetValue(p[1], out var owner):
                    owner.Keys.Add(ParseKey(p));
                    break;
                case "S" when p.Length >= 2:
                    report.SudoersFiles.Add(p[1]);
                    break;
            }
        }

        return report.Users.Count == 0 ? null : report;
    }

    private static AuditKey ParseKey(string[] p)
    {
        if (p.Length < 6 || p[3] == "-")
            return new AuditKey { File = p[2], Unreadable = true, Fingerprint = "нет доступа к файлу" };

        // ssh-keygen -l: "<bits> <fp> <comment> (<TYPE>)"
        var rest = p[5].Trim();
        var type = "";
        var open = rest.LastIndexOf('(');
        if (open >= 0 && rest.EndsWith(')'))
        {
            type = rest[(open + 1)..^1];
            rest = rest[..open].Trim();
        }

        return new AuditKey
        {
            File = p[2],
            Bits = p[3],
            Fingerprint = p[4],
            Type = type,
            Comment = rest == "no comment" ? "" : rest
        };
    }

    /// <summary>Текст для «Копировать» и истории.</summary>
    public static string Format(string host, UserAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"host: {host}");
        sb.AppendLine($"аудит пользователей: {report.Summary}");
        foreach (var u in report.Users)
        {
            sb.AppendLine();
            var flags = new List<string>();
            if (u.HasSudo) flags.Add(u.SudoText);
            flags.Add(u.PasswordText);
            if (u.Expired) flags.Add("срок истёк");
            sb.AppendLine($"{u.Login}  ({u.Info})  [{string.Join(", ", flags)}]");
            if (u.Groups.Length > 0) sb.AppendLine($"  группы: {u.Groups}");
            if (u.Keys.Count == 0) sb.AppendLine("  ключей нет");
            foreach (var k in u.Keys)
                sb.AppendLine(k.Unreadable
                    ? $"  ключ: {k.File} — нет доступа"
                    : $"  ключ: {k.Type} {k.Bits} {k.Fingerprint}" + (k.Comment.Length > 0 ? $"  {k.Comment}" : ""));
        }

        sb.AppendLine();
        sb.AppendLine(report.SudoersText);
        return sb.ToString().TrimEnd();
    }
}
