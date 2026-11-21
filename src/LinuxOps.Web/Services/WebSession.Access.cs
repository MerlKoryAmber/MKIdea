using LinuxOps.Models;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>Права окна: кто вошёл, что ему можно, какие папки хостов он видит. Проверка — и в интерфейсе, и здесь.</summary>
public sealed partial class WebSession
{
    /// <summary>Кто вошёл (задаёт Shell из cookie при открытии окна).</summary>
    public UserInfo User { get; private set; } = new();

    public void SetUser(UserInfo user, string ip)
    {
        User = user;
        Who = $"{user.Login} ({ip})";
    }

    public bool Can(string perm) => User.Can(perm);

    /// <summary>Нет права — сообщение в окне и запись в аудит. true — можно.</summary>
    private bool Allowed(string perm, string what)
    {
        if (User.Can(perm)) return true;
        _audit.Write(Who, "отказ: нет права", $"{what} (нужно {perm}, роли: {User.RolesText})");
        Warn($"Нет права: {Perm.All.FirstOrDefault(p => p.Id == perm).Text ?? perm}.");
        return false;
    }

    /// <summary>Папки, которые видит пользователь: разрешённые с вложенными. null — все.</summary>
    private HashSet<string>? AllowedFolders(List<HostFolder> all)
    {
        if (User.Folders is null) return null;
        var set = new HashSet<string>(User.Folders);
        bool grew;
        do
        {
            grew = false;
            foreach (var f in all)
                if (!set.Contains(f.Id) && set.Contains(f.ParentId)) { set.Add(f.Id); grew = true; }
        } while (grew);
        return set;
    }

    /// <summary>Отфильтровать хосты и папки по ролям. Предки разрешённых папок видны (как путь), их хосты — нет.</summary>
    private void ApplyFolderScope()
    {
        var allowed = AllowedFolders(Folders);
        if (allowed is null) return;
        Hosts = Hosts.Where(h => allowed.Contains(h.Config.FolderId)).ToList();
        var visible = new HashSet<string>(allowed);
        foreach (var id in allowed.ToList())
        {
            var p = Folders.FirstOrDefault(f => f.Id == id)?.ParentId;
            while (!string.IsNullOrEmpty(p) && visible.Add(p)) p = Folders.FirstOrDefault(f => f.Id == p)?.ParentId;
        }
        Folders = Folders.Where(f => visible.Contains(f.Id)).ToList();
    }

    /// <summary>Хост в папке, доступной пользователю (для сохранения и запуска).</summary>
    private bool InScope(StoredHost h)
    {
        var allowed = AllowedFolders(_store.LoadFolders());
        return allowed is null || allowed.Contains(h.Config.FolderId);
    }
}
