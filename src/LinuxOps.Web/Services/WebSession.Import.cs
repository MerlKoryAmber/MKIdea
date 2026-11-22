using System.Text;
using LinuxOps.Models;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>Строка импорта: что получится и почему. Ok — будет добавлен.</summary>
public sealed record ImportRow(int Line, string Name, string Host, int Port, bool Windows, string Folder, string User, bool Ok, string Note);

/// <summary>
/// Inventory (0.7.5): импорт хостов из CSV и одна учётка SSH сразу на много хостов.
/// Права — как при ручной правке: добавить — «Inventory» на папку (новая папка — на ближайшую существующую выше),
/// учётка — «Inventory» на каждый хост (иначе отказ целиком, без частичного применения).
/// </summary>
public sealed partial class WebSession
{
    public const int ImportMaxRows = 5000;

    /// <summary>Шаблон CSV: разделитель «;» (так открывает Excel в русской локали), UTF-8 с BOM.</summary>
    public static string ImportTemplate =>
        "﻿имя;адрес;порт;тип;папка;логин\r\n" +
        "web-01;10.0.0.11;22;linux;Отдел А/Web;root\r\n" +
        "db-01;db-01.corp.local;;linux;Отдел А/DB;\r\n" +
        "ts-01;10.0.0.50;3389;windows;Отдел А/Windows;\r\n";

