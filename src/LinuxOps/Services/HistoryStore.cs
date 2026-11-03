using System.IO;
using System.Text.Json;
using LinuxOps.Models;

namespace LinuxOps.Services;

public sealed class HistoryEntry
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Mode { get; set; } = ""; // dry-run | apply
    public string Host { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "";
    public string Preview { get; set; } = "";
    public string Detail { get; set; } = "";

    public string AtLocalText => AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public static class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static string HistoryPath { get; } = Path.Combine(
        Path.GetDirectoryName(DefaultsStore.DefaultsPath) ?? AppContext.BaseDirectory,
        "LinuxOps.history.json");

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new List<HistoryEntry>();
            var json = File.ReadAllText(HistoryPath);
            return JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOptions) ?? new List<HistoryEntry>();
        }
        catch
        {
            return new List<HistoryEntry>();
        }
    }

    public static void Append(IEnumerable<HistoryEntry> entries)
    {
        lock (Gate)
        {
            var all = Load();
            all.InsertRange(0, entries);
            if (all.Count > 500) all = all.Take(500).ToList();
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(all, JsonOptions));
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            if (File.Exists(HistoryPath)) File.Delete(HistoryPath);
        }
    }
}
