using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LinuxOps.Web.Services.Guac;

/// <summary>
/// Пропуск на один сеанс: окно (после проверки прав) кладёт сюда параметры подключения guacd, браузер открывает
/// туннель с номером пропуска. Пропуск одноразовый, живёт 60 с и только для того же пользователя.
/// Пароли и ключи в браузер не уходят — только номер.
/// </summary>
public sealed class GuacTicket
{
    public string Id { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public required string Login { get; init; }
    public required string Who { get; init; }
    /// <summary>ssh | rdp</summary>
    public required string Protocol { get; init; }
    public required string HostId { get; init; }
    public required string HostTitle { get; init; }
    /// <summary>Параметры connect для guacd (hostname, username, password, private-key, host-key, …).</summary>
    public required Dictionary<string, string> Params { get; init; }
    public DateTime Expires { get; } = DateTime.UtcNow.AddSeconds(60);
}

public sealed class GuacTickets
{
    private readonly ConcurrentDictionary<string, GuacTicket> _map = new();

    public string Issue(GuacTicket t)
    {
        foreach (var old in _map.Where(kv => kv.Value.Expires < DateTime.UtcNow).Select(kv => kv.Key).ToList()) _map.TryRemove(old, out _);
        _map[t.Id] = t;
        return t.Id;
    }

    /// <summary>Забрать пропуск (один раз). null — нет, истёк или чужой.</summary>
    public GuacTicket? Take(string id, string login)
    {
        if (string.IsNullOrEmpty(id) || !_map.TryRemove(id, out var t)) return null;
        if (t.Expires < DateTime.UtcNow || !string.Equals(t.Login, login, StringComparison.OrdinalIgnoreCase)) return null;
        return t;
    }
}
