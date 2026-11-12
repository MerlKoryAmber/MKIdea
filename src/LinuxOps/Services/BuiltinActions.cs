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
            TimeoutSeconds = 300,
            Distros = new List<string> { "ubuntu", "debian", "rhel", "alma", "suse" }
        },
        new ActionDefinition
        {
            Id = "install-updates",
            Name = "Установить обновления",
            Description = "Полный upgrade (нужен Применить)",
            BuiltIn = true,
            Destructive = true,
            TimeoutSeconds = 1800,
            Distros = new List<string> { "ubuntu", "debian", "rhel", "alma", "suse" }
        },
        new ActionDefinition
        {
            Id = "list-users",
            Name = "Аудит пользователей / sudo",
            Description = "Учётки, sudo, блокировки, ключи SSH",
            BuiltIn = true,
            Distros = new List<string> { "any" }
        },
        new ActionDefinition
        {
            Id = "proxy-check",
            Name = "Прокси: проверить",
            Description = "Что прописано (apt / dnf / zypper, окружение) и доступен ли репозиторий",
            BuiltIn = true,
            Privilege = "user",
            TimeoutSeconds = 60,
            Distros = new List<string> { "ubuntu", "debian", "rhel", "alma", "suse" }
        },
        new ActionDefinition
        {
            Id = "proxy-set",
            Name = "Прокси: прописать",
            Description = "Для обновлений (apt / dnf / zypper) и при желании для окружения; затем проверка через обновление списков пакетов",
            BuiltIn = true,
            Destructive = true,
            TimeoutSeconds = 300,
            Distros = new List<string> { "ubuntu", "debian", "rhel", "alma", "suse" },
            Parameters = new List<ActionParam>
            {
                new() { Name = "Proxy", Label = "Адрес прокси, например http://proxy.corp.local:3128 (без логина и пароля)", Default = "" },
                new() { Name = "NoProxy", Label = "Исключения через запятую (no_proxy)", Default = "localhost,127.0.0.1" },
                new() { Name = "Env", Label = "Также окружение — curl, wget, скрипты (да / нет)", Default = "да" }
            }
        },
        new ActionDefinition
        {
            Id = "proxy-unset",
            Name = "Прокси: убрать",
            Description = "Убрать то, что прописал LinuxOps; чужие настройки не трогаются",
            BuiltIn = true,
            Destructive = true,
            TimeoutSeconds = 60,
            Distros = new List<string> { "ubuntu", "debian", "rhel", "alma", "suse" }
        },
    };

    /// <summary>Действия, у которых dry-run — тот же скрипт с LINUXOPS_DRY=1: показывает изменения, ничего не пишет.</summary>
    public static bool UsesDryFlag(string id) => id is "proxy-set" or "proxy-unset";

    public static string ResolveScript(string id, DistroInfo distro)
    {
        return id switch
        {
            "check-updates" => CheckUpdates(distro),
            "install-updates" => InstallUpdates(distro),
            "list-users" => ListUsers(distro),
            "proxy-check" => ProxyScripts.Check,
            "proxy-set" => ProxyScripts.Set,
            "proxy-unset" => ProxyScripts.Unset,
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
            apt-get -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold upgrade
            """,
        "suse" => """
            set -e
            zypper --non-interactive update
            """,
            _ => "echo unsupported >&2; exit 3"
    };

    public static bool TrySimulate(string id, DistroInfo distro, out string script)
    {
        script = "";
        if (UsesDryFlag(id))
        {
            script = ResolveScript(id, distro);
            return true;
        }

        if (!string.Equals(id, "install-updates", StringComparison.OrdinalIgnoreCase))
            return false;
        script = SimulateInstall(distro);
        return true;
    }

    private static string SimulateInstall(DistroInfo d) => d.Family switch
    {
        "rhel" => """
            set -e
            if command -v dnf >/dev/null 2>&1; then
              dnf upgrade --assumeno || true
            else
              yum update --assumeno || true
            fi
            """,
        "debian" => """
            set -e
            export DEBIAN_FRONTEND=noninteractive
            apt-get -s upgrade
            """,
        "suse" => """
            set -e
            zypper -n update --dry-run
            """,
        _ => "echo unsupported >&2; exit 3"
    };

    private static string ListUsers(DistroInfo d) => UserAudit.Script;
}
