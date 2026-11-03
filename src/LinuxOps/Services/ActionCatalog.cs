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

        foreach (var d in action.Distros)
        {
            if (d.Equals(distro.Id, StringComparison.OrdinalIgnoreCase) ||
                d.Equals(distro.Family, StringComparison.OrdinalIgnoreCase) ||
                distro.Like.Contains(d, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
