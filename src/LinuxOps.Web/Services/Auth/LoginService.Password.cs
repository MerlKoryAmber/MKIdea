namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Смена своего пароля в интерфейсе (0.7.3): локальный админ и локальные пользователи; у AD пароль меняется в домене.
/// Нужны текущий пароль и код TOTP — открытое без присмотра окно пароль не поменяет. Неудачи считаются вместе со входом
/// (5 за 15 минут — пауза). Лабораторный режим (admin / admin без TOTP) — только меню на сервере: там же он и выключается.
/// После смены все входы учётки закрываются (отпечаток пароля в cookie, <see cref="PasswordStamp"/>) — и это окно тоже: вход заново.
/// </summary>
public sealed partial class LoginService
{
    /// <summary>Может ли пользователь менять пароль сам (пункт в шапке).</summary>
    public bool CanChangeOwnPassword(UserInfo u) => u.Kind switch
    {
        UserKind.Admin => store.LoadLocalAdmin() is { NoTotp: false, TotpConfirmed: true } la
                          && string.Equals(la.Login, u.Login, StringComparison.OrdinalIgnoreCase),
        UserKind.Local => store.LoadLocalUser(u.Login) is { Enabled: true, TotpConfirmed: true },
        _ => false
    };

    /// <summary>
    /// Отпечаток текущего пароля локальной учётки (в cookie входа): пароль сменили — отпечаток другой, вход закрывается.
    /// AD и API — пусто (пароль AD живёт в домене).
    /// </summary>
    public string PasswordStamp(string login, string kind)
    {
        var hash = kind switch
        {
            UserKind.Admin => store.LoadLocalAdmin() is var la && string.Equals(la.Login, login, StringComparison.OrdinalIgnoreCase) ? la.PasswordHash : "",
            UserKind.Local => store.LoadLocalUser(login)?.PasswordHash ?? "",
            _ => ""
        };
        return hash.Length == 0 ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("pws:" + hash)))[..16];
    }

    /// <summary>Сменить свой пароль. null — готово, иначе текст ошибки для окна.</summary>
    public string? ChangeOwnPassword(UserInfo u, string current, string next, string repeat, string code, string ip)
    {
        var who = $"{u.Login} ({ip})";
        var kLogin = "u:" + u.Login; var kIp = "ip:" + ip;
        if (Locked(kLogin) || Locked(kIp)) return "Слишком много неудачных попыток. Подождите 15 минут.";
        if (u.Kind is not (UserKind.Admin or UserKind.Local)) return "Пароль учётки AD меняется в домене.";
        if (next.Length < LocalUser.MinPassword) return $"Новый пароль — не короче {LocalUser.MinPassword} символов.";
        if (next != repeat) return "Новый пароль и повтор не совпадают.";
        if (next == current) return "Новый пароль совпадает с текущим.";

        lock (TotpGate)
        {
            // ещё раз под замком: параллельные попытки иначе проскочат предел все разом
            if (Locked(kLogin) || Locked(kIp)) return "Слишком много неудачных попыток. Подождите 15 минут.";
            ITotpAccount? acc = u.Kind == UserKind.Admin ? store.LoadLocalAdmin() : store.LoadLocalUser(u.Login);
            switch (acc)
            {
                case LocalAdmin la when !string.Equals(la.Login, u.Login, StringComparison.OrdinalIgnoreCase) || !la.Enabled:
                    return "Учётка не найдена или выключена.";
                case LocalAdmin { NoTotp: true }:
                    return "Лабораторный режим: пароль меняется только на сервере — sudo linuxops-web → «Local admin» (это и выключит лабораторный режим).";
                case LocalUser { Enabled: false }:
                case null:
                    return "Учётка не найдена или выключена.";
            }
            if (!acc.TotpConfirmed) return "Сначала привяжите TOTP: выйдите и войдите снова.";

            var hash = acc is LocalAdmin a ? a.PasswordHash : ((LocalUser)acc).PasswordHash;
            if (hash.Length == 0 || !PasswordHash.Verify(current, hash))
            {
                Failed(kLogin, kIp);
                audit.Write(who, "пароль: отказ", "неверный текущий пароль");
                return "Неверный текущий пароль.";
            }
            var step = Totp.Verify(store.Unprotect(acc.TotpSecretEnc), code, acc.TotpLastStep);
            if (step < 0)
            {
                Failed(kLogin, kIp);
                audit.Write(who, "пароль: отказ", "неверный код TOTP");
                return "Неверный код. Проверьте время на телефоне.";
            }
            acc.TotpLastStep = step;
            var newHash = PasswordHash.Hash(next);
            if (acc is LocalAdmin admin) { admin.PasswordHash = newHash; store.SaveLocalAdmin(admin); }
            else { var lu = (LocalUser)acc; lu.PasswordHash = newHash; store.SaveLocalUser(lu); }
            Clear(kLogin);
        }
        audit.Write(who, "пароль: сменён сам", u.Kind == UserKind.Admin ? "локальный админ" : "локальный пользователь");
        return null;
    }
}
