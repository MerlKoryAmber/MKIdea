using System.Collections.Concurrent;

namespace LinuxOps.Web.Services;

/// <summary>Блокировка хоста на время действия: второй видит «занят: кто, что, с какого времени».</summary>
public sealed class HostLocks
{
    public sealed record Holder(string Who, string What, DateTime Since);

    private readonly ConcurrentDictionary<string, Holder> _locks = new(StringComparer.OrdinalIgnoreCase);

    public static string Key(string host, int port) => $"{host}:{(port <= 0 ? 22 : port)}";

    /// <summary>Занять свободные; занятые другим — в busy.</summary>
    public List<string> TryLock(IEnumerable<string> keys, string who, string what, out List<(string Key, Holder By)> busy)
    {
        busy = new();
        var got = new List<string>();
        foreach (var k in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_locks.TryAdd(k, new Holder(who, what, DateTime.Now))) got.Add(k);
            else if (_locks.TryGetValue(k, out var by)) busy.Add((k, by));
        }
        return got;
    }

    public void Release(IEnumerable<string> keys)
    {
        foreach (var k in keys) _locks.TryRemove(k, out _);
    }
}
