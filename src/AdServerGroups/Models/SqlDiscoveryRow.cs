namespace AdServerGroups.Models;

public sealed class SqlDiscoveryRow
{
    public string ComputerName { get; init; } = string.Empty;
    public string DnsHostName { get; init; } = string.Empty;
    public bool SqlFound { get; init; }
    public string SqlLabel => SqlFound ? "есть" : "нет";
    public string InstancesText { get; init; } = "—";
    public string Access { get; init; } = "—";
    public string GroupName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<string> Instances { get; init; } = Array.Empty<string>();

    public bool CanPlan =>
        SqlFound &&
        string.Equals(Access, "OK", StringComparison.OrdinalIgnoreCase) &&
        Instances.Count > 0;
}
