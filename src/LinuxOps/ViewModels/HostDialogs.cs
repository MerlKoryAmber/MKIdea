using System.Windows;
using LinuxOps.Models;

namespace LinuxOps.ViewModels;

/// <summary>Добавить / изменить / дублировать / удалить ресурс Inventory (хост Linux или vCenter).</summary>
internal static class HostDialogs
{
    public static bool TryAdd(Window owner, AppSession session, string? folderId = null)
    {
        var dlg = new HostEditWindow { Owner = owner };
        if (dlg.ShowDialog() != true) return false;
        dlg.Result.IsSelected = true;
        dlg.Result.FolderId = folderId ?? "";
        session.Hosts.Add(dlg.Result);
        session.Persist();
        return true;
    }

    public static bool TryAddVCenter(Window owner, AppSession session, string? folderId = null)
    {
        var dlg = new VCenterEditWindow { Owner = owner };
        if (dlg.ShowDialog() != true) return false;
        dlg.Result.FolderId = folderId ?? "";
        session.VCenters.Add(dlg.Result);
        session.Persist();
        return true;
    }

    public static bool TryEdit(Window owner, AppSession session, IInventoryItem item)
    {
        switch (item)
        {
            case HostConfig host:
            {
                var dlg = new HostEditWindow(host) { Owner = owner };
                if (dlg.ShowDialog() != true) return false;
                var idx = session.Hosts.IndexOf(host);
                if (idx >= 0) session.Hosts[idx] = dlg.Result;
                break;
            }
            case VCenterProfile vc:
            {
                var dlg = new VCenterEditWindow(vc) { Owner = owner };
                if (dlg.ShowDialog() != true) return false;
                var idx = session.VCenters.IndexOf(vc);
                if (idx >= 0) session.VCenters[idx] = dlg.Result;
                break;
            }
            default:
                return false;
        }

        session.Persist();
        return true;
    }

    public static bool TryDuplicate(Window owner, AppSession session, IInventoryItem item)
    {
        switch (item)
        {
            case HostConfig host:
            {
                var copy = new HostConfig
                {
                    Name = NextName(session, string.IsNullOrWhiteSpace(host.Name) ? host.Host : host.Name),
                    Host = host.Host,
                    Port = host.Port,
                    Username = host.Username,
                    Auth = host.Auth,
                    PrivateKeyPath = host.PrivateKeyPath,
                    Password = host.Password,
                    UseSudo = host.UseSudo,
                    DistroHint = host.DistroHint,
                    IsSelected = true,
                    FolderId = host.FolderId
                };
                var dlg = new HostEditWindow(copy) { Owner = owner, Title = "Дублировать хост" };
                if (dlg.ShowDialog() != true) return false;
                dlg.Result.IsSelected = true;
                session.Hosts.Add(dlg.Result);
                break;
            }
            case VCenterProfile vc:
            {
                var copy = new VCenterProfile
                {
                    Name = NextName(session, vc.Title), Server = vc.Server, User = vc.User, IgnoreCert = vc.IgnoreCert,
                    FolderId = vc.FolderId
                };
                var dlg = new VCenterEditWindow(copy) { Owner = owner, Title = "Дублировать vCenter" };
                if (dlg.ShowDialog() != true) return false;
                session.VCenters.Add(dlg.Result);
                break;
            }
            default:
                return false;
        }

        session.Persist();
        return true;
    }

    private static string NextName(AppSession session, string baseName)
    {
        var names = session.Items.Select(h => h.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 2; ; n++)
        {
            var candidate = baseName + "-" + n;
            if (!names.Contains(candidate)) return candidate;
        }
    }

    public static bool TryRemove(Window owner, AppSession session, IInventoryItem item)
    {
        if (!AppDialog.Confirm(owner, $"Удалить {item.Display}?"))
            return false;
        if (item is HostConfig h) session.Hosts.Remove(h);
        else if (item is VCenterProfile v) session.VCenters.Remove(v);
        session.Persist();
        return true;
    }

    public static bool TryAddFolder(Window owner, AppSession session, string? parentId)
    {
        var where = string.IsNullOrEmpty(parentId) ? "" : $" в «{session.PathOf(parentId)}»";
        var name = AppDialog.AskText(owner, $"Имя новой папки{where}:", "Новая папка", "Папка");
        if (name is null) return false;
        session.AddFolder(name, parentId);
        return true;
    }

    public static bool TryRenameFolder(Window owner, AppSession session, HostFolder folder)
    {
        var name = AppDialog.AskText(owner, "Новое имя папки:", folder.Name, "Папка");
        if (name is null || name == folder.Name) return false;
        folder.Name = name;
        session.Commit();
        return true;
    }

    private static string Plural(int n, string one, string few, string many)
        => (n % 10 == 1 && n % 100 != 11) ? one : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? few : many;

    public static bool TryRemoveFolder(Window owner, AppSession session, HostFolder folder)
    {
        var n = session.CountIn(folder);
        var up = session.PathOf(folder.ParentId);
        var msg = n == 0 && session.Folders.All(f => f.ParentId != folder.Id)
            ? $"Удалить пустую папку «{folder.Name}»?"
            : $"Удалить папку «{folder.Name}»? Всё, что в ней ({n} {Plural(n, "ресурс", "ресурса", "ресурсов")} и вложенные папки), переедет в «{up}» — сами ресурсы не удаляются.";
        if (!AppDialog.Confirm(owner, msg, "Папка")) return false;
        session.DeleteFolder(folder);
        return true;
    }
}
