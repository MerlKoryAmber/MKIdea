using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdServerGroups.Models;

namespace AdServerGroups.Services;

public static class StateStore
{
    private const string FileName = "AdServerGroups.state";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AD-Server-Groups/Merl/state/v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static string StatePath { get; } = Path.Combine(GetAppDirectory(), FileName);

    public static AppWorkState Load()
    {
        try
        {
            if (!File.Exists(StatePath))
                return new AppWorkState();

            var protectedBytes = File.ReadAllBytes(StatePath);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(jsonBytes);
            return JsonSerializer.Deserialize<AppWorkState>(json) ?? new AppWorkState();
        }
        catch
        {
            return new AppWorkState();
        }
    }

    public static void Save(AppWorkState state)
    {
        var dir = Path.GetDirectoryName(StatePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(jsonBytes, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StatePath, protectedBytes);
    }

    public static string OptionsFingerprint(AppSettings settings)
    {
        var parts = new List<string>();
        if (settings.CreateMissingGroups) parts.Add("create");
        if (settings.AddGroupToLocalAdministrators) parts.Add("add");
        if (settings.MigrateDirectMembersToGroup) parts.Add("migrate");
        if (settings.ExpandNestedGroupsOnMigrate) parts.Add("expand");
        if (settings.RemoveNonTargetMembers) parts.Add("remove");
        return string.Join("+", parts);
    }

    public static ServerWorkRecord? Find(AppWorkState state, string computer, string targetGroup)
    {
        return state.Servers.FirstOrDefault(s =>
            string.Equals(s.Computer, computer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.TargetGroup, targetGroup, StringComparison.OrdinalIgnoreCase));
    }

    public static ServerWorkRecord GetOrCreate(AppWorkState state, string computer, string targetGroup)
    {
        var existing = Find(state, computer, targetGroup);
        if (existing is not null)
            return existing;

        var created = new ServerWorkRecord
        {
            Computer = computer,
            TargetGroup = targetGroup
        };
        state.Servers.Add(created);
        return created;
    }

    public static void RemoveSelected(AppWorkState state, IEnumerable<string> computers)
    {
        var set = computers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        state.Servers.RemoveAll(s => set.Contains(s.Computer));
    }

    public static string ComputeAdminsHash(IEnumerable<string> members)
    {
        var normalized = NormalizeMembers(members);
        if (normalized.Count == 0)
            return "empty";

        var payload = string.Join("\n", normalized);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public static void SetAdminsSnapshot(ServerWorkRecord record, IEnumerable<string> members)
    {
        record.AdminsSnapshot = NormalizeMembers(members);
        record.AdminsSnapshotHash = ComputeAdminsHash(record.AdminsSnapshot);
    }

    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) DiffAdmins(
        IEnumerable<string>? previous,
        IEnumerable<string>? current)
    {
        var prev = NormalizeMembers(previous ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var curr = NormalizeMembers(current ?? Array.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = curr.Where(m => !prev.Contains(m))
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var removed = prev.Where(m => !curr.Contains(m))
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (added, removed);
    }

    public static bool HasAdminsSnapshot(ServerWorkRecord? record) =>
        record is not null && !string.IsNullOrWhiteSpace(record.AdminsSnapshotHash);

    public static bool AdminsHashMatches(ServerWorkRecord? record, IEnumerable<string> currentMembers)
    {
        if (!HasAdminsSnapshot(record))
            return true; // старые записи без снимка — не считаем конфликтом

        return string.Equals(
            record!.AdminsSnapshotHash,
            ComputeAdminsHash(currentMembers),
            StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeMembers(IEnumerable<string> members) =>
        members
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
