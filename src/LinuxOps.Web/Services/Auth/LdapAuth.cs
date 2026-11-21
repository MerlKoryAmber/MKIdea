using System.DirectoryServices.Protocols;
using System.Net;

namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Вход через AD: простой bind учёткой пользователя (LDAPS, StartTLS или — только в лабе — без шифрования),
/// затем его группы с вложенностью (LDAP_MATCHING_RULE_IN_CHAIN). Служебная учётка не нужна.
/// Сертификат контроллера проверяет ОС сервера (системное хранилище CA).
/// </summary>
public static class LdapAuth
{
    public sealed record Result(bool Ok, string Error, string Login, string Display, List<string> Groups);

    private static Result Fail(string error) => new(false, error, "", "", new());

    /// <summary>user, MERL\user или user@merl.loc → sAMAccountName.</summary>
    public static string SamOf(string login)
    {
        login = login.Trim();
        var i = login.IndexOf('\\');
        if (i >= 0) return login[(i + 1)..];
        var at = login.IndexOf('@');
        return at >= 0 ? login[..at] : login;
    }

    public static Result Authenticate(AuthSettings s, string login, string password)
    {
        var sam = SamOf(login);
        // Пустой пароль в LDAP — это анонимный bind, он «успешен». Отсекаем до сервера.
        if (sam.Length == 0 || string.IsNullOrEmpty(password)) return Fail("Неверный логин или пароль.");
        if (sam.IndexOfAny(new[] { '*', '(', ')', '\0', '/', ',' }) >= 0) return Fail("Неверный логин или пароль.");
        if (string.IsNullOrWhiteSpace(s.AdServers) || string.IsNullOrWhiteSpace(s.AdDomain)) return Fail("Вход через AD не настроен.");

        var bindName = login.Contains('\\') || login.Contains('@') ? login.Trim() : $"{sam}@{s.AdDomain}";
        try
        {
            using var conn = Connect(s);
            conn.Bind(new NetworkCredential(bindName, password));

            var user = Search(conn, s.BaseDn, $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={Escape(sam)}))",
                "distinguishedName", "displayName", "sAMAccountName", "userAccountControl").FirstOrDefault();
            if (user is null) return Fail("Учётка не найдена в " + s.BaseDn + ".");
            var dn = Attr(user, "distinguishedName");
            if (int.TryParse(Attr(user, "userAccountControl"), out var uac) && (uac & 2) != 0) return Fail("Учётка AD отключена.");

            var groups = new List<string>();
            foreach (var g in Search(conn, s.BaseDn, $"(&(objectClass=group)(member:1.2.840.113556.1.4.1941:={Escape(dn)}))", "cn", "sAMAccountName"))
            {
                foreach (var n in new[] { Attr(g, "sAMAccountName"), Attr(g, "cn") })
                    if (n.Length > 0 && !groups.Contains(n, StringComparer.OrdinalIgnoreCase)) groups.Add(n);
            }
            var samReal = Attr(user, "sAMAccountName");
            var display = Attr(user, "displayName");
            return new Result(true, "", samReal.Length > 0 ? samReal : sam, display.Length > 0 ? display : sam, groups);
        }
        catch (LdapException ex) when (ex.ErrorCode == 49)
        {
            return Fail("Неверный логин или пароль.");
        }
        catch (LdapException ex) when (ex.ErrorCode is 81 or 91)
        {
            return Fail("Контроллер домена недоступен: " + s.AdServers + (s.AdSecurity == LdapSecurity.None ? "" : " (проверьте сертификат контроллера и TLS)") + ".");
        }
        catch (Exception ex)
        {
            return Fail("Ошибка LDAP: " + ex.Message);
        }
    }

    /// <summary>Кнопка «Проверить» в настройках: только соединение и TLS, без учётки (анонимный rootDSE).</summary>
    public static (bool Ok, string Text) Probe(AuthSettings s)
    {
        try
        {
            using var conn = Connect(s);
            conn.AuthType = AuthType.Anonymous;
            conn.Bind();
            var r = (SearchResponse)conn.SendRequest(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "dnsHostName"));
            var e = r.Entries.Count > 0 ? r.Entries[0] : null;
            return (true, $"Отвечает {(e is null ? s.AdServers : Attr(e, "dnsHostName"))}, домен {(e is null ? "?" : Attr(e, "defaultNamingContext"))}" +
                          (s.AdSecurity == LdapSecurity.None ? ". Без шифрования: пароли идут открытым текстом — только для лабы." : ", TLS — OK."));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static int _native;

    /// <summary>
    /// .NET 8 ищет libldap-2.5.so.0, а в AlmaLinux / RHEL 10 — OpenLDAP 2.6 с именем libldap.so.2 (тот же ABI).
    /// Подсказываем загрузчику доступное имя; ставится один раз до первого вызова LDAP.
    /// </summary>
    private static void EnsureNative()
    {
        if (OperatingSystem.IsWindows() || Interlocked.Exchange(ref _native, 1) == 1) return;
        try
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(LdapConnection).Assembly, (name, asm, path) =>
            {
                if (!name.StartsWith("libldap", StringComparison.Ordinal) && !name.StartsWith("liblber", StringComparison.Ordinal)) return IntPtr.Zero;
                var candidates = name.StartsWith("liblber") ? new[] { name, "liblber.so.2", "liblber-2.5.so.0" } : new[] { name, "libldap.so.2", "libldap-2.5.so.0", "libldap-2.4.so.2" };
                foreach (var c in candidates)
                    if (System.Runtime.InteropServices.NativeLibrary.TryLoad(c, out var h)) return h;
                return IntPtr.Zero;
            });
        }
        catch (InvalidOperationException) { /* распознаватель уже задан — значит, сборка справляется сама */ }
    }

    private static LdapConnection Connect(AuthSettings s)
    {
        EnsureNative();
        var servers = s.AdServers.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var conn = new LdapConnection(new LdapDirectoryIdentifier(servers, s.AdPort, false, false))
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(10)
        };
        conn.SessionOptions.ProtocolVersion = 3;
        conn.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (s.AdSecurity == LdapSecurity.Ldaps) conn.SessionOptions.SecureSocketLayer = true;
        else if (s.AdSecurity == LdapSecurity.StartTls) conn.SessionOptions.StartTransportLayerSecurity(null);
        return conn;
    }

    private static IEnumerable<SearchResultEntry> Search(LdapConnection conn, string baseDn, string filter, params string[] attrs)
    {
        var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, attrs) { SizeLimit = 2000 };
        var resp = (SearchResponse)conn.SendRequest(req);
        foreach (SearchResultEntry e in resp.Entries) yield return e;
    }

    private static string Attr(SearchResultEntry e, string name) =>
        e.Attributes.Contains(name) && e.Attributes[name].Count > 0 ? e.Attributes[name][0]?.ToString() ?? "" : "";

    /// <summary>RFC 4515: экранирование значения в фильтре.</summary>
    private static string Escape(string v) => v.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
}
