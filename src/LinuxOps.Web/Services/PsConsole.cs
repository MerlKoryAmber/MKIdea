using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace LinuxOps.Web.Services;

/// <summary>
/// PowerShell на сервере (Ф5): что стоит (pwsh, PowerCLI) и установка PowerCLI в каталог модулей консоли —
/// из PowerShell Gallery (Save-PSResource, с зависимостями) или из ZIP (наш экспорт или офлайн-архив PowerCLI от Broadcom).
/// pwsh запускается от сервиса без профиля, телеметрия выключена.
/// </summary>
public sealed class PsConsole(ILogger<PsConsole> log)
{
    public const string CoreModule = "VMware.VimAutomation.Core";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public sealed record Status(string? Pwsh, string? PowerCli, bool Ready, string Problem);

    /// <summary>pwsh — версия или null; PowerCLI — версия ядра из каталога модулей или null; Ready — консоль можно открыть.</summary>
    public async Task<Status> GetStatus()
    {
        var (code, outText, _) = await Run("$PSVersionTable.PSVersion.ToString()", TimeSpan.FromSeconds(30));
        var pwsh = code == 0 ? outText.Trim() : null;
        var cli = CoreVersion();
        var problem = pwsh is null ? "PowerShell 7 не установлен на сервере (ставится установщиком LinuxOps Web)."
            : !File.Exists(WebPaths.PsKey) ? $"Нет ключа консоли {WebPaths.PsKey} — повторите установку (linuxops-web → update)."
            : "";
        return new Status(pwsh, cli, problem.Length == 0, problem);
    }

    /// <summary>Версия VMware.VimAutomation.Core в каталоге модулей (папка версии с .psd1).</summary>
    public static string? CoreVersion()
    {
        var dir = Path.Combine(WebPaths.PsModules, CoreModule);
        if (!Directory.Exists(dir)) return null;
        var versions = Directory.EnumerateDirectories(dir)
            .Where(v => File.Exists(Path.Combine(v, CoreModule + ".psd1")))
            .Select(Path.GetFileName).OfType<string>().OrderByDescending(v => Version.TryParse(v, out var x) ? x : new Version()).ToList();
        if (versions.Count > 0) return versions[0];
        return File.Exists(Path.Combine(dir, CoreModule + ".psd1")) ? "(без версии)" : null;
    }

    /// <summary>Скачать PowerCLI (ядро vSphere и зависимости) из PowerShell Gallery. proxy — http://host:port или пусто.</summary>
    public async Task<(bool Ok, string Text)> InstallOnline(string proxy)
    {
        if (!await Gate.WaitAsync(0)) return (false, "Установка уже идёт.");
        try
        {
            Directory.CreateDirectory(WebPaths.PsModules);
            var path = WebPaths.PsModules.Replace("'", "''");
            var script = $"$ProgressPreference='SilentlyContinue'; Save-PSResource -Name {CoreModule} -Path '{path}' -Repository PSGallery -TrustRepository -ErrorAction Stop; 'OK'";
            var (code, o, e) = await Run(script, TimeSpan.FromMinutes(15), proxy);
            if (code == 0 && o.Contains("OK")) return (true, $"Установлено: PowerCLI {CoreVersion()}.");
            log.LogWarning("PowerCLI: {Err}", e);
            return (false, "Не удалось: " + FirstLine(e.Length > 0 ? e : o));
        }
        finally { Gate.Release(); }
    }

    /// <summary>Модули из ZIP: папка «Имя» с Имя.psd1 внутри или в подпапке версии.</summary>
    public async Task<(bool Ok, string Text)> InstallZip(Stream zip, long maxBytes)
    {
        if (!await Gate.WaitAsync(0)) return (false, "Установка уже идёт.");
        var temp = Path.Combine(Path.GetTempPath(), "lo-ps-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temp);
            var file = Path.Combine(temp, "m.zip");
            await using (var f = File.Create(file))
            {
                var buf = new byte[81920]; long total = 0; int n;
                while ((n = await zip.ReadAsync(buf)) > 0)
                {
                    total += n;
                    if (total > maxBytes) return (false, $"Архив больше {maxBytes / 1024 / 1024} МБ.");
                    await f.WriteAsync(buf.AsMemory(0, n));
                }
            }
            var dir = Path.Combine(temp, "x");
            try { ZipFile.ExtractToDirectory(file, dir); }
            catch (InvalidDataException) { return (false, "Это не ZIP."); }
            catch (IOException ex) { return (false, "Архив не распаковался: " + ex.Message); }
            var modules = FindModules(dir, 0).ToList();
            if (modules.Count == 0) return (false, "В архиве нет модулей PowerShell (папка «Имя» с Имя.psd1).");
            foreach (var m in modules) CopyDir(m, Path.Combine(WebPaths.PsModules, Path.GetFileName(m)));
            return (true, $"Установлено модулей: {modules.Count}" + (CoreVersion() is { } v ? $", PowerCLI {v}." : ", ядра PowerCLI среди них нет."));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
            Gate.Release();
        }
    }

    /// <summary>Убрать все модули из каталога консоли.</summary>
    public int RemoveAll()
    {
        if (!Directory.Exists(WebPaths.PsModules)) return 0;
        var dirs = Directory.GetDirectories(WebPaths.PsModules);
        foreach (var d in dirs) Directory.Delete(d, true);
        return dirs.Length;
    }

    private static IEnumerable<string> FindModules(string dir, int depth)
    {
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(d);
            var isModule = File.Exists(Path.Combine(d, name + ".psd1")) ||
                           Directory.EnumerateDirectories(d).Any(v => File.Exists(Path.Combine(v, name + ".psd1")));
            if (isModule) yield return d;
            else if (depth < 3)
                foreach (var m in FindModules(d, depth + 1)) yield return m;
        }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static string FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length > 0) ?? "нет ответа";

    /// <summary>pwsh -NoProfile -Command; без телеметрии; прокси — переменными окружения.</summary>
    private static async Task<(int Code, string Out, string Err)> Run(string script, TimeSpan timeout, string proxy = "")
    {
        var psi = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(a);
        psi.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["POWERSHELL_UPDATECHECK"] = "Off";
        if (proxy.Trim().Length > 0) { psi.Environment["HTTPS_PROXY"] = proxy.Trim(); psi.Environment["HTTP_PROXY"] = proxy.Trim(); }
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "", "не уложилось в " + timeout.TotalMinutes + " мин"); }
            return (p.ExitCode, await o, await e);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, "", "pwsh не найден"); }
    }
}
