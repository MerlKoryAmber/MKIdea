using System.IO;
using System.Text.Json;

namespace LinuxOps.Services;

public sealed class KnownHostRecord
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Fingerprint { get; set; } = "";
}

public static class KnownHostsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static string PathFile { get; } = Path.Combine(
        Path.GetDirectoryName(DefaultsStore.DefaultsPath) ?? AppContext.BaseDirectory,
        "known_hosts.json");

    public static string? Find(string host, int port)
    {
        lock (Gate)
        {
            return Load().FirstOrDefault(x =>
                x.Port == port && string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase))?.Fingerprint;
        }
    }

    public static void Remember(string host, int port, string fingerprint)
    {
        lock (Gate)
        {
            var list = Load();
            list.RemoveAll(x => x.Port == port && string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase));
            list.Add(new KnownHostRecord { Host = host, Port = port, Fingerprint = fingerprint });
            File.WriteAllText(PathFile, JsonSerializer.Serialize(list, JsonOptions));
        }
    }

    private static List<KnownHostRecord> Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return new List<KnownHostRecord>();
            return JsonSerializer.Deserialize<List<KnownHostRecord>>(File.ReadAllText(PathFile), JsonOptions)
                   ?? new List<KnownHostRecord>();
        }
        catch
        {
            return new List<KnownHostRecord>();
        }
    }
}
