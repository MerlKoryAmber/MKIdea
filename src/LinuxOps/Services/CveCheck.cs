using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinuxOps.Services;

/// <summary>Незакрытая CVE продукта, которую надо закрыть сейчас (Level = now).</summary>
public sealed record CveHit(string Id, double Score, bool Network, bool Kev, double? Epss, string Level, string Fix, string Title)
{
    public string ScoreText => Score > 0 ? Score.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    /// <summary>Одна строка: CVE-… · 8.1 · атакуют · исправления нет.</summary>
    public string Line
    {
        get
        {
            var parts = new List<string> { Id, ScoreText };
            if (Kev) parts.Add("атакуют");
            else if (Epss is { } e && e >= CveCheck.EpssNow) parts.Add($"атака {e * 100:0}%");
            if (Fix.Length > 0) parts.Add(Fix);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Сверка «Версий ПО» с базами уязвимостей — с ПК оператора (через его системный прокси), на хосты ничего не ставится.
/// Пакеты RHEL / CentOS (в т. ч. Stream) / Rocky / Alma / Oracle — база Red Hat: исправления переносятся в старые номера,
/// поэтому сравнивается сборка rpm с той, где Red Hat закрыл CVE. Сборки разработчика (контейнеры, nginx.org, вручную) —
/// NVD по номеру версии. Срочность — CISA KEV (атакуют на практике) и EPSS (вероятность атаки за 30 дней).
/// Наружу уходят только имена пакетов, номера версий и CVE; адреса и имена хостов — нет. Ответы хранятся сутки.
/// </summary>
public static partial class CveCheck
{
    /// <summary>Параметр-галка действия «Версии ПО». По умолчанию «да».</summary>
    public const string Param = "UPD_CVE";

    public const double EpssNow = 0.10;
    public const double ScoreNow = 9.0;

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);

    public static string CacheDir { get; } = Path.Combine(
        Path.GetDirectoryName(DefaultsStore.DefaultsPath) ?? AppContext.BaseDirectory, "LinuxOps.cve-cache");

    /// <summary>Ключ NVD (50 запросов за 30 с вместо 5): переменная LINUXOPS_NVD_API_KEY или файл LinuxOps.nvd-key.txt рядом с exe.</summary>
    private static string NvdKey
    {
        get
        {
            var k = Environment.GetEnvironmentVariable("LINUXOPS_NVD_API_KEY");
            if (!string.IsNullOrWhiteSpace(k)) return k.Trim();
            try
            {
                var f = Path.Combine(Path.GetDirectoryName(DefaultsStore.DefaultsPath) ?? AppContext.BaseDirectory, "LinuxOps.nvd-key.txt");
                return File.Exists(f) ? File.ReadAllText(f).Trim() : "";
            }
            catch { return ""; }
        }
    }

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxOps-CveCheck/1.0");
        return c;
    }

    // ---------- точка входа ----------

    /// <summary>Сверяет все строки отчёта; блокирует поток (PlanRunner работает в фоновом).</summary>
    public static void Run(VersionReport r, CancellationToken ct) => RunAsync(r, ct).GetAwaiter().GetResult();

    public static async Task RunAsync(VersionReport r, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Budget);
        var ctx = new Ctx(cts.Token);
        var started = DateTime.Now;
        try
        {
            var kev = await Kev(ctx);
            // одинаковые продукты (php и php-fpm из одного srpm, один образ в нескольких контейнерах) — один разбор
            var memo = new Dictionary<string, Task<Eval>>(StringComparer.Ordinal);
            foreach (var item in r.Items)
            {
                var plan = Plan(r, item);
                item.CveSource = plan.Source;
                if (plan.Kind == "none")
                {
                    item.SetCve("none", new List<CveHit>(), plan.Why);
                    continue;
                }
                if (!memo.TryGetValue(plan.Key, out var task)) memo[plan.Key] = task = Evaluate(plan, item, kev, ctx);
                try
                {
                    var ev = await task;
                    var hits = ev.Hits.Select(h => h with { Fix = WithRepoUpdate(h, ev, item) }).ToList();
                    item.SetCve(ev.Error.Length > 0 && hits.Count == 0 ? "none" : hits.Count == 0 ? "ok" : "now",
                        hits, ev.Error);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    item.SetCve("none", new List<CveHit>(), $"не уложились в {Budget.TotalMinutes:0} мин");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Errors.Add($"не уложились в {Budget.TotalMinutes:0} мин");
            foreach (var i in r.Items.Where(i => i.CveLevel.Length == 0)) i.SetCve("none", new List<CveHit>(), "не уложились по времени");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Errors.Add(ex.Message);
            CrashLog.Write("Сверка CVE", ex);
            foreach (var i in r.Items.Where(i => i.CveLevel.Length == 0)) i.SetCve("none", new List<CveHit>(), ex.Message);
        }

        r.CveDone = true;
        r.CveErrors = string.Join("; ", ctx.Errors.Distinct());
        r.CveStale = ctx.Stale.Count > 0 ? ctx.Stale.Min() : null;
        r.CveSeconds = (int)(DateTime.Now - started).TotalSeconds;
    }

    // ---------- как сверять строку ----------

    private sealed record PlanInfo(string Kind, string Key, string Source, string Why, string Src = "", int El = 0,
        Evr Installed = default, string Product = "", string Version = "");

    private static readonly string[] RhVendors = { "Red Hat", "CentOS", "Rocky", "AlmaLinux", "Oracle", "CloudLinux" };

    private static PlanInfo Plan(VersionReport r, VersionItem i)
    {
        if (i.FromPackage && i.SourcePackage.EndsWith(".src.rpm", StringComparison.Ordinal))
        {
            var el = ElMajor(i.PackageVersion);
            var rhVendor = RhVendors.Any(v => i.Vendor.Contains(v, StringComparison.OrdinalIgnoreCase));
            if (rhVendor && el > 0)
            {
                if (i.PackageVersion.Contains("module+", StringComparison.Ordinal))
                    return new PlanInfo("none", "", "", "пакет из модуля dnf — сверка с базой Red Hat для модулей не сделана");
                var src = SrpmName(i.SourcePackage);
                var inst = Evr.ParseFull(i.PackageVersion) with { Epoch = 0 }; // эпоха у Stream и RHEL расходится (nginx 2: и 1:) — сравниваем версию и сборку
                return new PlanInfo("rh", $"rh|{src}|{el}|{inst}", $"база Red Hat (RHEL {el}), исходный пакет {src}", "", src, el, inst);
            }
            if (rhVendor || el == 0 && i.Vendor.Length > 0 && !LooksUpstreamVendor(i.Vendor))
                return new PlanInfo("none", "", "", $"пакет дистрибутива {OsText(r, i)} — база уязвимостей для него не подключена");
            // nginx.org, remi и т. п. — версия разработчика
        }
        else if (i.FromPackage && i.PackageVersion.Length > 0 && DebianBackport().IsMatch(i.PackageVersion))
        {
            return new PlanInfo("none", "", "",
                $"пакет {OsText(r, i)} ({i.PackageVersion}) — исправления переносятся без смены версии, база Debian / Ubuntu пока не подключена");
        }

        if (!NvdCpes.ContainsKey(i.Product))
            return new PlanInfo("none", "", "", "нет соответствия в NVD");
        var v = NvdVersion(i.Product, i.Version);
        if (v.Length == 0) return new PlanInfo("none", "", "", "версия не распознана");
        var p = i.Product == "php-fpm" ? "php" : i.Product;
        return new PlanInfo("nvd", $"nvd|{p}|{v}", $"NVD по версии разработчика {i.Version}", "", Product: p, Version: v);
    }

    private static bool LooksUpstreamVendor(string vendor) =>
        vendor.Contains("nginx", StringComparison.OrdinalIgnoreCase) || vendor.Contains("Remi", StringComparison.OrdinalIgnoreCase)
        || vendor.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase) || vendor.Contains("MariaDB", StringComparison.OrdinalIgnoreCase)
        || vendor.Contains("MySQL", StringComparison.OrdinalIgnoreCase) || vendor.Contains("Oracle and/or", StringComparison.OrdinalIgnoreCase)
        || vendor.Contains("NodeSource", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(deb\d|ubuntu)", RegexOptions.CultureInvariant)]
    private static partial Regex DebianBackport();

    private static string OsText(VersionReport r, VersionItem i) =>
        r.Os.TryGetValue(i.Container, out var os) && os.Length > 0 ? os : "дистрибутива";

    // ---------- вычисление ----------

    private sealed record Eval(List<CveHit> Hits, string Error, Dictionary<string, Evr> Fixes);

    private static async Task<Eval> Evaluate(PlanInfo p, VersionItem item, HashSet<string> kev, Ctx ctx)
    {
        var open = new List<(string Id, double Score, bool Net, string Title, string Fix, Evr FixEvr)>();
        string error;
        if (p.Kind == "rh") error = await RedHat(p, open, ctx);
        else error = await Nvd(p, open, ctx);

        var epss = await Epss(open.Where(o => !kev.Contains(o.Id) && !(o.Net && o.Score >= ScoreNow)).Select(o => o.Id), ctx);
        var hits = new List<CveHit>();
        var fixes = new Dictionary<string, Evr>(StringComparer.Ordinal);
        foreach (var o in open)
        {
            var inKev = kev.Contains(o.Id);
            double? e = epss.TryGetValue(o.Id, out var ev) ? ev : null;
            var level = inKev || e >= EpssNow || (o.Net && o.Score >= ScoreNow) ? "now"
                      : "";
            if (level.Length == 0) continue;
            hits.Add(new CveHit(o.Id, o.Score, o.Net, inKev, e, level, o.Fix, o.Title));
            if (o.FixEvr.Version is { Length: > 0 }) fixes[o.Id] = o.FixEvr;
        }
        hits = hits.OrderBy(h => h.Level == "now" ? 0 : 1).ThenByDescending(h => h.Kev).ThenByDescending(h => h.Epss ?? 0)
                   .ThenByDescending(h => h.Score).ThenByDescending(h => h.Id, StringComparer.Ordinal).ToList();
        return new Eval(hits, error, fixes);
    }

    /// <summary>Закроет ли CVE обновление, которое уже предлагает репозиторий хоста.</summary>
    private static string WithRepoUpdate(CveHit h, Eval ev, VersionItem item)
    {
        if (!ev.Fixes.TryGetValue(h.Id, out var fix) || item.Container.Length > 0) return h.Fix;
        if (item.Update.Length == 0) return h.Fix + ", в репо хоста нет";
        var upd = Evr.ParseFull(item.Update) with { Epoch = 0 };
        return Evr.Compare(upd, fix) >= 0
            ? h.Fix + $", закроет обновление {item.Update}"
            : h.Fix + ", в репо хоста нет";
    }

    // ---------- Red Hat ----------

    private static async Task<string> RedHat(PlanInfo p, List<(string, double, bool, string, string, Evr)> open, Ctx ctx)
    {
        var list = new List<JsonElement>();
        for (var page = 1; page <= 10; page++)
        {
            var url = $"https://access.redhat.com/hydra/rest/securitydata/cve.json?package={Uri.EscapeDataString(p.Src)}&per_page=1000&page={page}";
            var f = await Get($"rh-{p.Src}-{page}", url, ctx);
            if (f.Body is null) return list.Count == 0 ? $"база Red Hat недоступна: {f.Error}" : "";
            using var doc = JsonDocument.Parse(f.Body);
            var n = 0;
            foreach (var e in doc.RootElement.EnumerateArray()) { list.Add(e.Clone()); n++; }
            if (n < 1000) break;
        }

        // CVE без исправления для этой версии RHEL — уточнить по карточке CVE (не затронуто / не будет исправлено…)
        var kevSet = await Kev(ctx);
        var unclear = new List<(string Id, double Score, bool Net, string Title)>();
        foreach (var e in list)
        {
            var id = Str(e, "CVE");
            if (id.Length == 0) continue;
            var (score, net) = RhScore(e);
            var title = RhTitle(Str(e, "bugzilla_description"), p.Src);
            var fixes = new List<Evr>();
            var fixedInOlderEl = false;
            if (e.TryGetProperty("affected_packages", out var ap) && ap.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in ap.EnumerateArray())
                {
                    if (Nevra.TryParse(s.GetString() ?? "", out var nv) && nv.Name == p.Src)
                    {
                        var el = ElMajor(nv.Evr.Release);
                        if (el == p.El) fixes.Add(nv.Evr with { Epoch = 0 });
                        else if (el > 0 && el < p.El) fixedInOlderEl = true;
                    }
                }
            }

            if (fixes.Count > 0)
            {
                var max = fixes.Aggregate((a, b) => Evr.Compare(a, b) >= 0 ? a : b);
                if (Evr.Compare(p.Installed, max) < 0)
                    open.Add((id, score, net, title, $"есть в RHEL {max.Text}", max));
                continue;
            }

            // Опубликована до выхода этой RHEL, а исправления для неё нет — эта вышла уже с исправлением (или её не касается).
            // Исключение — атакуемые (KEV): по ним карточку смотрим всегда.
            if (DateTime.TryParse(Str(e, "public_date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var pub)
                && ElGa.TryGetValue(p.El, out var ga) && pub < ga && (fixedInOlderEl || !kevSet.Contains(id)))
                continue;
            unclear.Add((id, score, net, title));
        }

        if (unclear.Count == 0) return "";
        // карточку смотрим только у тех, что по оценке, KEV или EPSS попали бы в «сейчас» / «в план»
        var kev = kevSet;
        var epss = await Epss(unclear.Where(u => !kev.Contains(u.Id) && !(u.Net && u.Score >= ScoreNow)).Select(u => u.Id), ctx);
        var need = unclear.Where(u => kev.Contains(u.Id) || (u.Net && u.Score >= ScoreNow) || (epss.TryGetValue(u.Id, out var x) && x >= EpssNow)).ToList();
        using var gate = new SemaphoreSlim(4);
        var details = await Task.WhenAll(need.Select(async u =>
        {
            await gate.WaitAsync(ctx.Token);
            try { return (u, f: await Get($"rhcve-{u.Id}", $"https://access.redhat.com/hydra/rest/securitydata/cve/{u.Id}.json", ctx)); }
            finally { gate.Release(); }
        }));
        foreach (var (u, f) in details)
        {
            if (f.Body is null) { open.Add((u.Id, u.Score, u.Net, u.Title, "нет ответа Red Hat — проверить вручную", default)); continue; }
            using var doc = JsonDocument.Parse(f.Body);
            var root = doc.RootElement;
            var product = $"Red Hat Enterprise Linux {p.El}";
            // исправление в карточке (в списке бывает не всё)
            var fixes = new List<Evr>();
            if (root.TryGetProperty("affected_release", out var ar) && ar.ValueKind == JsonValueKind.Array)
                foreach (var rel in ar.EnumerateArray())
                    if (Str(rel, "product_name").StartsWith(product, StringComparison.Ordinal)
                        && Nevra.TryParse(Str(rel, "package"), out var nv) && nv.Name == p.Src && ElMajor(nv.Evr.Release) == p.El)
                        fixes.Add(nv.Evr with { Epoch = 0 });
            if (fixes.Count > 0)
            {
                var max = fixes.Aggregate((a, b) => Evr.Compare(a, b) >= 0 ? a : b);
                if (Evr.Compare(p.Installed, max) < 0) open.Add((u.Id, u.Score, u.Net, u.Title, $"есть в RHEL {max.Text}", max));
                continue;
            }
            string? state = null;
            if (root.TryGetProperty("package_state", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var s in ps.EnumerateArray())
                {
                    var pn = Str(s, "package_name");
                    if (Str(s, "product_name") == product && (pn == p.Src || pn.StartsWith(p.Src + ":", StringComparison.Ordinal)))
                    {
                        state = Str(s, "fix_state");
                        break;
                    }
                }
            var fixText = state switch
            {
                null or "Not affected" or "" => null,
                "Affected" => "исправления нет",
                "Fix deferred" => "исправление отложено",
                "Will not fix" => "Red Hat не будет исправлять",
                "Under investigation" => "исправления нет (изучается)",
                "Out of support scope" => "вне поддержки Red Hat",
                _ => $"Red Hat: {state}"
            };
            if (fixText is not null) open.Add((u.Id, u.Score, u.Net, u.Title, fixText, default));
        }
        return "";
    }

    /// <summary>Выход RHEL: CVE, опубликованная раньше и исправленная в прошлых версиях, в этой уже закрыта.</summary>
    private static readonly Dictionary<int, DateTime> ElGa = new()
    {
        [6] = new DateTime(2010, 11, 10), [7] = new DateTime(2014, 6, 10), [8] = new DateTime(2019, 5, 7),
        [9] = new DateTime(2022, 5, 17), [10] = new DateTime(2025, 5, 20)
    };

    private static (double Score, bool Net) RhScore(JsonElement e)
    {
        var s = Str(e, "cvss3_score");
        var v = Str(e, "cvss3_scoring_vector");
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var score);
        return (score, v.Contains("AV:N", StringComparison.Ordinal));
    }

    private static string RhTitle(string d, string src)
    {
        // «openssh: openssh: текст» → «текст»
        var t = d;
        for (var k = 0; k < 2; k++)
        {
            var c = t.IndexOf(": ", StringComparison.Ordinal);
            if (c > 0 && c < 40 && !t[..c].Contains(' ')) t = t[(c + 2)..];
        }
        return Trim(t);
    }

    // ---------- NVD ----------

    private static readonly Dictionary<string, string[]> NvdCpes = new()
    {
        ["nginx"] = new[] { "f5:nginx", "f5:nginx_open_source", "nginx:nginx" },
        ["apache"] = new[] { "apache:http_server" },
        ["php"] = new[] { "php:php" },
        ["php-fpm"] = new[] { "php:php" },
        ["openssl"] = new[] { "openssl:openssl" },
        ["openssh"] = new[] { "openbsd:openssh" },
        ["mysql"] = new[] { "oracle:mysql", "oracle:mysql_server" },
        ["mariadb"] = new[] { "mariadb:mariadb" },
        ["postgresql"] = new[] { "postgresql:postgresql" },
        ["redis"] = new[] { "redis:redis" },
        ["node"] = new[] { "nodejs:node.js" },
        ["java"] = new[] { "oracle:openjdk" },
        ["python"] = new[] { "python:python" }
    };

    /// <summary>Версия в виде CPE: OpenSSH 9.6p1 → 9.6:p1, Java 1.8.0_392 → 1.8.0:update392.</summary>
    private static string NvdVersion(string product, string v)
    {
        v = v.Trim();
        if (v.Length == 0 || !char.IsDigit(v[0])) return "";
        if (product == "openssh")
        {
            var m = Regex.Match(v, @"^(\d+\.\d+)(p\d+)?");
            return m.Success ? m.Groups[1].Value + (m.Groups[2].Success ? ":" + m.Groups[2].Value : "") : "";
        }
        if (product == "java")
        {
            var m = Regex.Match(v, @"^1\.8\.0_(\d+)");
            if (m.Success) return "1.8.0:update" + m.Groups[1].Value;
        }
        return Regex.Match(v, @"^[0-9][0-9A-Za-z.]*").Value.TrimEnd('.');
    }

    private static async Task<string> Nvd(PlanInfo p, List<(string, double, bool, string, string, Evr)> open, Ctx ctx)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plainVersion = p.Version.Split(':')[0];
        var errors = new List<string>();
        foreach (var cpe in NvdCpes[p.Product])
        {
            var url = "https://services.nvd.nist.gov/rest/json/cves/2.0?virtualMatchString="
                      + Uri.EscapeDataString($"cpe:2.3:a:{cpe}:{p.Version}") + "&resultsPerPage=2000";
            var f = await Get($"nvd-{cpe.Replace(':', '_')}-{p.Version.Replace(':', '_')}", url, ctx, nvd: true);
            if (f.Body is null) { errors.Add(f.Error); continue; }
            using var doc = JsonDocument.Parse(f.Body);
            if (!doc.RootElement.TryGetProperty("vulnerabilities", out var vs)) continue;
            var product = cpe.Split(':')[1];
            foreach (var v in vs.EnumerateArray())
            {
                if (!v.TryGetProperty("cve", out var c)) continue;
                var id = Str(c, "id");
                if (id.Length == 0 || !seen.Add(id) || Str(c, "vulnStatus") == "Rejected") continue;
                var (score, net) = NvdScore(c);
                var title = "";
                if (c.TryGetProperty("descriptions", out var ds))
                    foreach (var d in ds.EnumerateArray())
                        if (Str(d, "lang") == "en") { title = Trim(Str(d, "value")); break; }
                // CVE чужого продукта, где наш только «среда» (Odoo «на Python») — не наша
                if (!VulnerableFor(c, cpe)) continue;
                var fixIn = NvdFixedIn(c, product, plainVersion);
                open.Add((id, score, net, title, fixIn.Length > 0 ? $"исправлено в {fixIn}" : "исправления нет", default));
            }
        }
        return errors.Count == NvdCpes[p.Product].Length && errors.Count > 0 ? $"NVD недоступна: {errors[0]}" : "";
    }

    private static (double Score, bool Net) NvdScore(JsonElement c)
    {
        if (!c.TryGetProperty("metrics", out var m)) return (0, false);
        foreach (var key in new[] { "cvssMetricV31", "cvssMetricV30", "cvssMetricV40" })
        {
            if (!m.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            JsonElement? pick = null;
            foreach (var x in arr.EnumerateArray())
            {
                if (pick is null || Str(x, "type") == "Primary") pick = x;
                if (Str(x, "type") == "Primary") break;
            }
            if (pick is { } px && px.TryGetProperty("cvssData", out var cd))
            {
                var score = cd.TryGetProperty("baseScore", out var bs) && bs.TryGetDouble(out var d) ? d : 0;
                return (score, Str(cd, "vectorString").Contains("AV:N", StringComparison.Ordinal));
            }
        }
        return (0, false);
    }

    /// <summary>Уязвим именно этот продукт (vulnerable = true), а не другой, работающий на нём.</summary>
    private static bool VulnerableFor(JsonElement c, string vendorProduct)
    {
        if (!c.TryGetProperty("configurations", out var cfgs)) return true;
        foreach (var cfg in cfgs.EnumerateArray())
        {
            if (!cfg.TryGetProperty("nodes", out var nodes)) continue;
            foreach (var n in nodes.EnumerateArray())
            {
                if (!n.TryGetProperty("cpeMatch", out var ms)) continue;
                foreach (var m in ms.EnumerateArray())
                    if (m.TryGetProperty("vulnerable", out var v) && v.ValueKind == JsonValueKind.True
                        && Str(m, "criteria").Contains($":{vendorProduct}:", StringComparison.Ordinal))
                        return true;
            }
        }
        return false;
    }

    /// <summary>Наименьшая «versionEndExcluding» из диапазонов, куда попадает наша версия, — где разработчик исправил.</summary>
    private static string NvdFixedIn(JsonElement c, string product, string version)
    {
        string? best = null;
        if (!c.TryGetProperty("configurations", out var cfgs)) return "";
        foreach (var cfg in cfgs.EnumerateArray())
        {
            if (!cfg.TryGetProperty("nodes", out var nodes)) continue;
            foreach (var n in nodes.EnumerateArray())
            {
                if (!n.TryGetProperty("cpeMatch", out var ms)) continue;
                foreach (var m in ms.EnumerateArray())
                {
                    if (!Str(m, "criteria").Contains($":{product}:", StringComparison.Ordinal)) continue;
                    var end = Str(m, "versionEndExcluding");
                    if (end.Length == 0 || Rpm.VerCmp(version, end) >= 0) continue;
                    var start = Str(m, "versionStartIncluding");
                    if (start.Length > 0 && Rpm.VerCmp(version, start) < 0) continue;
                    if (best is null || Rpm.VerCmp(end, best) < 0) best = end;
                }
            }
        }
        return best ?? "";
    }

    // ---------- KEV и EPSS ----------

    private static async Task<HashSet<string>> Kev(Ctx ctx)
    {
        if (ctx.KevSet is not null) return ctx.KevSet;
        var set = new HashSet<string>(StringComparer.Ordinal);
        var f = await Get("kev", "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json", ctx);
        if (f.Body is null) ctx.Errors.Add($"список CISA KEV не получен ({f.Error}) — срочность только по оценке и EPSS");
        else
        {
            using var doc = JsonDocument.Parse(f.Body);
            if (doc.RootElement.TryGetProperty("vulnerabilities", out var vs))
                foreach (var v in vs.EnumerateArray())
                    if (Str(v, "cveID") is { Length: > 0 } id) set.Add(id);
        }
        return ctx.KevSet = set;
    }

    private static readonly ConcurrentDictionary<string, (DateTime At, double Value)> EpssMem = new(StringComparer.Ordinal);

    private static async Task<Dictionary<string, double>> Epss(IEnumerable<string> ids, Ctx ctx)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var need = new List<string>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (EpssMem.TryGetValue(id, out var v) && DateTime.UtcNow - v.At < Ttl) result[id] = v.Value;
            else need.Add(id);
        }
        foreach (var chunk in need.Chunk(60))
        {
            var url = "https://api.first.org/data/v1/epss?cve=" + string.Join(",", chunk) + "&limit=100";
            try
            {
                using var resp = await Http.GetAsync(url, ctx.Token);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ctx.Token));
                foreach (var d in doc.RootElement.GetProperty("data").EnumerateArray())
                {
                    if (double.TryParse(Str(d, "epss"), NumberStyles.Float, CultureInfo.InvariantCulture, out var e))
                    {
                        var id = Str(d, "cve");
                        result[id] = e;
                        EpssMem[id] = (DateTime.UtcNow, e);
                    }
                }
                // нет в EPSS — 0, чтобы не спрашивать снова
                foreach (var id in chunk.Where(c => !result.ContainsKey(c))) EpssMem[id] = (DateTime.UtcNow, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ctx.Token.IsCancellationRequested)
            {
                ctx.Errors.Add($"EPSS не получен ({Reason(ex)}) — срочность по KEV и оценке");
                break;
            }
        }
        return result;
    }

