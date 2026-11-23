using System.Diagnostics;
using LinuxOps.Services;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>Прокси выхода сервера в интернет (0.7.9): только из ⚙, системный не подхватывается; пусто — напрямую.</summary>
public static class NetProxy
{
    public const string AddressKey = "net.proxy";
    public const string BypassKey = "net.bypass";

    public static string Address(WebStore store) => store.GetSetting(AddressKey) ?? "";
    public static string Bypass(WebStore store) => store.GetSetting(BypassKey) ?? "";

    /// <summary>При старте службы: сохранённое значение испорчено — напрямую и запись в журнал.</summary>
    public static void Apply(WebStore store)
    {
        if (OutProxy.Configure(Address(store), Bypass(store)) is { } err)
        {
            OutProxy.Configure("", "");
            CrashLog.Write("Прокси выхода в интернет из ⚙ не применён — напрямую", new InvalidDataException(err));
        }
    }
}

public sealed partial class WebSession
{
    public string NetProxyAddress => NetProxy.Address(_store);
    public string NetProxyBypass => NetProxy.Bypass(_store);

    /// <summary>Сохранить прокси (право «Настройки»). Итог — текстом.</summary>
    public string SetNetProxy(string address, string bypass)
    {
        if (!Allowed(Perm.Settings, "настройки: выход в интернет")) return "Нет права «Настройки».";
        address = address.Trim();
        bypass = string.Join(", ", OutProxy.Masks(bypass));
        if (OutProxy.Validate(address, bypass) is { } err) return err;
        if (address == NetProxyAddress && bypass == NetProxyBypass) return "Без изменений.";
        _store.PutSetting(NetProxy.AddressKey, address);
        _store.PutSetting(NetProxy.BypassKey, bypass);
        OutProxy.Configure(address, bypass);
        _audit.Write(Who, "настройки: выход в интернет", address.Length == 0 ? "напрямую" : $"прокси {OutProxy.Address}" + (bypass.Length > 0 ? $", кроме {bypass}" : ""));
        return address.Length == 0 ? "Сохранено: напрямую, без прокси." : $"Сохранено: через {OutProxy.Address}.";
    }

    /// <summary>Проверка связи с базами — тем же путём, что сверка CVE. Ничего не сохраняет.</summary>
    public async Task<string> ProbeNetProxy()
    {
        if (!Allowed(Perm.Settings, "настройки: проверить выход в интернет")) return "Нет права «Настройки».";
        var sw = Stopwatch.StartNew();
        var r = await CveCheck.Probe();
        return $"{r} ({sw.Elapsed.TotalSeconds:0.0} с, {(OutProxy.Address.Length > 0 ? "через " + OutProxy.Address : "напрямую")})";
    }
}
