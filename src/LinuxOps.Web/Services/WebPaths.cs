namespace LinuxOps.Web.Services;

/// <summary>
/// Где сервис хранит данные. Linux: /var/lib/linuxops-web (база, кэш CVE, known_hosts), мастер-ключ — отдельно,
/// /etc/linuxops-web/master.key. Windows (разработка): data\ и data-key\ рядом с программой.
/// Переопределяется переменными LINUXOPS_WEB_DATA и LINUXOPS_WEB_KEY.
/// </summary>
public static class WebPaths
{
    public static string DataDir { get; } = Resolve("LINUXOPS_WEB_DATA",
        OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data") : "/var/lib/linuxops-web");

    public static string KeyFile { get; } = Resolve("LINUXOPS_WEB_KEY",
        OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data-key", "master.key") : "/etc/linuxops-web/master.key");

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
