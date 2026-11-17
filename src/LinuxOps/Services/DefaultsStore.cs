using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxOps.Models;

namespace LinuxOps.Services;

public static class DefaultsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string DefaultsPath { get; } = Path.Combine(GetAppDirectory(), "LinuxOps.defaults.json");

    /// <summary>
    /// Что случилось с файлом настроек при чтении (испорчен — отложен в копию, не читается — запись отключена).
    /// Показывается один раз при старте; null — всё в порядке.
    /// </summary>
    public static string? Problem { get; private set; }

    /// <summary>Файл не удалось прочитать (занят, нет прав) — не перезаписываем его пустыми настройками.</summary>
    private static bool _readOnly;

    public static AppDefaults Load()
    {
        if (!File.Exists(DefaultsPath)) return new AppDefaults();

        string json;
        try
        {
            json = ReadWithRetry(DefaultsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _readOnly = true;
            Problem ??= $"Не удалось прочитать файл настроек ({Why(ex)}):\n{DefaultsPath}\n\n" +
                        "Программа работает с пустыми настройками, изменения в файл не сохраняются, чтобы не затереть его. " +
                        "Закройте то, что держит файл, и перезапустите LinuxOps.";
            return new AppDefaults();
        }

        AppDefaults? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppDefaults>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return SetAsideBroken(ex.LineNumber is { } line
                ? $"ошибка в JSON, строка {line + 1}, позиция {(ex.BytePositionInLine ?? 0) + 1}"
                : "ошибка в JSON");
        }
        if (loaded is null) return SetAsideBroken("в файле нет настроек (null)");

        ExpandPaths(loaded);
        foreach (var h in loaded.Hosts)
            h.Password = Unprotect(h.Password);
        return loaded;
    }

    private static string Why(Exception ex) => ex is UnauthorizedAccessException
        ? "нет прав доступа к файлу"
        : "файл занят другой программой или недоступен";

    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
        }
    }

    /// <summary>Испорченный файл переименовываем рядом — дальше программа начинает с чистого файла, данные не теряются.</summary>
    private static AppDefaults SetAsideBroken(string reason)
    {
        var dir = Path.GetDirectoryName(DefaultsPath)!;
        var backup = Path.Combine(dir, $"LinuxOps.defaults.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        try
        {
            File.Move(DefaultsPath, backup);
            Problem ??= $"Файл настроек испорчен ({reason}) и не прочитан.\n\n" +
                        $"Он сохранён как:\n{backup}\n\n" +
                        "Программа начала с пустыми настройками. Исправьте копию и верните её под именем " +
                        "LinuxOps.defaults.json при закрытой программе — или перенесите хосты заново.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _readOnly = true;
            Problem ??= $"Файл настроек испорчен ({reason}), отложить его в копию не удалось ({Why(ex)}):\n{DefaultsPath}\n\n" +
                        "Изменения в файл не сохраняются, чтобы не затереть его.";
        }
        return new AppDefaults();
    }

    /// <summary>Notepad++ из комплекта (Tools\Notepad++ рядом с exe), если он есть.</summary>
    public static string? BundledEditor
    {
        get
        {
            var path = Path.Combine(GetAppDirectory(), "Tools", "Notepad++", "notepad++.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Редактор для F4. Пусто или «notepad.exe» (старое значение по умолчанию) — Notepad++ из комплекта,
    /// без него — Блокнот. Чтобы всё же Блокнот при наличии Notepad++, укажите C:\Windows\notepad.exe.
    /// </summary>
    public static string F4Editor
    {
        get
        {
            var editor = Load().F4Editor?.Trim().Trim('"') ?? "";
            if (editor.Length == 0 || string.Equals(editor, "notepad.exe", StringComparison.OrdinalIgnoreCase))
                return BundledEditor ?? "notepad.exe";
            return editor;
        }
        set
        {
            var defaults = Load();
            var v = (value ?? "").Trim().Trim('"');
            // Комплектный путь не запоминаем: папку с программой могут перенести.
            if (BundledEditor is { } bundled && string.Equals(v, bundled, StringComparison.OrdinalIgnoreCase)) v = "";
            defaults.F4Editor = v;
            Write(defaults);
        }
    }

    public static string Theme
    {
        get => Load().Theme is { Length: > 0 } t ? t : "system";
        set
        {
            var defaults = Load();
            defaults.Theme = value;
            Write(defaults);
        }
    }

    public static void SaveSelectedActions(IEnumerable<string> actionIds)
    {
        var defaults = Load();
        defaults.SelectedActionIds = actionIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Write(defaults);
    }

    /// <summary>Inventory целиком: хосты, vCenter и папки — одной записью.</summary>
    public static void SaveInventory(IEnumerable<HostConfig> hosts, IEnumerable<VCenterProfile> vcenters, IEnumerable<HostFolder> folders,
        IEnumerable<WindowsHost> windows)
    {
        var defaults = Load();
        defaults.Hosts = hosts.Select(CloneHostForSave).ToList();
        defaults.VCenters = vcenters.Select(p => new VCenterProfile
        {
            Name = p.Name.Trim(), Server = p.Server.Trim(), User = p.User.Trim(), IgnoreCert = p.IgnoreCert,
            FolderId = p.FolderId, IsSelected = p.IsSelected
        }).ToList();
        defaults.Folders = folders.Select(f => new HostFolder
        {
            Id = f.Id, Name = f.Name.Trim(), ParentId = f.ParentId, IsExpanded = f.IsExpanded
        }).ToList();
        defaults.WindowsHosts = windows.Select(w => new WindowsHost
        {
            Name = w.Name.Trim(), Host = w.Host.Trim(), Port = w.Port is > 0 and < 65536 ? w.Port : 3389, User = w.User.Trim(),
            Auth = w.Auth is "restricted" or "password" ? w.Auth : "rcg", AdminConsole = w.AdminConsole,
            RedirectDrives = w.RedirectDrives, RedirectPrinters = w.RedirectPrinters, FolderId = w.FolderId, IsSelected = w.IsSelected
        }).ToList();
        Write(defaults);
    }

    private static void Write(AppDefaults defaults)
    {
        // Load() расшифровывает пароли хостов — перед записью шифруем всегда, иначе любое сохранение
        // (план, тема, редактор, vCenter) писало бы их открытым текстом. Protect повторно не шифрует.
        if (_readOnly) return;
        defaults.Hosts = defaults.Hosts.Select(CloneHostForSave).ToList();
        var json = JsonSerializer.Serialize(defaults, JsonOptions);
        // Сначала во временный файл, потом подмена: оборванная запись не оставит полупустой JSON.
        var tmp = DefaultsPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, DefaultsPath, overwrite: true);
    }

    private static HostConfig CloneHostForSave(HostConfig h) => new()
    {
        Name = h.Name,
        Host = h.Host,
        Port = h.Port <= 0 ? 22 : h.Port,
        Username = h.Username,
        Auth = string.IsNullOrWhiteSpace(h.Auth) ? "Key" : h.Auth,
        PrivateKeyPath = CollapseUserProfile(h.PrivateKeyPath),
        Password = Protect(h.Password),
        UseSudo = h.UseSudo,
        DistroHint = h.DistroHint,
        IsSelected = h.IsSelected,
        FolderId = h.FolderId
    };

    public const string PasswordPrefix = "dpapi:";

    public static string Protect(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.StartsWith(PasswordPrefix, StringComparison.Ordinal))
            return password ?? string.Empty;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        return PasswordPrefix + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(PasswordPrefix, StringComparison.Ordinal))
            return stored ?? string.Empty;
        try
        {
            var raw = Convert.FromBase64String(stored[PasswordPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return stored;
        }
    }

    private static string CollapseUserProfile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "%USERPROFILE%" + path[home.Length..];
        return path;
    }

    private static void ExpandPaths(AppDefaults defaults)
    {
        foreach (var h in defaults.Hosts)
        {
            if (!string.IsNullOrWhiteSpace(h.PrivateKeyPath))
                h.PrivateKeyPath = Environment.ExpandEnvironmentVariables(h.PrivateKeyPath);
        }
    }

    private static string GetAppDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
