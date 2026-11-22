using LinuxOps.Models;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>
/// Права окна: кто вошёл и что ему можно на каком объекте. Назначение на папку действует на всё внутри неё.
/// Проверка — и в интерфейсе (кнопки), и здесь (сервис сам отказывает и пишет в аудит).
/// </summary>
public sealed partial class WebSession
{
    /// <summary>Кто вошёл (задаёт Shell из cookie при открытии окна).</summary>
    public UserInfo User { get; private set; } = new();

    /// <summary>Все папки (для проверки прав); Folders — только видимые.</summary>
    private List<HostFolder> _allFolders = new();

    public void SetUser(UserInfo user, string ip)
    {
        User = user;
        Who = $"{user.Login} ({ip})";
    }

    /// <summary>Есть ли право хоть где-нибудь (вкладки, кнопки). На объекте — CanOn / CanOnFolder.</summary>
    public bool Can(string perm) => User.Can(perm);

    /// <summary>Папка id — она сама или внутри папки ancestor.</summary>
    private bool Under(string id, string ancestor) => Access.Under(id, ancestor, _allFolders);

    /// <summary>Право на папке (folderId "" — корень: только назначение на «всё»).</summary>
    public bool CanOnFolder(string perm, string folderId) => User.Grants.Any(g => g.Perms.Contains(perm) &&
        (g.Scope == Scope.All || (g.Scope == Scope.Folder && folderId.Length > 0 && Under(folderId, g.ObjectId))));

    /// <summary>Право на хосте: на «всё», на сам хост или на папку над ним.</summary>
    public bool CanOn(string perm, StoredHost h) => Access.CanOn(User, perm, h, _allFolders);

    private string PermText(string perm) => Perm.All.FirstOrDefault(p => p.Id == perm).Text ?? perm;

    private void Deny(string perm, string what)
    {
        _audit.Write(Who, "отказ: нет права", $"{what} (нужно {perm}, роли: {User.RolesText})");
        Warn($"Нет права «{PermText(perm)}»: {what}.");
    }

    /// <summary>Нет права нигде — сообщение и аудит. true — можно (дальше проверяется объект).</summary>
    private bool Allowed(string perm, string what)
    {
        if (User.Can(perm)) return true;
        Deny(perm, what);
        return false;
    }

    private bool AllowedOn(string perm, StoredHost h, string what)
    {
        if (CanOn(perm, h)) return true;
        Deny(perm, $"{what}: {h.Config.Title}");
        return false;
    }

    private bool AllowedOnFolder(string perm, string folderId, string what)
    {
        if (CanOnFolder(perm, folderId)) return true;
        Deny(perm, what);
        return false;
    }

    /// <summary>Проверки в окне хоста: существующий — право «Inventory» на него, новый — на папку, куда его кладут.</summary>
    private bool CanTestHost(StoredHost h, string what) => Hosts.Contains(h)
        ? AllowedOn(Perm.HostsEdit, h, what)
        : AllowedOnFolder(Perm.HostsEdit, h.Config.FolderId, what + ": новый хост в эту папку");

    /// <summary>Запуск по нескольким хостам: право нужно на каждом, иначе — отказ со списком (без частичного запуска).</summary>
    private bool AllHostsAllowed(string perm, IEnumerable<StoredHost> hosts, string what)
    {
        var denied = hosts.Where(h => !CanOn(perm, h)).Select(h => h.Config.Title).ToList();
        if (denied.Count == 0) return true;
        _audit.Write(Who, "отказ: нет права", $"{what} (нужно {perm}) на: {string.Join(", ", denied)}");
        Warn($"Нет права «{PermText(perm)}» на: {string.Join(", ", denied)}. Снимите эти хосты.");
        return false;
    }

    /// <summary>Путь папки «Отдел А / Отдел Б» (для аудита и окон).</summary>
    public string FolderPath(string id)
    {
        var parts = new List<string>();
        for (var f = _allFolders.FirstOrDefault(x => x.Id == id); f is not null; f = _allFolders.FirstOrDefault(x => x.Id == f.ParentId))
            parts.Insert(0, f.Name);
        return parts.Count == 0 ? "(корень)" : string.Join(" / ", parts);
    }

    /// <summary>
    /// Отобрать хосты и папки, которые пользователь видит (право «Видеть хосты» на хост или папку выше).
    /// Папки-предки видны как путь, их прочие хосты — нет.
    /// </summary>
    private void ApplyFolderScope()
    {
        _allFolders = Folders.ToList();
        if (!User.Limited) return;
        Hosts = Hosts.Where(h => CanOn(Perm.HostsView, h)).ToList();
        var visible = new HashSet<string>();
        foreach (var f in Folders.Where(f => CanOnFolder(Perm.HostsView, f.Id))) visible.Add(f.Id);
        foreach (var id in visible.ToList().Concat(Hosts.Select(h => h.Config.FolderId)).ToList())
            for (var p = id; !string.IsNullOrEmpty(p); p = Folders.FirstOrDefault(f => f.Id == p)?.ParentId ?? "")
                visible.Add(p);
        Folders = Folders.Where(f => visible.Contains(f.Id)).ToList();
    }

    /// <summary>Хост в той же области, что видит пользователь (перед сохранением, запуском).</summary>
    private bool InScope(StoredHost h) => CanOn(Perm.HostsView, h);
}