    // ---------- загрузка с кэшем ----------

    private sealed class Ctx(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public ConcurrentBag<string> Errors { get; } = new();
        public ConcurrentBag<DateTime> Stale { get; } = new();
        public HashSet<string>? KevSet { get; set; }
    }

    private sealed record Fetched(string? Body, string Error);

    private static readonly SemaphoreSlim NvdGate = new(1);
    private static DateTime _nvdLast = DateTime.MinValue;

    /// <summary>Ответ из кэша (не старше суток) или из сети; сеть не ответила — устаревший кэш с пометкой.</summary>
    private static async Task<Fetched> Get(string key, string url, Ctx ctx, bool nvd = false)
    {
        var file = Path.Combine(CacheDir, Safe(key) + ".json");
        var fi = new FileInfo(file);
        if (fi.Exists && DateTime.Now - fi.LastWriteTime < Ttl)
        {
            try { return new Fetched(await File.ReadAllTextAsync(file, ctx.Token), ""); } catch (IOException) { }
        }

        string error;
        try
        {
            var body = nvd ? await GetNvd(url, ctx.Token) : await GetText(url, ctx.Token);
            try
            {
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(file, body, ctx.Token);
            }
            catch (IOException) { }
            return new Fetched(body, "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ctx.Token.IsCancellationRequested)
        {
            error = Reason(ex);
        }

        if (fi.Exists)
        {
            ctx.Stale.Add(fi.LastWriteTime);
            try { return new Fetched(await File.ReadAllTextAsync(file, ctx.Token), ""); } catch (IOException) { }
        }
        return new Fetched(null, error);
    }

    private static async Task<string> GetText(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>NVD без ключа — 5 запросов за 30 с: один запрос за раз и пауза; 403/429/503 — повтор после паузы.</summary>
    private static async Task<string> GetNvd(string url, CancellationToken ct)
    {
        var key = NvdKey;
        var gap = TimeSpan.FromMilliseconds(key.Length > 0 ? 700 : 6500);
        await NvdGate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var wait = _nvdLast + gap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _nvdLast = DateTime.UtcNow;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (key.Length > 0) req.Headers.TryAddWithoutValidation("apiKey", key);
                using var resp = await Http.SendAsync(req, ct);
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct);
            }
        }
        finally
        {
            NvdGate.Release();
        }
    }

