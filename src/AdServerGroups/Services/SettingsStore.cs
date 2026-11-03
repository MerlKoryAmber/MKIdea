using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdServerGroups.Models;

namespace AdServerGroups.Services;

public static class SettingsStore
{
    private const string FileName = "AdServerGroups.settings";
    private const string DefaultsFileName = "AdServerGroups.defaults.json";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AD-Server-Groups/Merl/v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>Зашифрованный файл рядом с exe (пользовательские настройки).</summary>
    public static string SettingsPath { get; } = Path.Combine(GetAppDirectory(), FileName);

    /// <summary>Базовый JSON рядом с exe (среда/лаба) — не вшит в сборку кода.</summary>
    public static string DefaultsPath { get; } = Path.Combine(GetAppDirectory(), DefaultsFileName);

    private static string LegacyPlainPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AdServerGroups",
        "settings.json");

    private static readonly string[] RequiredKeepMembers =
    {
        "Administrator",
        "Administrators",
        "Domain Admins",
        "Exchange Trusted Subsystem",
        "Organization Management",
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var protectedBytes = File.ReadAllBytes(SettingsPath);
                var jsonBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(jsonBytes);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? CreateFromDefaultsFile();
                if (EnsureRequiredKeepMembers(loaded))
                    Save(loaded);
                return loaded;
            }

            // Однократная миграция со старого %AppData%\...\settings.json
            if (File.Exists(LegacyPlainPath))
            {
                var json = File.ReadAllText(LegacyPlainPath);
                var migrated = JsonSerializer.Deserialize<AppSettings>(json) ?? CreateFromDefaultsFile();
                EnsureRequiredKeepMembers(migrated);
                Save(migrated);
                try { File.Delete(LegacyPlainPath); } catch { /* ignore */ }
                return migrated;
            }
        }
        catch
        {
            // битый/чужой файл — ниже defaults.json / пустые поля
        }

        // Нет зашифрованных настроек: базовый JSON рядом с exe (среда), иначе пустой каркас.
        var fromDefaults = CreateFromDefaultsFile();
        EnsureRequiredKeepMembers(fromDefaults);
        return fromDefaults;
    }

    /// <summary>Читает AdServerGroups.defaults.json; при отсутствии — пустые site-поля.</summary>
    public static AppSettings CreateFromDefaultsFile()
    {
        try
        {
            if (File.Exists(DefaultsPath))
            {
                var json = File.ReadAllText(DefaultsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // ignore — вернём пустой каркас
        }

        return new AppSettings();
    }

    /// <summary>Дописывает обязательные keep-группы в старые settings без потери пользовательских.</summary>
    public static bool EnsureRequiredKeepMembers(AppSettings settings)
    {
        var parts = (settings.AlwaysKeepMembers ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var changed = false;
        foreach (var required in RequiredKeepMembers)
        {
            if (parts.Any(p => string.Equals(p, required, StringComparison.OrdinalIgnoreCase)))
                continue;
            parts.Add(required);
            changed = true;
        }

        if (changed)
            settings.AlwaysKeepMembers = string.Join(",", parts);
        return changed;
    }

    public static void Save(AppSettings settings)
    {
        SanitizeGeometry(settings);

        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(jsonBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SettingsPath, protectedBytes);
    }

    private static void SanitizeGeometry(AppSettings settings)
    {
        if (double.IsNaN(settings.WindowLeft) || double.IsInfinity(settings.WindowLeft))
            settings.WindowLeft = double.NaN;
        if (double.IsNaN(settings.WindowTop) || double.IsInfinity(settings.WindowTop))
            settings.WindowTop = double.NaN;
        if (double.IsNaN(settings.WindowWidth) || settings.WindowWidth < 100)
            settings.WindowWidth = 1180;
        if (double.IsNaN(settings.WindowHeight) || settings.WindowHeight < 100)
            settings.WindowHeight = 760;
        if (double.IsNaN(settings.LogPanelHeight) || settings.LogPanelHeight < 72)
            settings.LogPanelHeight = 140;
    }

    public static string ResolveGroupName(AppSettings settings, string computerName)
    {
        if (string.IsNullOrWhiteSpace(settings.GroupNameTemplate))
            throw new InvalidOperationException("В настройках не задан шаблон имени группы.");

        if (!settings.GroupNameTemplate.Contains("{ComputerName}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Шаблон имени группы должен содержать {ComputerName}.");

        return settings.GroupNameTemplate.Replace("{ComputerName}", computerName, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveGroupDescription(AppSettings settings, string computerName)
        => settings.GroupDescriptionTemplate.Replace("{ComputerName}", computerName, StringComparison.OrdinalIgnoreCase);

    public static string ResolveSqlGroupDescription(AppSettings settings, string computerName)
    {
        var template = string.IsNullOrWhiteSpace(settings.SqlGroupDescriptionTemplate)
            ? "SQL sysadmin for {ComputerName}"
            : settings.SqlGroupDescriptionTemplate;
        return template.Replace("{ComputerName}", computerName, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveSqlGroupName(AppSettings settings, string computerName)
    {
        var template = string.IsNullOrWhiteSpace(settings.SqlGroupNameTemplate)
            ? "GRP-SQL-{ComputerName}"
            : settings.SqlGroupNameTemplate;
        if (!template.Contains("{ComputerName}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Шаблон SQL-группы должен содержать {ComputerName}.");
        return template.Replace("{ComputerName}", computerName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>NetBIOS для SQL login: DomainNetBios или первый лейбл DomainDns.</summary>
    public static string ResolveDomainNetBios(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DomainNetBios))
            return settings.DomainNetBios.Trim();

        var dns = (settings.DomainDns ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dns))
            return string.Empty;

        var label = dns.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return label ?? string.Empty;
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
