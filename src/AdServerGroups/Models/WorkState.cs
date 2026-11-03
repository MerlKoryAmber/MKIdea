namespace AdServerGroups.Models;

public sealed class AppWorkState
{
    public int Version { get; set; } = 1;
    public List<ServerWorkRecord> Servers { get; set; } = new();
}

public sealed class ServerWorkRecord
{
    public string Computer { get; set; } = string.Empty;
    public string TargetGroup { get; set; } = string.Empty;
    public bool GroupCreatedInAd { get; set; }
    public bool GroupAddedToLocalAdmins { get; set; }
    public List<string> MigratedPrincipals { get; set; } = new();
    public List<string> RemovedFromLocal { get; set; } = new();
    public string OptionsFingerprint { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public DateTimeOffset AppliedUtc { get; set; }
    public string AppliedBy { get; set; } = string.Empty;

    /// <summary>Состав Administrators на момент последнего успешного apply (для diff).</summary>
    public List<string> AdminsSnapshot { get; set; } = new();

    /// <summary>SHA-256 от нормализованного списка членов Administrators.</summary>
    public string AdminsSnapshotHash { get; set; } = string.Empty;

    public string AppliedLocal => AppliedUtc == default
        ? "—"
        : AppliedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public string StatusSummary =>
        $"{AppliedLocal} · {TargetGroup}" +
        (GroupAddedToLocalAdmins ? " · local" : "") +
        (MigratedPrincipals.Count > 0 ? $" · migrated {MigratedPrincipals.Count}" : "") +
        (string.IsNullOrWhiteSpace(AdminsSnapshotHash) ? "" : " · snap");
}