    private static readonly Dictionary<string, string> ImportColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["имя"] = "name", ["name"] = "name", ["название"] = "name",
        ["адрес"] = "host", ["host"] = "host", ["ip"] = "host", ["address"] = "host", ["хост"] = "host",
        ["порт"] = "port", ["port"] = "port",
        ["тип"] = "type", ["type"] = "type", ["ос"] = "type", ["os"] = "type",
        ["папка"] = "folder", ["folder"] = "folder", ["path"] = "folder", ["путь"] = "folder",
        ["логин"] = "user", ["user"] = "user", ["login"] = "user", ["username"] = "user", ["пользователь"] = "user"
    };

    /// <summary>Разобрать CSV и проверить каждую строку: формат, повтор адреса, право на папку. Ничего не сохраняет.</summary>
    public List<ImportRow> ImportPreview(string text, out string error)
    {
        error = "";
        var rows = new List<ImportRow>();
        var lines = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first < 0) { error = "Файл пустой."; return rows; }
        var sep = new[] { ';', ',', '\t' }.OrderByDescending(c => lines[first].Count(x => x == c)).First();
        var head = SplitCsv(lines[first], sep).Select(h => ImportColumns.GetValueOrDefault(h.Trim().ToLowerInvariant(), "")).ToList();
        if (!head.Contains("host")) { error = "Нет колонки «адрес» в первой строке. Скачайте шаблон."; return rows; }
        int Col(string n) => head.IndexOf(n);

        // адреса, которые уже есть — во всём Inventory, не только в видимой части
        var existing = _store.LoadHosts().Select(h => h.Config.Host.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = first + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (rows.Count >= ImportMaxRows) { error = $"Больше {ImportMaxRows} строк — остальные не прочитаны."; break; }
            var f = SplitCsv(lines[i], sep);
            string Get(string n) => Col(n) is var c && c >= 0 && c < f.Count ? f[c].Trim() : "";
            var host = Get("host");
            var name = Get("name");
            var type = Get("type").ToLowerInvariant();
            var folder = string.Join("/", Get("folder").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var user = Get("user");
            var line = i + 1;
            bool? win = type switch
            {
                "" or "linux" or "ssh" or "лин" or "линукс" => false,
                "windows" or "win" or "rdp" or "вин" or "виндовс" => true,
                _ => null
            };
            ImportRow Bad(string why) => new(line, name, host, 0, win == true, folder, user, false, why);
            if (host.Length == 0) { rows.Add(Bad("нет адреса")); continue; }
            if (host.Length > 253 || host.Any(ch => char.IsWhiteSpace(ch) || ch is ';' or ',' or '/' or '\\' or '@')) { rows.Add(Bad("адрес с недопустимыми знаками")); continue; }
            if (win is null) { rows.Add(Bad($"тип «{Get("type")}»: linux или windows")); continue; }
            var portText = Get("port");
            var port = win == true ? 3389 : 22;
            if (portText.Length > 0 && (!int.TryParse(portText, out port) || port is <= 0 or > 65535)) { rows.Add(Bad("порт — число 1…65535")); continue; }
            if (user.Length > 64 || user.Any(char.IsWhiteSpace)) { rows.Add(Bad("логин с пробелами или длиннее 64")); continue; }
            if (existing.Contains(host)) { rows.Add(new(line, name, host, port, win == true, folder, user, false, "пропуск: адрес уже есть в Inventory")); continue; }
            if (!inFile.Add(host)) { rows.Add(new(line, name, host, port, win == true, folder, user, false, "пропуск: адрес повторяется в файле")); continue; }
            var (parentId, missing) = ResolveFolder(folder);
            if (!CanOnFolder(Perm.HostsEdit, parentId))
            {
                rows.Add(new(line, name, host, port, win == true, folder, user, false, $"нет права «Inventory» на «{FolderPath(parentId)}»"));
                continue;
            }
            rows.Add(new(line, name.Length > 0 ? name : host, host, port, win == true, folder, win == true ? "" : user, true,
                missing.Count > 0 ? "новая папка: " + string.Join("/", missing) : ""));
        }
        return rows;
    }

    /// <summary>Папка по пути «А/Б/В»: самая глубокая существующая и недостающие имена после неё.</summary>
    private (string Id, List<string> Missing) ResolveFolder(string path)
    {
        var id = "";
        var parts = path.Length == 0 ? [] : path.Split('/').ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var next = _allFolders.FirstOrDefault(x => x.ParentId == id && string.Equals(x.Name, parts[i], StringComparison.OrdinalIgnoreCase));
            if (next is null) return (id, parts.Skip(i).ToList());
            id = next.Id;
        }
        return (id, []);
    }

    /// <summary>Добавить строки предпросмотра с Ok. Права проверяются заново. Итог — текстом.</summary>
    public string ImportApply(IReadOnlyList<ImportRow> rows, string fileName)
    {
        if (!Allowed(Perm.HostsEdit, "хосты: импорт")) return "Нет права «Inventory».";
        var existing = _store.LoadHosts().Select(h => h.Config.Host.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0;
        var newFolders = new List<string>();
        var names = new List<string>();
        foreach (var r in rows.Where(r => r.Ok))
        {
            if (existing.Contains(r.Host)) { skipped++; continue; }
            var (parentId, missing) = ResolveFolder(r.Folder);
            if (!CanOnFolder(Perm.HostsEdit, parentId)) { skipped++; continue; }
            foreach (var m in missing)
            {
                var f = new HostFolder { Name = m, ParentId = parentId };
                _store.SaveFolder(f);
                Folders.Add(f); _allFolders.Add(f);
                newFolders.Add(FolderPath(f.Id));
                parentId = f.Id;
            }
            var h = new StoredHost
            {
                Config = new HostConfig
                {
                    Name = r.Name, Host = r.Host, Port = r.Port, Username = r.User, Auth = "PrivateKey", UseSudo = true,
                    FolderId = parentId, IsSelected = false, DistroHint = r.Windows ? StoredHost.WindowsHint : ""
                }
            };
            _store.SaveHost(h);
            Hosts.Add(h);
            existing.Add(r.Host);
            names.Add(r.Name);
            added++;
        }
        var text = $"добавлено хостов: {added}" + (newFolders.Count > 0 ? $", папок: {newFolders.Count}" : "") + (skipped > 0 ? $", пропущено при загрузке: {skipped}" : "");
        _audit.Write(Who, "хосты: импорт", $"{fileName}: {text}" + (newFolders.Count > 0 ? "; папки: " + string.Join(", ", newFolders) : "")
            + (names.Count > 0 ? "; хосты: " + string.Join(", ", names.Take(50)) + (names.Count > 50 ? $" и ещё {names.Count - 50}" : "") : ""));
        Notify();
        return text;
    }

    /// <summary>
    /// Учётка SSH на много хостов: логин (пусто — не менять), ключ или пароль, sudo (null — не менять).
    /// Пишется только в хранилище сервиса, на хосты — ничего. Право «Inventory» — на каждом, иначе отказ целиком.
    /// </summary>
    public string BulkCredential(IReadOnlyList<StoredHost> hosts, string user, bool useKey, string? key, string? keyName, string? password, bool? sudo)
    {
        var list = hosts.Where(h => !h.IsWindows && Hosts.Contains(h)).ToList();
        if (list.Count == 0) return "Не выбрано ни одного хоста Linux.";
        if (!Allowed(Perm.HostsEdit, "хосты: учётка SSH") || !AllHostsAllowed(Perm.HostsEdit, list, "хосты: учётка SSH")) return "Нет права «Inventory» на часть хостов — ничего не изменено.";
        foreach (var h in list)
        {
            var c = h.Config;
            if (user.Length > 0) c.Username = user;
            c.Auth = useKey ? "PrivateKey" : "Password";
            if (sudo is { } s) c.UseSudo = s;
            _store.SaveHost(h, useKey ? null : password, useKey ? key : null, useKey ? keyName : null);
        }
        var what = (user.Length > 0 ? $"логин {user}, " : "") + (useKey ? (key is { Length: > 0 } ? $"ключ ({keyName})" : "вход по ключу (ключ прежний)") : "пароль")
                   + (sudo is { } sd ? (sd ? ", sudo" : ", без sudo") : "");
        _audit.Write(Who, "хосты: учётка SSH", $"{what} — {list.Count} хостов: " + string.Join(", ", list.Select(h => h.Config.Title).Take(50)) + (list.Count > 50 ? $" и ещё {list.Count - 50}" : ""));
        Notify();
        return $"Готово: {what} — на {list.Count} хостах.";
    }

    /// <summary>Строка CSV: кавычки и удвоенные кавычки внутри.</summary>
    private static List<string> SplitCsv(string line, char sep)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (q)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') q = false;
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == sep) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }
}
