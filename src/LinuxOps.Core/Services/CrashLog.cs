using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LinuxOps.Services;

/// <summary>Журнал ошибок LinuxOps.crash.log рядом с программой. Окна и перехват ошибок интерфейса — у приложения (настольный: CrashReporter).</summary>
public static class CrashLog
{
    public static string LogPath { get; } = Path.Combine(
        Path.GetDirectoryName(DefaultsStore.DefaultsPath) ?? AppContext.BaseDirectory, "LinuxOps.crash.log");

    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    /// <summary>Последнее нативное исключение (задаёт настольное приложение).</summary>
    public static Func<string?>? NativeInfo { get; set; }

    public static void Write(string what, Exception? ex, bool includeNative = false)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {what}");
            sb.AppendLine($"LinuxOps {(Assembly.GetEntryAssembly() ?? typeof(CrashLog).Assembly).GetName().Version} · {RuntimeInformation.OSDescription} · {Environment.OSVersion.Version} · .NET {Environment.Version}");
            if (includeNative && NativeInfo?.Invoke() is { } native) sb.AppendLine("Нативное исключение: " + native);
            if (ex is not null) sb.AppendLine(ex.ToString());
            lock (Gate)
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, sb.ToString() + Environment.NewLine);
            }
        }
        catch
        {
            // журнал не записался — молча: ошибка журнала не должна ронять программу
        }
    }

}
