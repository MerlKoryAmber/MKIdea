namespace LinuxOps.Web.Services;

/// <summary>Хранение записей сеансов: сколько дней (0 — бессрочно), предел объёма в ГБ (0 — без предела), писать ли нажатия клавиш.</summary>
public sealed record RecordingPolicy(int Days, double MaxGb, bool Keys);

/// <summary>Сколько занимают записи: сеансов с записью, байт, самая старая.</summary>
public sealed record RecordingUsage(int Sessions, long Bytes, DateTime? Oldest);

/// <summary>
/// Чистка записей сеансов (каталог WebPaths.RecordingsDir): раз в час и после сохранения настроек.
/// Сначала — старше срока, потом, если каталог больше предела, — самые старые, пока не уложится.
/// Идущий сеанс не трогается. Удалённая запись помечается в журнале сеансов, чистка — в аудите.
/// Файлы сеанса: &lt;id&gt;.guac (экран), у SSH ещё &lt;id&gt;.txt и &lt;id&gt;.txt.timing.
/// </summary>
public sealed class RecordingsKeeper(WebStore store, AuditLog audit, ILogger<RecordingsKeeper> log) : BackgroundService
{
    public const string Who = "хранение записей";
    public const int DefaultDays = 90;
    public const double DefaultMaxGb = 20;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecordingPolicy Load() => new(
        int.TryParse(store.GetSetting("rec.days"), out var d) ? Math.Clamp(d, 0, 3650) : DefaultDays,
        double.TryParse(store.GetSetting("rec.maxgb"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) ? Math.Clamp(g, 0, 100000) : DefaultMaxGb,
        store.GetSetting("rec.keys") == "1");

    public void Save(RecordingPolicy p, string who)
    {
        p = p with { Days = Math.Clamp(p.Days, 0, 3650), MaxGb = Math.Clamp(Math.Round(p.MaxGb, 2), 0, 100000) };
        store.PutSetting("rec.days", p.Days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        store.PutSetting("rec.maxgb", p.MaxGb.ToString(System.Globalization.CultureInfo.InvariantCulture));
        store.PutSetting("rec.keys", p.Keys ? "1" : "0");
        audit.Write(who, "настройки: записи сеансов", Describe(p));
    }

    public static string Describe(RecordingPolicy p) =>
        (p.Days > 0 ? $"хранить {p.Days} дн." : "хранить бессрочно")
        + (p.MaxGb > 0 ? $", не больше {p.MaxGb:0.##} ГБ" : ", без предела объёма")
        + (p.Keys ? ", с нажатиями клавиш" : ", без нажатий клавиш");

    /// <summary>Текущий объём каталога записей.</summary>
    public RecordingUsage Usage()
    {
        var groups = Groups();
        return new(groups.Count, groups.Sum(g => g.Bytes), groups.Count == 0 ? null : groups.Min(g => g.Time));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await CleanAsync(); }
            catch (Exception ex) { log.LogError(ex, "чистка записей сеансов"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Почистить сейчас по текущим настройкам. Итог — строкой (пусто — нечего удалять).</summary>
    public async Task<string> CleanAsync()
    {
        await _gate.WaitAsync();
        try { return await Task.Run(Clean); }
        finally { _gate.Release(); }
    }

    private sealed record Group(string Id, DateTime Time, long Bytes, List<FileInfo> Files);

    /// <summary>Файлы записей по сеансам; время — начало сеанса из журнала, а нет его — время файла.</summary>
    private List<Group> Groups()
    {
        var dir = new DirectoryInfo(WebPaths.RecordingsDir);
        if (!dir.Exists) return [];
        var starts = store.LoadSessions(int.MaxValue).ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);
        return dir.EnumerateFiles()
            .Where(f => f.Name.Length >= 32 && f.Name[..32].All(Uri.IsHexDigit))
            .GroupBy(f => f.Name[..32], StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var files = g.ToList();
                var t = starts.TryGetValue(g.Key, out var s) && DateTime.TryParseExact(s.Start, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var st) ? st : files.Min(f => f.LastWriteTime);
                return new Group(g.Key, t, files.Sum(f => f.Length), files);
            })
            .ToList();
    }

    private string Clean()
    {
        var p = Load();
        var running = store.LoadSessions(int.MaxValue).Where(s => s.Status == "идёт").Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // запись, которую guacd ещё пишет (файл свежий), не трогаем, даже если сеанса нет в журнале
        bool Busy(Group g) => running.Contains(g.Id) || g.Files.Any(f => f.LastWriteTime > DateTime.Now.AddMinutes(-5));

        var groups = Groups().Where(g => !Busy(g)).OrderBy(g => g.Time).ToList();
        var total = Groups().Sum(g => g.Bytes);
        int byAge = 0, bySize = 0;
        long freed = 0;
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var error = "";

        bool Delete(Group g, string note)
        {
            if (failed.Contains(g.Id)) return false;
            try
            {
                foreach (var f in g.Files) f.Delete();
                store.SessionNote(g.Id, note);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("запись {Id}: не удалить — {Msg}", g.Id, ex.Message);
                failed.Add(g.Id);
                error = ex.Message;
                return false;
            }
        }

        if (p.Days > 0)
        {
            var limit = DateTime.Now.AddDays(-p.Days);
            foreach (var g in groups.Where(g => g.Time < limit).ToList())
            {
                if (!Delete(g, $"запись удалена: старше {p.Days} дн.")) continue;
                groups.Remove(g); total -= g.Bytes; freed += g.Bytes; byAge++;
            }
        }
        if (p.MaxGb > 0)
        {
            var max = (long)(p.MaxGb * 1024 * 1024 * 1024);
            foreach (var g in groups.ToList())
            {
                if (total <= max) break;
                if (!Delete(g, $"запись удалена: каталог больше {p.MaxGb:0.##} ГБ")) continue;
                total -= g.Bytes; freed += g.Bytes; bySize++;
            }
        }
        if (byAge + bySize + failed.Count == 0) return "";
        var text = (byAge + bySize > 0 ? $"удалено записей: {byAge + bySize} ({FileSize(freed)})" : "ничего не удалено")
                   + (byAge > 0 ? $", старше срока — {byAge}" : "") + (bySize > 0 ? $", сверх объёма — {bySize}" : "")
                   + (failed.Count > 0 ? $"; не удалось удалить: {failed.Count} — {error}" : "");
        audit.Write(Who, "записи сеансов: чистка", text + "; " + Describe(p));
        return text;
    }

    public static string FileSize(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.##} ГБ",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} МБ",
        >= 1L << 10 => $"{b / 1024.0:0} КБ",
        _ => $"{b} Б"
    };
}
