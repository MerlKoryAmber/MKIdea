namespace LinuxOps.Web.Services;

/// <summary>
/// Где сервис хранит данные. Linux: /var/lib/linuxops-web (база, кэш CVE, known_hosts), мастер-ключ — отдельно,
/// /etc/linuxops-web/master.key. Windows (разработка): data\ и data-key\ рядом с программой.
/// Переопределяется переменными LINUXOPS_WEB_DATA и LINUXOPS_WEB_KEY.
/// </summary>
public static class WebPaths
{
    /// <summary>Свои стили и скрипты — с версией в адресе: после обновления браузер не возьмёт старые из кэша.</summary>
    public static string Asset(string path) => path + "?v=" + (typeof(WebPaths).Assembly.GetName().Version?.ToString() ?? "0");

    public static string DataDir { get; } = Resolve("LINUXOPS_WEB_DATA",
        OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data") : "/var/lib/linuxops-web");

    public static string KeyFile { get; } = Resolve("LINUXOPS_WEB_KEY",
        OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data-key", "master.key") : "/etc/linuxops-web/master.key");

    /// <summary>
    /// Записи сеансов guacd. Пишет сам guacd (пользователь guacd), сервис читает через группу и удаляет старые (RecordingsKeeper):
    /// каталог guacd:linuxops-web 2770 (ставит install.sh). Отдельно от DataDir — тот закрыт 0700.
    /// </summary>
    public static string RecordingsDir { get; } = Resolve("LINUXOPS_WEB_RECORDINGS",
        OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data", "recordings") : "/var/lib/linuxops-web-rec");

    /// <summary>guacd: адрес и порт (по умолчанию — на этом же сервере).</summary>
    public static string Guacd { get; } = Resolve("LINUXOPS_WEB_GUACD", "localhost:4822");

    /// <summary>
    /// Консоль PowerShell (Ф5): учётка ОС с оболочкой pwsh, вход guacd по SSH на этот сервер ключом сервиса;
    /// модули (PowerCLI) — в каталоге, который пишет сервис и читает учётка. Всё создаёт install.sh (ps_account_ensure).
    /// На сервере пути закреплены установщиком (профиль консоли, ReadWritePaths юнита) — переменные окружения только для разработки.
    /// </summary>
    public static string PsUser { get; } = Resolve("LINUXOPS_WEB_PS_USER", "linuxops-ps");
    public static string PsKey { get; } = Resolve("LINUXOPS_WEB_PS_KEY", "/etc/linuxops-web/ps_ed25519");
    public static string PsModules { get; } = Resolve("LINUXOPS_WEB_PS_MODULES", "/var/lib/linuxops-web-ps/Modules");
    public static int PsSshPort { get; } = int.TryParse(Resolve("LINUXOPS_WEB_PS_SSH_PORT", "22"), out var p) ? p : 22;

    public static string DbFile => Path.Combine(DataDir, "linuxops-web.db");
    public static string AuditFile => Path.Combine(DataDir, "audit.log");

    private static string Resolve(string env, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(env);
        return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    }

    /// <summary>До первого обращения к ядру: его файлы (known_hosts, кэш CVE, свои действия, журнал ошибок) — в каталог данных.</summary>
    public static void Prepare()
    {
        Directory.CreateDirectory(DataDir);
        // база, known_hosts и журнал аудита — только владельцу сервиса
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("LINUXOPS_DATA_DIR", DataDir);
    }
}
