using System.Collections.Concurrent;

namespace LinuxOps.Web.Services.Guac;

/// <summary>Идущий сеанс (терминал, RDP, PowerShell): для списка «Идут сейчас», просмотра вживую и закрытия.</summary>
public sealed class LiveSession
{
    public required string Id { get; init; }
    public required string Who { get; init; }
    public required string Login { get; init; }
    /// <summary>ssh | rdp</summary>
    public required string Protocol { get; init; }
    public required string HostTitle { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    /// <summary>Номер соединения у guacd (ответ ready) — к нему подключается зритель (select).</summary>
    public required string ConnectionId { get; init; }
    /// <summary>Консоль PowerShell на сервере: смотреть и закрывать — только с правом «vCenter и PowerShell» (встроенная «Админ»).</summary>
    public bool IsPowerShell { get; init; }
    /// <summary>Закрыть сеанс со стороны сервиса.</summary>
    public CancellationTokenSource Kill { get; } = new();
    /// <summary>
    /// Владелец ушёл (туннель владельца закрыт). guacd держит соединение, пока подключён хоть кто-то, — зрители по этому знаку
    /// отключаются сами, иначе сеанс к хосту жил бы без владельца.
    /// </summary>
    public CancellationTokenSource Ended { get; } = new();
    /// <summary>Кто закрыл (для статуса в журнале сеансов).</summary>
    public string? ClosedBy { get; set; }
    private int _watchers;
    public int Watchers => _watchers;
    public void WatcherIn() => Interlocked.Increment(ref _watchers);
    public void WatcherOut() => Interlocked.Decrement(ref _watchers);
}

/// <summary>
/// Идущие сеансы этого процесса (в памяти: после перезапуска службы сеансов нет — guacd их рвёт).
/// Зрители сюда не попадают: они не сеансы, а подключение к чужому.
/// </summary>
public sealed class GuacLive
{
    private readonly ConcurrentDictionary<string, LiveSession> _map = new();
    // занятые места по логину: от начала подключения к guacd до конца сеанса (зрители не считаются)
    private readonly Dictionary<string, int> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Настройка: сколько сеансов одновременно у одного человека (0 — без предела).</summary>
    public const string LimitKey = "sess.limit";
    public static int Limit(WebStore store) => int.TryParse(store.GetSetting(LimitKey), out var n) ? Math.Clamp(n, 0, 1000) : 0;

    /// <summary>Сколько сеансов у человека сейчас (идущие и те, что подключаются).</summary>
    public int Count(string login) { lock (_slots) return _slots.GetValueOrDefault(login); }

    /// <summary>Занять место под сеанс. false — предел исчерпан (limit 0 — без предела). Освободить — <see cref="Release"/>.</summary>
    public bool TryReserve(string login, int limit)
    {
        lock (_slots)
        {
            var n = _slots.GetValueOrDefault(login);
            if (limit > 0 && n >= limit) return false;
            _slots[login] = n + 1;
            return true;
        }
    }

    public void Release(string login)
    {
        lock (_slots)
        {
            var n = _slots.GetValueOrDefault(login) - 1;
            if (n > 0) _slots[login] = n; else _slots.Remove(login);
        }
    }

    public void Add(LiveSession s) => _map[s.Id] = s;
    public void Remove(string id) => _map.TryRemove(id, out _);
    public LiveSession? Find(string id) => _map.GetValueOrDefault(id);
    public List<LiveSession> List() => _map.Values.OrderBy(s => s.Started).ToList();

    /// <summary>Закрыть сеанс: туннель рвёт соединение с guacd и браузером. false — сеанса уже нет.</summary>
    public bool Close(string id, string by)
    {
        if (!_map.TryGetValue(id, out var s)) return false;
        s.ClosedBy = by;
        try { s.Kill.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }
}
