using LinuxOps.Models;

namespace LinuxOps.Services;

/// <summary>Встроенные PowerShell-действия vSphere (только чтение). Остальное — по запросу владельца.</summary>
public static class VsphereActions
{
    private static ActionDefinition Ps(string id, string name, string description, string script, params ActionParam[] parameters) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        BuiltIn = true,
        Kind = "powershell",
        Distros = new List<string>(),
        Privilege = "vCenter",
        TimeoutSeconds = 300,
        Script = script.Replace("\r\n", "\n").Trim() + "\n",
        Parameters = parameters.ToList()
    };

    public static IReadOnlyList<ActionDefinition> All { get; } = new[]
    {
        Ps("vsphere-old-snapshots", "vSphere: старые снапшоты",
            "Снапшоты старше N дней: ВМ, имя, дата, размер.",
            """
            $days = [int]$Days
            $border = (Get-Date).AddDays(-$days)
            $snaps = @(Get-VM | Get-Snapshot | Where-Object Created -lt $border | Sort-Object Created)
            $snaps | Select-Object VM, Name, Created,
                @{N = 'РазмерГБ'; E = { [math]::Round($_.SizeGB, 1) } },
                @{N = 'Дней'; E = { [int]((Get-Date) - $_.Created).TotalDays } } | Format-Table -AutoSize
            "Итого: снапшотов старше $days дн.: $($snaps.Count)"
            """,
            new ActionParam { Name = "Days", Label = "Старше, дней", Default = "7" })
    };
}
