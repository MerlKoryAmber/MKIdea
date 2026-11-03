using LinuxOps.Models;

namespace LinuxOps.Services;

public static class BuiltinActions
{
    public static IReadOnlyList<ActionDefinition> All { get; } = new[]
    {
        new ActionDefinition
        {
            Id = "check-updates",
            Name = "Проверить обновления",
            Description = "Доступные пакеты: apt / dnf / zypper",
            BuiltIn = true,
            Distros = new List<string> { "ubuntu", "debian", "rhel", "alma", "suse" }
        },
        new ActionDefinition
        {
            Id = "install-updates",
            Name = "Установить обновления",
            Description = "Полный upgrade (нужен Применить)",
            BuiltIn = true,
            Destructive = true,
            Distros = new List<string> { "ubuntu", "debian", "rhel", "alma", "suse" }
        },
        new ActionDefinition
        {
            Id = "list-users",
            Name = "Аудит пользователей / sudo",
            Description = "Учётки, shell, wheel/sudo, sudoers.d",
            BuiltIn = true,
            Distros = new List<string> { "any" }
        },
        new ActionDefinition
        {
            Id = "ensure-user-demo",
            Name = "Пользователь и роль",
            Description = "План: создать пользователя и добавить в wheel/sudo",
            BuiltIn = true,
            Distros = new List<string> { "any" }
        }
    };

    public static string ResolveScript(string id, DistroInfo distro)
    {
        return id switch
        {
            "check-updates" => CheckUpdates(distro),
            "install-updates" => InstallUpdates(distro),
            "list-users" => ListUsers(distro),
            "ensure-user-demo" => EnsureUserDemo(distro),
            _ => throw new InvalidOperationException($"Неизвестное built-in действие: {id}")
        };
    }

    private static string CheckUpdates(DistroInfo d) => d.Family switch
    {
        "rhel" => """
            set -e
            if command -v dnf >/dev/null 2>&1; then
              dnf -q check-update || true
              echo "---"
              dnf -q updateinfo summary || true
            elif command -v yum >/dev/null 2>&1; then
              yum -q check-update || true
            else
              echo "Нет dnf/yum" >&2; exit 2
            fi
            """,
        "debian" => """
            set -e
            export DEBIAN_FRONTEND=noninteractive
            apt-get -qq update
            apt list --upgradable 2>/dev/null | sed -n '1,200p'
            """,
        "suse" => """
            set -e
            zypper --non-interactive refresh
            zypper --non-interactive lu
            """,
        _ => "echo \"Неизвестный family; укажите Script в custom action\" >&2; exit 3"
    };

    private static string InstallUpdates(DistroInfo d) => d.Family switch
    {
        "rhel" => """
            set -e
            if command -v dnf >/dev/null 2>&1; then
              dnf -y upgrade
            else
              yum -y update
            fi
            """,
        "debian" => """
            set -e
            export DEBIAN_FRONTEND=noninteractive
            apt-get -y upgrade
            """,
        "suse" => """
            set -e
            zypper --non-interactive update
            """,
        _ => "echo unsupported >&2; exit 3"
    };

    private static string ListUsers(DistroInfo d) => """
        echo "## login shells (не nologin/false)"
        getent passwd | awk -F: '$7 !~ /(nologin|false)$/ {print $1":"$3":"$6":"$7}'
        echo
        echo "## human-ish UID (0 или >=1000, <65534)"
        getent passwd | awk -F: '($3==0 || ($3>=1000 && $3<65534)) {print $1":"$3":"$6":"$7}'
        echo
        echo "## sudo / wheel"
        getent group wheel 2>/dev/null || echo "(no wheel)"
        getent group sudo 2>/dev/null || echo "(no sudo)"
        echo
        if command -v sudo >/dev/null 2>&1; then
          echo "## /etc/sudoers.d"
          ls -1 /etc/sudoers.d 2>/dev/null || echo "(empty)"
        fi
        """;

    private static string EnsureUserDemo(DistroInfo d)
    {
        var group = d.Family == "debian" ? "sudo" : "wheel";
        return string.Join('\n', new[]
        {
            "USERNAME=\"${LINUXOPS_USER:-opsuser}\"",
            $"echo \"# dry plan for user=$USERNAME group={group}\"",
            "echo \"id \\\"$USERNAME\\\" || useradd -m -s /bin/bash \\\"$USERNAME\\\"\"",
            $"echo \"usermod -aG {group} \\\"$USERNAME\\\"\"",
            "echo \"# set password interactively on host; not automated here\"",
            "id \"$USERNAME\" 2>/dev/null || echo \"(user not present yet)\"",
            $"getent group {group} || true"
        });
    }
}
