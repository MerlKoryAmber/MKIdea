using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LinuxOps.Services;

/// <summary>
/// Модули vSphere (PowerCLI) в папке Modules рядом с LinuxOps.exe.
/// Интернет: только нужное для vSphere (VMware.VimAutomation.Core и зависимости) прямо из PowerShell Gallery —
/// в движке нет PowerShellGet. Без интернета: ZIP или папка (наш экспорт или офлайн-архив PowerCLI от Broadcom).
/// </summary>
public static class PowerCliInstaller
{
    /// <summary>PowerCLI 13.3 — линейка под vSphere 8. Другую версию можно поставить из ZIP.</summary>
    public const string CoreModule = "VMware.VimAutomation.Core";
    public const string CoreVersion = "13.3.0.24145081";
    private const string Gallery = "https://www.powershellgallery.com/api/v2/package/";

    public static string ModulesDir => PsEngineClient.UserModules;

    /// <summary>Установленные модули: имя → версии.</summary>
    public static SortedDictionary<string, List<string>> Installed()
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ModulesDir)) return result;
        foreach (var dir in Directory.EnumerateDirectories(ModulesDir))
        {
            var name = Path.GetFileName(dir);
            var versions = Directory.EnumerateDirectories(dir)
                .Where(v => File.Exists(Path.Combine(v, name + ".psd1")))
                .Select(Path.GetFileName).OfType<string>().ToList();
            if (versions.Count == 0 && File.Exists(Path.Combine(dir, name + ".psd1"))) versions.Add("(без версии)");
            if (versions.Count > 0) result[name] = versions;
        }

        return result;
    }

    public static string? CoreInstalled() =>
        Installed().TryGetValue(CoreModule, out var v) ? string.Join(", ", v) : null;

    // ---------- Из интернета ----------

    public static async Task<int> DownloadAsync(IProgress<string> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxOps");
        var queue = new Queue<(string Id, string Version)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((CoreModule, CoreVersion));
        var count = 0;
        while (queue.Count > 0)
        {
            var (id, version) = queue.Dequeue();
            if (!seen.Add(id)) continue;
            progress.Report($"скачивание {id} {version}…");
            var bytes = await http.GetByteArrayAsync(Gallery + id + "/" + version, ct);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            foreach (var dep in Dependencies(zip)) queue.Enqueue(dep);
            ExtractNupkg(zip, Path.Combine(ModulesDir, id, version));
            count++;
        }

        progress.Report($"установлено модулей: {count}");
        return count;
    }

    /// <summary>Зависимости из .nuspec: версия — нижняя граница диапазона «[13.3.0.24145081, )».</summary>
    private static IEnumerable<(string, string)> Dependencies(ZipArchive zip)
    {
        var spec = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (spec is null) yield break;
        using var s = spec.Open();
        var doc = XDocument.Load(s);
        foreach (var d in doc.Descendants().Where(e => e.Name.LocalName == "dependency"))
        {
            var id = (string?)d.Attribute("id");
            var range = (string?)d.Attribute("version") ?? "";
            var version = Regex.Match(range, @"\d+(\.\d+)+").Value;
            if (!string.IsNullOrEmpty(id) && version.Length > 0) yield return (id!, version);
        }
    }

    private static void ExtractNupkg(ZipArchive zip, string target)
    {
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        foreach (var e in zip.Entries)
        {
            var name = Uri.UnescapeDataString(e.FullName);
            // Служебное nupkg — не часть модуля.
            if (name.StartsWith("_rels/") || name.StartsWith("package/") || name == "[Content_Types].xml" ||
                (name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !name.Contains('/')) || name.EndsWith('/'))
                continue;
            var path = Path.GetFullPath(Path.Combine(target, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // путь вне папки модуля
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            e.ExtractToFile(path, overwrite: true);
        }
    }

    // ---------- ZIP или папка ----------

    /// <summary>Скопировать модули из ZIP или папки. Модуль — папка «Имя» с Имя.psd1 внутри или в подпапке версии.</summary>
    public static int InstallFrom(string source, IProgress<string> progress)
    {
        string? temp = null;
        try
        {
            var dir = source;
            if (File.Exists(source))
            {
                temp = Path.Combine(Path.GetTempPath(), "LinuxOps-modules-" + Guid.NewGuid().ToString("N"));
                progress.Report("распаковка…");
                ZipFile.ExtractToDirectory(source, temp);
                dir = temp;
            }

            var modules = FindModules(dir, depth: 0).ToList();
            if (modules.Count == 0)
                throw new InvalidOperationException("В архиве или папке не найдено модулей PowerShell (папка «Имя» с Имя.psd1).");
            foreach (var m in modules)
            {
                var name = Path.GetFileName(m);
                progress.Report("копирование " + name + "…");
                CopyDir(m, Path.Combine(ModulesDir, name));
            }

            progress.Report($"установлено модулей: {modules.Count}");
            return modules.Count;
        }
        finally
        {
            if (temp is not null)
            {
                try { Directory.Delete(temp, recursive: true); }
                catch { /* временная папка — не критично */ }
            }
        }
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
                foreach (var m in FindModules(d, depth + 1))
                    yield return m;
        }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(from))
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Все модули из папки Modules — в один ZIP, чтобы перенести на машину без интернета.</summary>
    public static void ExportZip(string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(ModulesDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }
}
