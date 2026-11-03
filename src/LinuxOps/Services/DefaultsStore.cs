using System.IO;
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

    public static AppDefaults Load()
    {
        try
        {
            if (File.Exists(DefaultsPath))
            {
                var json = File.ReadAllText(DefaultsPath);
                var loaded = JsonSerializer.Deserialize<AppDefaults>(json, JsonOptions);
                if (loaded is not null)
                {
                    ExpandPaths(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // fall through
        }

        return new AppDefaults();
    }

    public static void SaveHosts(IEnumerable<HostConfig> hosts)
    {
        var defaults = Load();
        defaults.Hosts = hosts.Select(CloneHostForSave).ToList();
        Write(defaults);
    }

    public static void SaveSelectedActions(IEnumerable<string> actionIds)
    {
        var defaults = Load();
        defaults.SelectedActionIds = actionIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Write(defaults);
    }

    private static void Write(AppDefaults defaults)
    {
        var json = JsonSerializer.Serialize(defaults, JsonOptions);
        File.WriteAllText(DefaultsPath, json);
    }

    private static HostConfig CloneHostForSave(HostConfig h) => new()
    {
        Name = h.Name,
        Host = h.Host,
        Port = h.Port <= 0 ? 22 : h.Port,
        Username = h.Username,
        Auth = string.IsNullOrWhiteSpace(h.Auth) ? "Key" : h.Auth,
        PrivateKeyPath = CollapseUserProfile(h.PrivateKeyPath),
        Password = h.Password,
        DistroHint = h.DistroHint,
        IsSelected = h.IsSelected
    };

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
