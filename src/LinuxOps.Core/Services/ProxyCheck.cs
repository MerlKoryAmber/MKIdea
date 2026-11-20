using System.Text;

namespace LinuxOps.Services;

/// <summary>Строка таблицы «Прокси: проверить»: где прописано, что именно и кто прописал.</summary>
public sealed class ProxyRow
{
    public string Where { get; init; } = "";
    public string Value { get; init; } = "";
    /// <summary>lo — LinuxOps, off — отключено LinuxOps (вернётся при «убрать»), over — чужой скрипт перекрыт нашим, other — чужая настройка.</summary>
    public string Owner { get; init; } = "other";

    public string OwnerText => Owner switch
    {
        "lo" => "LinuxOps",
        "off" => "отключено LinuxOps",
        "over" => "перекрыто LinuxOps",
        _ => "чужое"
    };
}

public sealed class ProxyReport
{
    public string Host { get; set; } = "";
    public string PackageManager { get; set; } = "";
    public string Proxy { get; set; } = "";
    public List<ProxyRow> Rows { get; } = new();
    public string RepoUrl { get; set; } = "";
    /// <summary>HTTP-код, «000» — недоступен, «nocurl» — нечем проверить, пусто — репозиторий не найден.</summary>
    public string RepoCode { get; set; } = "";

    public bool? RepoOk => RepoUrl.Length == 0 || RepoCode is "" or "nocurl" ? null : RepoCode != "000";

    public string ProxyText => Proxy.Length > 0 ? Proxy : "не задан";

    public string RepoText => RepoUrl.Length == 0
        ? "репозиторий не найден"
        : RepoCode == "nocurl" ? $"{RepoUrl} — нет curl, не проверено"
        : RepoCode == "000" ? $"{RepoUrl} — недоступен"
        : $"{RepoUrl} — доступен (HTTP {RepoCode})";

    public string Summary => $"прокси {ProxyText} · " + (RepoOk switch
    {
        true => "репозиторий доступен",
        false => "репозиторий недоступен",
        _ => RepoUrl.Length == 0 ? "репозиторий не найден" : "доступность не проверена"
    });

    public string RowsTitle => Rows.Count == 0 ? "Нигде не прописан" : $"Где прописан ({Rows.Count})";
    public string ManagerText => "Менеджер пакетов: " + PackageManager switch { "yum" => "dnf / yum", "none" => "не найден", var x => x };
}

/// <summary>Разбор вывода «Прокси: проверить» (строки с TAB) и текстовый вид.</summary>
public static class ProxyCheck
{
    public const string ActionId = "proxy-check";

    public static ProxyReport? Parse(string stdout)
    {
        ProxyReport? r = null;
        foreach (var raw in stdout.Replace("\r", "").Split('\n'))
        {
            var p = raw.Split('\t');
            switch (p[0])
            {
                case "PM" when p.Length >= 2:
                    r = new ProxyReport { PackageManager = p[1] };
                    break;
                case "R" when r is not null && p.Length >= 4:
                    r.Rows.Add(new ProxyRow { Where = p[1], Value = p[2], Owner = p[3] });
                    break;
                case "CUR" when r is not null && p.Length >= 2:
                    r.Proxy = p[1];
                    break;
                case "REPO" when r is not null && p.Length >= 3:
                    r.RepoUrl = p[1];
                    r.RepoCode = p[2];
                    break;
            }
        }

        return r;
    }

    public static string Format(string host, ProxyReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"host: {host}");
        sb.AppendLine(r.ManagerText);
        sb.AppendLine($"Прокси для обновлений: {r.ProxyText}");
        sb.AppendLine();
        sb.AppendLine(r.RowsTitle + (r.Rows.Count > 0 ? ":" : ""));
        foreach (var row in r.Rows)
            sb.AppendLine($"  [{row.OwnerText}] {row.Where}: {row.Value}");
        sb.AppendLine();
        sb.AppendLine("Репозиторий: " + r.RepoText);
        return sb.ToString();
    }
}
