using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AdServerGroups.Models;

public sealed class OuNode
{
    public string Name { get; init; } = string.Empty;
    public string DistinguishedName { get; init; } = string.Empty;
    public ObservableCollection<OuNode> Children { get; } = new();
    public override string ToString() => Name;
}

public sealed class AdComputerItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Name { get; init; } = string.Empty;
    public string DistinguishedName { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string DnsHostName { get; init; } = string.Empty;

    /// <summary>Хост для WinNT/SMB: предпочитаем dNSHostName (Kerberos/FQDN), иначе CN.</summary>
    public string RemoteHost =>
        string.IsNullOrWhiteSpace(DnsHostName) ? Name : DnsHostName.Trim().TrimEnd('.');

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class GroupMappingItem
{
    public string ComputerName { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}

public sealed class LocalMemberItem
{
    public string Name { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
}

public sealed class PlanLine
{
    public string Text { get; init; } = string.Empty;
}

public enum PlanActionKind
{
    CreateGroup,
    SkipExistingGroup,
    AddMemberToGroup,
    AddGroupToLocalAdmins,
    RemoveLocalMember
}

public sealed class PlannedAction
{
    public PlanActionKind Kind { get; init; }
    public string ComputerName { get; init; } = string.Empty;
    /// <summary>FQDN/DNS для удалённого WinNT; если пусто — ComputerName.</summary>
    public string RemoteHost { get; init; } = string.Empty;
    public string GroupName { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    public string EffectiveRemoteHost =>
        string.IsNullOrWhiteSpace(RemoteHost) ? ComputerName : RemoteHost;

    public string ToLogLine()
    {
        if (!string.IsNullOrWhiteSpace(Detail) &&
            (Detail.StartsWith("[AD]", StringComparison.Ordinal) ||
             Detail.StartsWith("[LOCAL]", StringComparison.Ordinal) ||
             Detail.StartsWith("[SKIP]", StringComparison.Ordinal) ||
             Detail.StartsWith("[INFO]", StringComparison.Ordinal) ||
             Detail.StartsWith("[CONFLICT]", StringComparison.Ordinal)))
        {
            return Detail;
        }

        return Kind switch
        {
            PlanActionKind.CreateGroup => $"[AD] CREATE  {GroupName}  in groups OU",
            PlanActionKind.SkipExistingGroup => $"[AD] SKIP    {GroupName}  already exists",
            PlanActionKind.AddMemberToGroup => $"[AD] ADD     {Target}  → {GroupName}",
            PlanActionKind.AddGroupToLocalAdmins => $"[LOCAL] ADD  {GroupName}  → {ComputerName}\\Administrators",
            PlanActionKind.RemoveLocalMember => $"[LOCAL] REMOVE {Target}  from {ComputerName}\\Administrators",
            _ => Detail
        };
    }
}

public sealed record MigrationLeaf(string Member, string? UnpackedFrom);
