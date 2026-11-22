using LinuxOps.Web.Services.Auth;
using LinuxOps.Web.Services.Guac;

namespace LinuxOps.Web.Services;

/// <summary>
/// Идущие сеансы (0.7.7): список, просмотр вживую (только смотреть), закрыть. Права — «Сеансы: смотреть» и «Сеансы: закрывать»
/// (только из назначения на «всё»; у встроенной «Админ» есть). Пользователь сеанса ничего не видит; всё — в аудит.
/// </summary>
public sealed partial class WebSession
{
    public bool CanSeeLive => Can(Perm.SessionsWatch) || Can(Perm.SessionsClose);

    /// <summary>Пропуск зрителя к идущему сеансу. null — отказ или сеанса уже нет (причина — в Notice).</summary>
    public string? WatchSession(string id, GuacLive live, GuacTickets tickets)
    {
        if (!Allowed(Perm.SessionsWatch, "сеансы: смотреть")) return null;
        if (live.Find(id) is not { } s) { ShowNotice("Сеанс уже закрыт."); return null; }
        // консоль PowerShell на сервере (PowerCLI, учётки vCenter) — только тем, кому она доступна самой
        if (s.IsPowerShell && !Allowed(Perm.Vcenter, "сеансы: смотреть консоль PowerShell")) return null;
        var t = new GuacTicket { Login = User.Login, Who = Who, Protocol = s.Protocol, HostId = "", HostTitle = s.HostTitle, Params = new(), WatchId = s.Id };
        return tickets.Issue(t);
    }

    /// <summary>
    /// Предел одновременных сеансов на человека (0.7.8): проверка до выдачи пропуска — чтобы не спрашивать пароль RDP зря.
    /// Окончательно место занимает туннель (<see cref="GuacLive.TryReserve"/>).
    /// </summary>
    public bool SessionSlotFree(GuacLive live)
    {
        var limit = GuacLive.Limit(_store);
        if (limit == 0 || live.Count(User.Login) < limit) return true;
        _audit.Write(Who, "сеанс: отказ — предел", $"уже {limit} одновременно");
        ShowNotice(GuacTunnel.LimitText(limit));
        return false;
    }

    public int SessionLimit => GuacLive.Limit(_store);

    /// <summary>Задать предел (⚙, право «Настройки»). Итог — текстом.</summary>
    public string SetSessionLimit(int n)
    {
        if (!Allowed(Perm.Settings, "настройки: предел сеансов")) return "Нет права «Настройки».";
        if (n is < 0 or > 1000) return "От 0 до 1000.";
        if (n == SessionLimit) return "Без изменений.";
        _store.PutSetting(GuacLive.LimitKey, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _audit.Write(Who, "настройки: предел сеансов", n == 0 ? "без предела" : $"{n} на человека");
        return n == 0 ? "Сохранено: без предела." : $"Сохранено: не больше {n} на человека.";
    }

    /// <summary>Закрыть чужой (или свой) идущий сеанс. Итог — текстом.</summary>
    public string CloseSession(string id, GuacLive live)
    {
        if (!Allowed(Perm.SessionsClose, "сеансы: закрыть")) return "Нет права «Сеансы: закрывать».";
        if (live.Find(id) is not { } s) return "Сеанс уже закрыт.";
        if (s.IsPowerShell && !Allowed(Perm.Vcenter, "сеансы: закрыть консоль PowerShell")) return "Консоль PowerShell закрывает только «Админ».";
        if (!live.Close(id, Who)) return "Сеанс уже закрыт.";
        _audit.Write(Who, "сеанс: закрыт администратором", $"{s.HostTitle}, {s.Who}, сеанс {s.Id}");
        return $"Закрыт: {s.HostTitle}, {s.Who}.";
    }
}
