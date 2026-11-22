using System.Text.Json;
using LinuxOps.Services;

namespace LinuxOps.Web.Services;

/// <summary>Строка сводки: что требует внимания на хосте (CVE «закрыть сейчас», обновления безопасности, ветка без поддержки).</summary>
public sealed record FleetItem(string Where, string Product, string Version, string Update, bool Security, string Cve, string CveIds, string State, string Severity);

/// <summary>Последняя проверка «Версии ПО» хоста — для сводки по парку (Ф2+). Только срочное, полный отчёт — в History.</summary>
public sealed class FleetSnapshot
{
    public string HostId { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTime At { get; set; }
    public string Who { get; set; } = "";
    public string Summary { get; set; } = "";
    public string UpdatesText { get; set; } = "";
    public string CveSummary { get; set; } = "";
    public string Errors { get; set; } = "";
    public int Updates { get; set; }
    public int Security { get; set; }
    public int Urgent { get; set; }
    public int Bad { get; set; }
    public int Warn { get; set; }
    public List<FleetItem> Items { get; set; } = new();

    public static FleetSnapshot From(StoredHost h, VersionReport r, string who) => new()
    {
        HostId = h.Id, Host = h.Config.Title, At = DateTime.Now, Who = who,
        Summary = r.Summary, UpdatesText = r.UpdatesText, CveSummary = r.CveSummary,
        Errors = string.Join("; ", new[] { r.UpdateError, r.CveErrors }.Concat(r.ContainerErrors).Where(e => !string.IsNullOrWhiteSpace(e))),
        Updates = r.Items.Count(i => i.Update.Length > 0),
        Security = r.Items.Count(i => i.SecurityUpdate),
        Urgent = r.CveNowCount,
        Bad = r.BadCount, Warn = r.WarnCount,
        Items = r.Items
            .Where(i => i.CveLevel == "now" || i.SecurityUpdate || i.Severity is "bad" or "warn")
            .OrderByDescending(i => i.CveLevel == "now").ThenByDescending(i => i.Severity == "bad").ThenByDescending(i => i.SecurityUpdate)
            .Select(i => new FleetItem(i.WhereText, i.ProductText, i.Version, i.Update, i.SecurityUpdate, i.CveText,
                string.Join(", ", i.Cves.Select(c => c.Id)), i.StateText, i.CveLevel == "now" ? "bad" : i.Severity))
            .ToList()
    };
}

public sealed partial class WebStore
{
    private bool _fleetReady;

    private void EnsureFleet()
    {
        if (_fleetReady) return;
        using var c = Open();
        Exec(c, "CREATE TABLE IF NOT EXISTS fleet(host_id TEXT PRIMARY KEY, json TEXT NOT NULL)");
        _fleetReady = true;
    }

    public void SaveFleet(FleetSnapshot s)
    {
        EnsureFleet();
        using var c = Open();
        Exec(c, "INSERT INTO fleet(host_id, json) VALUES($id, $j) ON CONFLICT(host_id) DO UPDATE SET json=$j",
            ("$id", s.HostId), ("$j", JsonSerializer.Serialize(s)));
    }

    public List<FleetSnapshot> LoadFleet()
    {
        EnsureFleet();
        var list = new List<FleetSnapshot>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM fleet";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<FleetSnapshot>(r.GetString(0), Json) is { } s) list.Add(s);
        return list;
    }

    public void DeleteFleet(string hostId)
    {
        EnsureFleet();
        using var c = Open();
        Exec(c, "DELETE FROM fleet WHERE host_id=$id", ("$id", hostId));
    }
}
