using LinuxOps.Models;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>
/// Inventory (0.7.9): переложить хосты и папки в другую папку — меню «Переместить в папку…» и перетаскивание.
/// Право «Inventory» нужно на то, что перекладывают (папку — как при удалении: на её родителя), и на папку назначения.
/// Доступы, выданные на папку, после переезда действуют по новому месту — это пишется в аудит путём «откуда → куда».
/// Проверка и запись — по свежим данным из базы и под общим замком: список папок сеанса мог устареть (другое окно,
/// другой админ), а цикл папок повесил бы обход «вверх по родителям». В базе меняется только родитель / папка.
/// </summary>
public sealed partial class WebSession
{
    private static readonly object MoveGate = new();

    /// <summary>Папка id — это root или лежит внутри него (по списку folders). Цикл в данных — тоже «внутри».</summary>
    private static bool IsInside(string id, string root, IReadOnlyList<HostFolder> folders)
    {
        var seen = new HashSet<string>();
        for (var p = id; p.Length > 0; p = folders.FirstOrDefault(x => x.Id == p)?.ParentId ?? "")
        {
            if (p == root || !seen.Add(p)) return true;
        }
        return false;
    }

    /// <summary>Подтянуть свежий список папок в сеанс (пути в аудите и окнах — по нему).</summary>
    private void RefreshFolders(List<HostFolder> fresh)
    {
        foreach (var f in fresh)
            if (_allFolders.FirstOrDefault(x => x.Id == f.Id) is { } mine) { mine.ParentId = f.ParentId; mine.Name = f.Name; }
    }

    /// <summary>Переложить хосты. Итог — (получилось, текст; пусто — нечего делать).</summary>
    public (bool Ok, string Text) MoveHosts(IReadOnlyCollection<StoredHost> hosts, string folderId)
    {
        if (!Allowed(Perm.HostsEdit, "хост: переместить")) return (false, "Нет права «Inventory».");
        lock (MoveGate)
        {
            var folders = _store.LoadFolders();
            RefreshFolders(folders);
            if (folderId.Length > 0 && !folders.Any(f => f.Id == folderId)) return (false, "Папки уже нет — обновите страницу.");
            var to = FolderPath(folderId);
            if (!AllowedOnFolder(Perm.HostsEdit, folderId, $"хост: переместить в «{to}»")) return (false, $"Нет права «Inventory» на «{to}».");
            var mine = hosts.Select(h => Hosts.FirstOrDefault(x => x.Id == h.Id)).OfType<StoredHost>().Where(h => h.Config.FolderId != folderId).ToList();
            var denied = mine.Where(h => !CanOn(Perm.HostsEdit, h)).ToList();
            if (denied.Count > 0)
            {
                Deny(Perm.HostsEdit, "хост: переместить: " + string.Join(", ", denied.Select(h => h.Config.Title)));
                return (false, "Нет права «Inventory» на: " + string.Join(", ", denied.Select(h => h.Config.Title)) + ".");
            }
            if (mine.Count == 0) return (true, "");
            var moved = new List<StoredHost>();
            foreach (var h in mine)
            {
                var from = FolderPath(h.Config.FolderId);
                if (!_store.SetHostFolder(h.Id, folderId)) continue; // хост удалили в другом окне — не воскрешать
                h.Config.FolderId = folderId;
                moved.Add(h);
                _audit.Write(Who, "хост: перемещён", $"{h.Config.Title}: {from} → {to}");
            }
            Notify();
            if (moved.Count == 0) return (false, "Хоста уже нет — обновите страницу.");
            return (true, moved.Count == 1 ? $"«{moved[0].Config.Title}» — в «{to}»." : $"Перемещено хостов: {moved.Count} — в «{to}».");
        }
    }

    public (bool Ok, string Text) MoveFolder(HostFolder f, string parentId)
    {
        lock (MoveGate)
        {
            var folders = _store.LoadFolders();
            RefreshFolders(folders);
            var fresh = folders.FirstOrDefault(x => x.Id == f.Id);
            if (fresh is null) return (false, "Папки уже нет — обновите страницу.");
            if (parentId.Length > 0 && !folders.Any(x => x.Id == parentId)) return (false, "Папки назначения уже нет — обновите страницу.");
            if (fresh.ParentId == parentId) return (true, "");
            if (IsInside(parentId, fresh.Id, folders)) return (false, "Нельзя переложить папку в саму себя или в её подпапку.");
            var from = FolderPath(fresh.Id);
            if (!AllowedOnFolder(Perm.HostsEdit, fresh.ParentId, $"папка: переместить «{from}»")) return (false, $"Нет права «Inventory» там, где лежит «{fresh.Name}».");
            if (!AllowedOnFolder(Perm.HostsEdit, parentId, $"папка: переместить в «{FolderPath(parentId)}»")) return (false, $"Нет права «Inventory» на «{FolderPath(parentId)}».");
            if (!_store.SetFolderParent(fresh.Id, parentId)) return (false, "Папки уже нет — обновите страницу.");
            // видимая копия (Folders) и полный список (_allFolders) могут быть разными объектами
            foreach (var x in _allFolders.Concat(Folders).Where(x => x.Id == fresh.Id)) x.ParentId = parentId;
            var to = FolderPath(fresh.Id);
            _audit.Write(Who, "папка: перемещена", $"{from} → {to}");
            Notify();
            return (true, $"Папка — теперь «{to}».");
        }
    }

    /// <summary>Для окна «Переместить в папку»: можно ли положить папку сюда (по списку сеанса; окончательно — в MoveFolder).</summary>
    public bool CanMoveFolder(HostFolder f, string parentId) =>
        CanOnFolder(Perm.HostsEdit, f.ParentId) && CanOnFolder(Perm.HostsEdit, parentId)
        && f.ParentId != parentId && !IsInside(parentId, f.Id, _allFolders);
}
