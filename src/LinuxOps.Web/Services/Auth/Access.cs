using LinuxOps.Models;

namespace LinuxOps.Web.Services.Auth;

/// <summary>Право на объекте — общее для окна браузера (WebSession) и API: назначение на «всё», на сам хост или на папку выше.</summary>
public static class Access
{
    /// <summary>Папка id — она сама или внутри папки ancestor.</summary>
    public static bool Under(string id, string ancestor, IReadOnlyList<HostFolder> folders)
    {
        for (var p = id; !string.IsNullOrEmpty(p); p = folders.FirstOrDefault(f => f.Id == p)?.ParentId)
            if (p == ancestor) return true;
        return false;
    }

    public static bool CanOn(UserInfo user, string perm, StoredHost h, IReadOnlyList<HostFolder> folders) =>
        // глобальные права (Inventory, настройки, аудит, vCenter) — только из назначения на «всё»
        user.Grants.Any(g => g.Perms.Contains(perm) && (!Perm.Global.Contains(perm) || g.Scope == Scope.All) &&
            (g.Scope == Scope.All || (g.Scope == Scope.Host && g.ObjectId == h.Id) ||
             (g.Scope == Scope.Folder && Under(h.Config.FolderId, g.ObjectId, folders))));
}
