using System.IO;
using System.Text.Json;
using LinuxOps.Models;

namespace LinuxOps.Services;

public static class ActionCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string ActionsDirectory => Path.Combine(GetAppDirectory(), "Actions");

    public static IReadOnlyList<ActionDefinition> LoadAll()
    {
        var list = new List<ActionDefinition>();
        list.AddRange(BuiltinActions.All);

        var dir = ActionsDirectory;
        if (!Directory.Exists(dir))
            return list;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(file);
                var def = JsonSerializer.Deserialize<ActionDefinition>(json, JsonOptions);
                if (def is null || string.IsNullOrWhiteSpace(def.Id))
                    continue;
                def.BuiltIn = false;
                if (string.IsNullOrWhiteSpace(def.Name))
                    def.Name = Path.GetFileNameWithoutExtension(file);
                list.Add(def);
            }
            catch
            {
                // skip broken custom action
            }
        }

        return list;
    }

    public static void SaveCustom(ActionDefinition def)
    {
        if (def.BuiltIn)
            throw new InvalidOperationException("Встроенные действия нельзя перезаписывать.");
        if (string.IsNullOrWhiteSpace(def.Id))
            throw new InvalidOperationException("Нужен Id действия.");

        Directory.CreateDirectory(ActionsDirectory);
        def.BuiltIn = false;
        var path = Path.Combine(ActionsDirectory, SanitizeFileName(def.Id) + ".json");
        var json = JsonSerializer.Serialize(def, JsonOptions);
        File.WriteAllText(path, SshRunner.NormalizeLf(json));
    }

    public static void DeleteCustom(string id)
    {
        var path = Path.Combine(ActionsDirectory, SanitizeFileName(id) + ".json");
        if (File.Exists(path))
            File.Delete(path);
    }

    public static bool Supports(ActionDefinition action, DistroInfo distro)
    {
        if (action.Distros.Count == 0)
            return true;
        if (action.Distros.Any(d => d.Equals("any", StringComparison.OrdinalIgnoreCase)))
            return true;

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormDistro(distro.Id),
            NormDistro(distro.Family)
        };
        foreach (var word in distro.Like.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            tags.Add(NormDistro(word));

        foreach (var d in action.Distros)
        {
            if (tags.Contains(NormDistro(d)))
                return true;
        }

        return false;
    }

    /// <summary>almalinux→alma и соседние id к одному ярлыку каталога.</summary>
    public static string NormDistro(string? raw)
    {
        var s = (raw ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
        return s switch
        {
            "almalinux" => "alma",
            "rockylinux" => "rocky",
            "ol" or "oracle" or "oraclelinux" => "ol",
            "opensuse" or "opensuse-leap" or "opensuse-tumbleweed" or "sles" => "suse",
            "redhat" => "rhel",
            _ => s
        };
    }

    public static bool IdExists(string id)
    {
        if (BuiltinActions.All.Any(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return true;
        var path = Path.Combine(ActionsDirectory, SanitizeFileName(id) + ".json");
        return File.Exists(path);
    }

    public static string UniqueId(string baseId)
    {
        var id = string.IsNullOrWhiteSpace(baseId) ? "custom-action" : baseId.Trim();
        if (!IdExists(id)) return id;
        for (var n = 2; ; n++)
        {
            var candidate = id + "-" + n;
            if (!IdExists(candidate)) return candidate;
        }
    }

    public static string ResolveScript(ActionDefinition action, DistroInfo distro)
    {
        string raw;
        if (!string.IsNullOrWhiteSpace(action.Script))
            raw = action.Script;
        else
            raw = BuiltinActions.ResolveScript(action.Id, distro);

        return SshRunner.NormalizeLf(raw);
    }

    private static string SanitizeFileName(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return id.Trim();
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