    private static string Reason(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } s } => $"HTTP {(int)s}",
        HttpRequestException h => h.InnerException?.Message ?? h.Message,
        TaskCanceledException => "нет ответа за 60 с",
        _ => ex.Message
    };

    private static string Safe(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        return sb.ToString();
    }

    // ---------- мелочи ----------

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" }
            : "";

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 140 ? s[..137] + "…" : s;
    }

    /// <summary>Мажор RHEL по сборке: 38.el9_4.1 → 9; не RHEL — 0.</summary>
    public static int ElMajor(string release)
    {
        var m = ElRx().Match(release);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    [GeneratedRegex(@"\.el(\d+)")]
    private static partial Regex ElRx();

    /// <summary>nginx-1.20.1-32.el9.src.rpm → nginx.</summary>
    public static string SrpmName(string srpm)
    {
        var s = srpm.EndsWith(".src.rpm", StringComparison.Ordinal) ? srpm[..^8] : srpm;
        for (var k = 0; k < 2; k++)
        {
            var d = s.LastIndexOf('-');
            if (d <= 0) break;
            s = s[..d];
        }
        return s;
    }
}

/// <summary>Эпоха, версия, сборка rpm.</summary>
public readonly record struct Evr(int Epoch, string Version, string Release)
{
    public string Text => (Epoch > 0 ? $"{Epoch}:" : "") + Version + (Release.Length > 0 ? "-" + Release : "");
    public override string ToString() => Text;

    /// <summary>Эпоха отдельно («(none)» — 0), «версия-сборка».</summary>
    public static Evr Parse(string epoch, string versionRelease)
    {
        var e = int.TryParse(epoch, out var x) ? x : 0;
        var full = ParseFull(versionRelease);
        return full with { Epoch = Math.Max(e, full.Epoch) };
    }

    /// <summary>«[эпоха:]версия[-сборка]».</summary>
    public static Evr ParseFull(string s)
    {
        s = s.Trim();
        var e = 0;
        var c = s.IndexOf(':');
        if (c > 0 && int.TryParse(s[..c], out var x)) { e = x; s = s[(c + 1)..]; }
        var d = s.LastIndexOf('-');
        return d > 0 ? new Evr(e, s[..d], s[(d + 1)..]) : new Evr(e, s, "");
    }

    public static int Compare(Evr a, Evr b)
    {
        if (a.Epoch != b.Epoch) return a.Epoch < b.Epoch ? -1 : 1;
        var v = Rpm.VerCmp(a.Version, b.Version);
        if (v != 0) return v;
        if (a.Release.Length == 0 || b.Release.Length == 0) return 0;
        return Rpm.VerCmp(a.Release, b.Release);
    }
}

