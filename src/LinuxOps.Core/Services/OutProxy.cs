using System.Net;
using System.Text.RegularExpressions;

namespace LinuxOps.Services;

/// <summary>
/// Прокси для выхода в интернет с машины, где работает LinuxOps: сверка CVE (NVD, Red Hat, KEV, EPSS, выпуски) и туннель
/// «списки обновлений через сервер/ПК». Настольная — системный прокси Windows (по умолчанию). Веб — только заданный
/// в ⚙ (<see cref="Configure"/>): системный не подхватывается, не задан — напрямую.
/// </summary>
public static partial class OutProxy
{
    // прокси и его адрес меняются вместе (одна ссылка) — сообщение «через …» не разойдётся с тем, куда шли
    private sealed record State(IWebProxy Proxy, string Address);
    private static volatile State? _set;

    /// <summary>Задан явно (веб): системный прокси не используется.</summary>
    public static bool Explicit => _set is not null;
    /// <summary>host:port заданного прокси; пусто — напрямую или системный.</summary>
    public static string Address => _set?.Address ?? "";

    public static IWebProxy Current => _set?.Proxy ?? HttpClient.DefaultProxy;

    /// <summary>Для HttpClient: каждый запрос спрашивает текущий прокси — смена в ⚙ действует без перезапуска.</summary>
    public static IWebProxy Dynamic { get; } = new DynamicProxy();

    /// <summary>Пусто — напрямую. Возвращает текст ошибки или null.</summary>
    public static string? Configure(string address, string bypass)
    {
        if (Parse(address, bypass, out var proxy, out var authority) is { } err) return err;
        _set = new State(proxy, authority);
        return null;
    }

    /// <summary>Проверка ввода без применения: null — годится.</summary>
    public static string? Validate(string address, string bypass) => Parse(address, bypass, out _, out _);

    private static string? Parse(string address, string bypass, out IWebProxy proxy, out string authority)
    {
        proxy = new WebProxy(); // без адреса — напрямую
        authority = "";
        address = address.Trim();
        if (address.Length == 0) return null;
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var u) || u.Host.Length == 0)
            return "Адрес прокси не разобран — пример: proxy.corp.local:3128";
        if (u.Scheme != Uri.UriSchemeHttp) return "Поддерживается только HTTP-прокси (http://…).";
        if (u.UserInfo.Length > 0) return "Логин и пароль в адресе прокси не поддерживаются.";
        var masks = Masks(bypass);
        if (masks.FirstOrDefault(m => !MaskRx().IsMatch(m)) is { } bad) return $"Исключение «{bad}» не разобрано — имена или маски: *.corp.local, 10.1.2.3";
        proxy = new WebProxy(new Uri($"http://{u.Authority}/"))
        {
            BypassProxyOnLocal = true,
            BypassList = masks.Select(ToRegex).ToArray()
        };
        authority = u.Authority;
        return null;
    }

    public static List<string> Masks(string bypass) =>
        bypass.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // *.corp.local, .corp.local (то же), host, 10.1.2.3, 10.1.* — без CIDR. WebProxy сверяет маску со строкой
    // «схема://хост[:порт]», поэтому схема и порт — необязательные части шаблона; «*» не переходит через «/».
    private static string ToRegex(string mask)
    {
        if (mask.StartsWith('.')) mask = "*" + mask;
        return @"^(https?://)?" + Regex.Escape(mask).Replace(@"\*", "[^/]*") + @"(:\d+)?/?$";
    }

    [GeneratedRegex(@"^[A-Za-z0-9*.\-]+$")]
    private static partial Regex MaskRx();

    /// <summary>Для сообщений об ошибке связи: через что шли.</summary>
    public static string Note => !Explicit ? ""
        : Address.Length == 0 ? "прокси сервера не задан — напрямую (⚙ → «Выход в интернет»)"
        : $"через прокси {Address}";

    private sealed class DynamicProxy : IWebProxy
    {
        public ICredentials? Credentials { get => Current.Credentials; set { } }
        public Uri? GetProxy(Uri destination) => Current.GetProxy(destination);
        public bool IsBypassed(Uri host) => Current.IsBypassed(host);
    }
}
