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

    public static bool TryDuplicate(Window owner, AppSession session, HostConfig host)
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
            IsSelected = true
        };
        var dlg = new HostEditWindow(copy) { Owner = owner, Title = "Дублировать хост" };
        if (dlg.ShowDialog() != true) return false;
        dlg.Result.IsSelected = true;
        session.Hosts.Add(dlg.Result);
        session.Persist();
        return true;
    }

    private static string NextName(AppSession session, string baseName)
    {
        var names = session.Hosts.Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 2; ; n++)
        {
            var candidate = baseName + "-" + n;
            if (!names.Contains(candidate)) return candidate;
        }
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
