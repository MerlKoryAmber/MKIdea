using System.Windows;
using LinuxOps.Models;

namespace LinuxOps.ViewModels;

internal static class HostDialogs
{
    public static bool TryAdd(Window owner, AppSession session)
    {
        var dlg = new HostEditWindow { Owner = owner };
        if (dlg.ShowDialog() != true) return false;
        dlg.Result.IsSelected = true;
        session.Hosts.Add(dlg.Result);
        session.Persist();
        return true;
    }

    public static bool TryEdit(Window owner, AppSession session, HostConfig host)
    {
        var dlg = new HostEditWindow(host) { Owner = owner };
        if (dlg.ShowDialog() != true) return false;
        var idx = session.Hosts.IndexOf(host);
        if (idx >= 0) session.Hosts[idx] = dlg.Result;
        session.Persist();
        return true;
    }

    public static bool TryRemove(Window owner, AppSession session, HostConfig host)
    {
        if (!AppDialog.Confirm(owner, $"Удалить {host.Display}?"))
            return false;
        session.Hosts.Remove(host);
        session.Persist();
        return true;
    }
}
