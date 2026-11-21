using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Второй фактор для AD через MK 2FA. Ожидание — в памяти сервиса (номер — в защищённой cookie браузера).
/// Человек пускается только после consume: тот же username и тот же context, что у этой попытки.
/// MK 2FA недоступен — вход AD закрыт (аварийный путь — локальный админ).
/// </summary>
public sealed partial class LoginService
{
    /// <summary>Ожидание подтверждения: кто прошёл пароль, номер запроса в MK 2FA, context этой попытки.</summary>
    public sealed class MfaPending
    {
        public string Token { get; init; } = "";
        public UserInfo User { get; init; } = null!;
        public string ChallengeId { get; init; } = "";
        public string Context { get; init; } = "";
        public string Ip { get; init; } = "";
        public DateTimeOffset Expires { get; set; }
        /// <summary>pending_push | pending_code</summary>
        public string Status { get; set; } = "pending_push";
        public int PollMs { get; set; } = 2000;
    }

    /// <summary>Состояние для страницы входа: push | code | done | fail.</summary>
    public sealed record MfaState(string State, string Message = "", UserInfo? User = null, int PollMs = 2000);

    private readonly ConcurrentDictionary<string, MfaPending> _mfa = new();

    public MfaPending? FindMfa(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_mfa.TryGetValue(token, out var p)) return null;
        if (p.Expires >= DateTimeOffset.UtcNow) return p;
        _mfa.TryRemove(token, out _);
        return null;
    }

    /// <summary>Пароль AD прошёл — запросить подтверждение в MK 2FA.</summary>
    public async Task<Outcome> StartMfa(UserInfo user, string ip)
    {
        foreach (var old in _mfa.Values.Where(x => x.Expires < DateTimeOffset.UtcNow).ToList()) _mfa.TryRemove(old.Token, out _);
        var context = "lo-login-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var r = await mfa.Create(user.Login, context, ip);
        if (r.Ok && r.ChallengeId.Length > 0 && r.Status is "pending_push" or "pending_code")
        {
            var p = new MfaPending
            {
                Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                User = user, ChallengeId = r.ChallengeId, Context = context, Ip = ip, Status = r.Status, PollMs = r.PollMs,
                Expires = r.Expires is { } e && e > DateTimeOffset.UtcNow ? e : DateTimeOffset.UtcNow.AddMinutes(5)
            };
            _mfa[p.Token] = p;
            audit.Write($"{user.Login} ({ip})", "вход: MK 2FA", r.Status == "pending_code" ? "ждём код" : "ждём подтверждения в Express");
            return new(Step.Mfa, User: user, Pending: p.Token);
        }
        audit.Write($"{user.Login} ({ip})", "вход: отказ", $"MK 2FA: {Describe(r)}");
        return new(Step.Fail, r.Code switch
        {
            403 => "Подтверждение входа для вашей учётки недоступно (второй фактор не настроен или заблокирован). Обратитесь в поддержку.",
            429 => "Слишком много запросов на подтверждение. Подождите несколько минут.",
            _ => "MK 2FA недоступен — вход для пользователей домена сейчас невозможен. Попробуйте позже."
        });
    }

    /// <summary>Опрос со страницы входа: статус в MK 2FA; approved — забрать результат и пустить.</summary>
    public async Task<MfaState> PollMfa(string? token)
    {
        if (FindMfa(token) is not { } p) return new("fail", "Время на подтверждение вышло. Войдите снова.");
        var r = await mfa.Get(p.ChallengeId);
        if (r.Code == 0) return new(Wait(p), "Нет связи с MK 2FA, повторяю…", PollMs: p.PollMs);
        return await Next(p, r);
    }

    /// <summary>Код из приложения (статус pending_code).</summary>
    public async Task<MfaState> CodeMfa(string? token, string code)
    {
        if (FindMfa(token) is not { } p) return new("fail", "Время на подтверждение вышло. Войдите снова.");
        var kLogin = "u:" + p.User.Login; var kIp = "ip:" + p.Ip;
        if (Locked(kLogin) || Locked(kIp)) return new("code", "Слишком много неверных кодов. Подождите 15 минут.");
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length is < 6 or > 8) return new("code", "Введите код из приложения — 6 цифр.");
        var r = await mfa.Verify(p.ChallengeId, code);
        if (r.Code == 422)
        {
            Failed(kLogin, kIp);
            if (r.Error == "code_reused") return new("code", "Этот код уже использован — дождитесь следующего в приложении.");
            audit.Write($"{p.User.Login} ({p.Ip})", "вход: отказ", "MK 2FA: неверный код");
            if (r.Status == "denied") return Drop(p, "Попытки ввода кода кончились — вход отклонён.");
            return new("code", "Неверный код.");
        }
        if (r.Code == 0) return new("code", "Нет связи с MK 2FA. Попробуйте ещё раз.");
        return await Next(p, r);
    }

    /// <summary>Человек ушёл со страницы или нажал «Отмена» — запрос в MK 2FA отменить.</summary>
    public async Task CancelMfa(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_mfa.TryRemove(token, out var p)) return;
        await mfa.Cancel(p.ChallengeId);
        audit.Write($"{p.User.Login} ({p.Ip})", "вход: MK 2FA отменён", "«Отмена» или ушёл со страницы входа");
    }

    private static string Wait(MfaPending p) => p.Status == "pending_code" ? "code" : "push";

    private async Task<MfaState> Next(MfaPending p, MkMfa.Reply r)
    {
        switch (r.Status)
        {
            case "pending_push":
            case "pending_code":
                p.Status = r.Status;
                return new(Wait(p), PollMs: p.PollMs);
            case "approved":
                return await Finish(p);
            case "denied":
                return Drop(p, "Вход отклонён в MK 2FA.");
        }
        if (r.Code == 410 || r.Status is "expired" or "cancelled" or "consumed")
            return Drop(p, "Время на подтверждение вышло. Войдите снова.");
        audit.Write($"{p.User.Login} ({p.Ip})", "вход: отказ", "MK 2FA: " + Describe(r));
        return Drop(p, "MK 2FA ответил ошибкой — войдите снова.", false);
    }

    /// <summary>consume: только 200, тот же логин и context. Иначе не пускать.</summary>
    private async Task<MfaState> Finish(MfaPending p)
    {
        var r = await mfa.Consume(p.ChallengeId, p.Context);
        _mfa.TryRemove(p.Token, out _);
        if (r.Code == 200 && r.Context == p.Context && SameLogin(r.Username, p.User.Login))
        {
            Clear("u:" + p.User.Login);
            audit.Write($"{p.User.Login} ({p.Ip})", "вход", $"AD + MK 2FA, роли: {p.User.RolesText}");
            return new("done", User: p.User);
        }
        audit.Write($"{p.User.Login} ({p.Ip})", "вход: отказ",
            r.Code == 200 ? $"MK 2FA: consume вернул другого человека или context ({r.Username})" : "MK 2FA consume: " + Describe(r));
        return new("fail", "Подтверждение не прошло проверку — войдите снова.");
    }

    private MfaState Drop(MfaPending p, string message, bool log = true)
    {
        _mfa.TryRemove(p.Token, out _);
        if (log) audit.Write($"{p.User.Login} ({p.Ip})", "вход: отказ", "MK 2FA: " + message);
        return new("fail", message);
    }

    /// <summary>ivanov, CORP\ivanov и ivanov@corp.ru — один человек.</summary>
    private static bool SameLogin(string a, string b)
    {
        static string Sam(string s) { s = s.Trim(); var i = s.LastIndexOf('\\'); if (i >= 0) s = s[(i + 1)..]; var at = s.IndexOf('@'); return at > 0 ? s[..at] : s; }
        return a.Length > 0 && string.Equals(Sam(a), Sam(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(MkMfa.Reply r) =>
        r.Code == 0 ? "нет связи: " + r.Message : $"{r.Code} {r.Error} {r.Message}".Trim();
}
