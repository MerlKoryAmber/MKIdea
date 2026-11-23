using System.Text.Json;

namespace LinuxOps.Services;

/// <summary>
/// Последний выпуск ветки у разработчика (endoflife.date) — для контейнеров и собранного вручную: «ветка поддерживается»
/// ещё не значит «стоит последний выпуск». Пакеты хоста не трогаем (исправления — от дистрибутива), пакеты Debian / Ubuntu
/// с перенесёнными исправлениями — тоже. Ответ хранится сутки, как и базы CVE.
/// </summary>
public static partial class CveCheck
{
    private static readonly Dictionary<string, string> EolSlugs = new(StringComparer.Ordinal)
    {
        ["nginx"] = "nginx", ["php"] = "php", ["php-fpm"] = "php", ["openssl"] = "openssl", ["mysql"] = "mysql",
        ["mariadb"] = "mariadb", ["postgresql"] = "postgresql", ["node"] = "nodejs", ["python"] = "python",
        ["redis"] = "redis", ["apache"] = "apache-http-server"
    };

    private static async Task Releases(VersionReport r, Ctx ctx)
    {
        var items = r.Items.Where(i => EolSlugs.ContainsKey(i.Product)
                                       && !(i.Container.Length == 0 && i.FromPackage && r.PackageManager != "none")
                                       && UpstreamNumbered(i)).ToList();
        var failed = new List<string>();
        foreach (var g in items.GroupBy(i => EolSlugs[i.Product]))
        {
            var f = await Get("eol-" + g.Key, $"https://endoflife.date/api/{g.Key}.json", ctx);
            if (f.Body is null) { failed.Add($"{g.Key} ({f.Error})"); continue; }
            var latest = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var doc = JsonDocument.Parse(f.Body))
                foreach (var c in Arr(doc.RootElement))
                    if (Str(c, "cycle") is { Length: > 0 } cycle && Str(c, "latest") is { Length: > 0 } last) latest[cycle] = last;
            foreach (var i in g)
            {
                var branch = Eol.BranchOf(i.Product == "php-fpm" ? "php" : i.Product, i.Version);
                if (!latest.TryGetValue(branch, out var last) || CompareVersions(last, i.Version) <= 0) continue;
                i.Latest = last;
                i.StateText += $" · есть {last}";
                if (i.Severity is "ok" or "none") i.Severity = "warn";
            }
        }
        if (failed.Count > 0) ctx.Errors.Add("последние выпуски (endoflife.date) не получены: " + string.Join(", ", failed));
    }

    /// <summary>
    /// Номер версии — как у разработчика: собрано вручную / из образа или пакет Alpine (nginx-1.27.5-r1 — apk отдаёт имя с версией). Пакеты RHEL / UBI (.el9)
    /// и Debian / Ubuntu (1.22.1-9) переносят исправления без смены номера — «есть новее» для них было бы ложным.
    /// </summary>
    private static bool UpstreamNumbered(VersionItem i) =>
        !i.FromPackage || AlpineRelease().IsMatch((i.Package + " " + i.PackageVersion).Trim()) || LooksUpstreamVendor(i.Vendor);

    [System.Text.RegularExpressions.GeneratedRegex(@"-r\d+$")]
    private static partial System.Text.RegularExpressions.Regex AlpineRelease();

    /// <summary>Проверка связи (⚙): короткие запросы к NVD, EPSS и endoflife.date тем же клиентом, что и сверка. Итог — текстом.</summary>
    public static async Task<string> Probe()
    {
        var targets = new[]
        {
            ("NVD", "https://services.nvd.nist.gov/rest/json/cves/2.0?cveId=CVE-2021-44228"),
            ("EPSS", "https://api.first.org/data/v1/epss?cve=CVE-2021-44228"),
            ("endoflife.date", "https://endoflife.date/api/nginx.json")
        };
        var parts = await Task.WhenAll(targets.Select(async t =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var body = await GetText(t.Item2, cts.Token);
                return IsJson(body) ? $"{t.Item1} — OK" : $"{t.Item1} — ответ не JSON (страница прокси?)";
            }
            catch (Exception ex) { return $"{t.Item1} — {Reason(ex)}"; }
        }));
        return string.Join("; ", parts);
    }

    /// <summary>По числам частей: 1.30.10 &gt; 1.30.9; хвосты без цифр не считаются.</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.', '-', '_')
            .Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : -1)
            .TakeWhile(n => n >= 0).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (var k = 0; k < Math.Max(x.Length, y.Length); k++)
        {
            var c = (k < x.Length ? x[k] : 0).CompareTo(k < y.Length ? y[k] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
