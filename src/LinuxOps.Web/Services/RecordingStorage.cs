using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinuxOps.Web.Services;

/// <summary>Хранилище записей: что подключено (строка /etc/fstab с пометкой), смонтировано ли, свободное место.</summary>
public sealed record StorageState(string Type, string Source, bool Configured, bool Mounted, long? Free)
{
    public string TypeName => Type switch { "cifs" => "SMB", "nfs" or "nfs4" => "NFS", "xfs" => "диск", _ => Type };
}

/// <summary>Ответ помощника root: успех, текст, данные (список дисков).</summary>
public sealed record StorageReply(bool Ok, string Message, JsonNode? Data);

/// <summary>
/// Записи сеансов на отдельном хранилище (0.7.4). guacd пишет в буфер — <see cref="WebPaths.RecordingsDir"/>;
/// готовые записи служба переносит в <see cref="ArchiveDir"/> (RecordingsKeeper), куда помощник root монтирует SMB, NFS или диск.
/// Монтировать служба не может (нет root, NoNewPrivileges): кладёт запрос в storage/request, его забирает
/// linuxops-web-storage.path → `linuxops-web storage apply` (deploy/lib/storage.sh), ответ — /run/linuxops-web-storage/result (каталог root, служба только читает).
/// </summary>
public static class RecordingStorage
{
    public static string ArchiveDir { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(AppContext.BaseDirectory, "data", "archive") : "/var/lib/linuxops-web-archive";

    private static string Dir => Path.Combine(WebPaths.DataDir, "storage");
    /// <summary>Ответ помощника: каталог root (служба только читает) — ссылкой его не подменить.</summary>
    private const string ResultFile = "/run/linuxops-web-storage/result";
    private const string FstabMark = "# linuxops-web-archive";

    /// <summary>Меньше этого свободного места в буфере — сеанс идёт без записи (с тревогой).</summary>
    public const long MinBufferFree = 1L << 30;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static StorageState State()
    {
        string type = "", source = "";
        try
        {
            if (File.Exists("/etc/fstab"))
                foreach (var l in File.ReadLines("/etc/fstab"))
                {
                    if (!l.Contains(FstabMark, StringComparison.Ordinal) || l.TrimStart().StartsWith('#')) continue;
                    var f = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 3) { source = f[0]; type = f[2]; }
                    // диск — по UUID; показать имя устройства
                    if (source.StartsWith("UUID=", StringComparison.Ordinal)
                        && new FileInfo("/dev/disk/by-uuid/" + source[5..]).LinkTarget is { } dev)
                        source = "/dev/" + Path.GetFileName(dev);
                    break;
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var mounted = Available();
        return new(type, source, source.Length > 0, mounted, mounted ? FreeBytes(ArchiveDir) : null);
    }

    private static readonly object ProbeLock = new();
    private static Task<bool>? _probe;
    private static (DateTime At, bool Ok) _probed;

    /// <summary>
    /// Хранилище смонтировано и отвечает. Сетевое может пропасть: обращение к нему тогда висит —
    /// проверяем в фоне не дольше 5 с, итог помним 30 с; зависшая проверка новую не плодит.
    /// </summary>
    public static bool Available()
    {
        if (!IsMountPoint(ArchiveDir)) return false;
        lock (ProbeLock)
        {
            if (DateTime.UtcNow - _probed.At < TimeSpan.FromSeconds(30)) return _probed.Ok;
            if (_probe is null || _probe.IsCompleted)
                _probe = Task.Run(() => { try { Directory.EnumerateFileSystemEntries(ArchiveDir).Take(1).ToList(); return true; } catch { return false; } });
            var p = _probe;
            var ok = p.Wait(TimeSpan.FromSeconds(5)) && p.Result;
            _probed = (DateTime.UtcNow, ok);
            return ok;
        }
    }

    /// <summary>Точка монтирования — по /proc/self/mountinfo (поле 5), без вызова mountpoint.</summary>
    public static bool IsMountPoint(string path)
    {
        try
        {
            if (!File.Exists("/proc/self/mountinfo")) return false;
            foreach (var l in File.ReadLines("/proc/self/mountinfo"))
            {
                var f = l.Split(' ');
                if (f.Length > 4 && Unescape(f[4]) == path) return true;
            }
        }
        catch (IOException) { }
        return false;
    }

    // mountinfo экранирует пробел и т. п. как \040
    private static string Unescape(string s) => s.Contains('\\')
        ? System.Text.RegularExpressions.Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString())
        : s;

    public static long? FreeBytes(string path)
    {
        try { return Directory.Exists(path) ? new DriveInfo(path).AvailableFreeSpace : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Можно ли писать сеанс: буфер есть и в нём не меньше <see cref="MinBufferFree"/>. Нет — причина.</summary>
    public static string? CannotRecord()
    {
        var dir = WebPaths.RecordingsDir;
        if (!Directory.Exists(dir)) return "нет каталога записей " + dir;
        var free = FreeBytes(dir);
        if (free is { } f && f < MinBufferFree) return $"в буфере записей свободно {RecordingsKeeper.FileSize(f)} (меньше {RecordingsKeeper.FileSize(MinBufferFree)})";
        return null;
    }

    /// <summary>Файл записи: сначала буфер, потом хранилище (если смонтировано).</summary>
    public static string? Find(string name)
    {
        var a = Path.Combine(WebPaths.RecordingsDir, name);
        if (File.Exists(a)) return a;
        if (!OperatingSystem.IsWindows() && !Available()) return null;
        var b = Path.Combine(ArchiveDir, name);
        return File.Exists(b) ? b : null;
    }

    /// <summary>Запрос помощнику root и ожидание ответа. Поля проверяет помощник; здесь — только сборка JSON.</summary>
    public static async Task<StorageReply> RequestAsync(Dictionary<string, string> fields, TimeSpan timeout)
    {
        if (OperatingSystem.IsWindows()) return new(false, "Только на сервере Linux.", null);
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Dir);
            var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var obj = new JsonObject { ["id"] = id };
            foreach (var (k, v) in fields) obj[k] = v;
            // прежний ответ не удалить (каталог root) — отличаем по id
            var result = ResultFile;
            var tmp = Path.Combine(Dir, "request.tmp");
            await using (var fs = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
                await fs.WriteAsync(Encoding.UTF8.GetBytes(obj.ToJsonString()));
            File.Move(tmp, Path.Combine(Dir, "request"), true);

            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(500);
                if (!File.Exists(result)) continue;
                JsonNode? r;
                try { r = JsonNode.Parse(await File.ReadAllTextAsync(result)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }
                if (r?["id"]?.GetValue<string>() != id) continue;
                return new(r["ok"]?.GetValue<bool>() == true, r["message"]?.GetValue<string>() ?? "", r);
            }
            try { File.Delete(Path.Combine(Dir, "request")); } catch (IOException) { }
            return new(false, "Помощник не ответил. Проверьте на сервере: systemctl status linuxops-web-storage.path", null);
        }
        finally { Gate.Release(); }
    }
}