/// <summary>«имя-[эпоха:]версия-сборка» из базы Red Hat (openssh-0:8.7p1-38.el9_4.1).</summary>
public readonly record struct Nevra(string Name, Evr Evr)
{
    public static bool TryParse(string s, out Nevra n)
    {
        n = default;
        var d1 = s.LastIndexOf('-');
        if (d1 <= 0) return false;
        var d2 = s.LastIndexOf('-', d1 - 1);
        if (d2 <= 0) return false;
        var name = s[..d2];
        var ev = s[(d2 + 1)..d1];
        if (name.Contains('/') || name.Contains(':') || ev.Length == 0 || !char.IsDigit(ev[0])) return false;
        n = new Nevra(name, Evr.ParseFull(ev + "-" + s[(d1 + 1)..]));
        return true;
    }
}

/// <summary>Сравнение версий по правилам rpm (rpmvercmp): цифры — числом, буквы — строкой, «~» — раньше всего.</summary>
public static class Rpm
{
    public static int VerCmp(string a, string b)
    {
        if (a == b) return 0;
        int i = 0, j = 0;
        while (true)
        {
            while (i < a.Length && !IsAlnum(a[i]) && a[i] != '~' && a[i] != '^') i++;
            while (j < b.Length && !IsAlnum(b[j]) && b[j] != '~' && b[j] != '^') j++;

            bool ta = i < a.Length && a[i] == '~', tb = j < b.Length && b[j] == '~';
            if (ta || tb)
            {
                if (!ta) return 1;
                if (!tb) return -1;
                i++; j++;
                continue;
            }
            bool ca = i < a.Length && a[i] == '^', cb = j < b.Length && b[j] == '^';
            if (ca || cb)
            {
                if (i >= a.Length) return -1;
                if (j >= b.Length) return 1;
                if (!ca) return 1;
                if (!cb) return -1;
                i++; j++;
                continue;
            }
            if (i >= a.Length || j >= b.Length) break;

            int si = i, sj = j;
            var num = IsDigit(a[i]);
            if (num)
            {
                while (i < a.Length && IsDigit(a[i])) i++;
                while (j < b.Length && IsDigit(b[j])) j++;
            }
            else
            {
                while (i < a.Length && IsAlpha(a[i])) i++;
                while (j < b.Length && IsAlpha(b[j])) j++;
            }
            var sa = a[si..i];
            var sb = b[sj..j];
            if (sb.Length == 0) return num ? 1 : -1;
            if (num)
            {
                sa = sa.TrimStart('0');
                sb = sb.TrimStart('0');
                if (sa.Length != sb.Length) return sa.Length > sb.Length ? 1 : -1;
            }
            var c = string.CompareOrdinal(sa, sb);
            if (c != 0) return c < 0 ? -1 : 1;
        }
        if (i >= a.Length && j >= b.Length) return 0;
        return i >= a.Length ? -1 : 1;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsAlpha(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsAlnum(char c) => IsDigit(c) || IsAlpha(c);
}
