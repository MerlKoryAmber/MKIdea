using System.Collections.ObjectModel;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.ViewModels;

/// <summary>
/// Общий Inventory для всех вкладок: хосты Linux, vCenter, хосты Windows и папки. Списки плоские — папки только способ
/// показа (FolderId у ресурса), поэтому SSH, SFTP и запуск действий о них не знают.
/// </summary>
public sealed class AppSession
{
    public ObservableCollection<HostConfig> Hosts { get; } = new();
    public ObservableCollection<VCenterProfile> VCenters { get; } = new();
    public ObservableCollection<HostFolder> Folders { get; } = new();
    public ObservableCollection<WindowsHost> WindowsHosts { get; } = new();

    /// <summary>Поменялось дерево (папки, перенос ресурса) — деревья на вкладках перестраиваются.</summary>
    public event Action? TreeChanged;

    /// <summary>Ресурсы для действий (Actions): хосты Linux и vCenter. Хосты Windows сюда не входят.</summary>
    public IEnumerable<IInventoryItem> Items => Hosts.Cast<IInventoryItem>().Concat(VCenters);

    /// <summary>Все ресурсы Inventory, вместе с хостами Windows (папки, Inventory, «Подключения»).</summary>
    public IEnumerable<IInventoryItem> AllItems => Items.Concat(WindowsHosts);

    /// <summary>Хосты для вкладки «Подключения»: Linux (SSH) и Windows (RDP).</summary>
    public IEnumerable<IInventoryItem> ConnectItems => Hosts.Cast<IInventoryItem>().Concat(WindowsHosts);

    public void Reload()
    {
        var d = DefaultsStore.Load();
        Hosts.Clear();
        foreach (var h in d.Hosts) Hosts.Add(h);
        VCenters.Clear();
        foreach (var v in d.VCenters) VCenters.Add(v);
        WindowsHosts.Clear();
        foreach (var w in d.WindowsHosts) WindowsHosts.Add(w);
        Folders.Clear();
        foreach (var f in d.Folders) Folders.Add(f);
        // Ссылка на несуществующую папку (правили JSON руками) — ресурс в корень.
        var ids = Folders.Select(f => f.Id).ToHashSet();
        foreach (var i in AllItems.Where(i => i.FolderId.Length > 0 && !ids.Contains(i.FolderId))) i.FolderId = "";
        foreach (var f in Folders.Where(f => f.ParentId.Length > 0 && !ids.Contains(f.ParentId))) f.ParentId = "";
        TreeChanged?.Invoke();
    }

    public void Persist() => DefaultsStore.SaveInventory(Hosts, VCenters, Folders, WindowsHosts);

    /// <summary>Сохранить и перестроить деревья.</summary>
    public void Commit()
    {
        Persist();
        TreeChanged?.Invoke();
    }

    public HostFolder? Folder(string? id) => string.IsNullOrEmpty(id) ? null : Folders.FirstOrDefault(f => f.Id == id);

    /// <summary>Путь папки для подписей: «Площадка А / Прод».</summary>
    public string PathOf(string? folderId)
    {
        var parts = new List<string>();
        for (var f = Folder(folderId); f is not null && parts.Count < 64; f = Folder(f.ParentId)) parts.Insert(0, f.Name);
        return parts.Count == 0 ? "корень" : string.Join(" / ", parts);
    }

    /// <summary><paramref name="folderId"/> — это <paramref name="ancestorId"/> или лежит внутри неё.</summary>
    public bool IsInside(string? folderId, string ancestorId)
    {
        for (var (f, n) = (Folder(folderId), 0); f is not null && n < 64; f = Folder(f.ParentId), n++)
            if (f.Id == ancestorId) return true;
        return false;
    }

    public HostFolder AddFolder(string name, string? parentId)
    {
        var f = new HostFolder { Name = name.Trim(), ParentId = parentId ?? "" };
        Folders.Add(f);
        Commit();
        return f;
    }

    /// <summary>Удалить папку: её ресурсы и вложенные папки переезжают на уровень выше.</summary>
    public void DeleteFolder(HostFolder folder)
    {
        foreach (var i in AllItems.Where(i => i.FolderId == folder.Id)) i.FolderId = folder.ParentId;
        foreach (var f in Folders.Where(f => f.ParentId == folder.Id)) f.ParentId = folder.ParentId;
        Folders.Remove(folder);
        Commit();
    }

    /// <summary>Ресурсов в папке вместе с вложенными.</summary>
    public int CountIn(HostFolder folder) => AllItems.Count(i => IsInside(i.FolderId, folder.Id));

    public void Move(IInventoryItem item, string? folderId)
    {
        item.FolderId = folderId ?? "";
        Commit();
    }

    /// <summary>Перенести папку. Нельзя в саму себя и в свои вложенные.</summary>
    public bool Move(HostFolder folder, string? parentId)
    {
        if (!string.IsNullOrEmpty(parentId) && IsInside(parentId, folder.Id)) return false;
        folder.ParentId = parentId ?? "";
        Commit();
        return true;
    }
}
