using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LinuxOps.Views;

/// <summary>Именованные настройки клиента RDP (Remote Credential Guard, Restricted Admin, масштаб). IID и порядок методов — из библиотеки типов mstscax.dll.</summary>
[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpExtendedSettings
{
    void PutProperty([MarshalAs(UnmanagedType.BStr)] string name, [In] ref object value);
    [return: MarshalAs(UnmanagedType.Struct)]
    object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
}

/// <summary>События mstscax (dispinterface IMsTscAxEvents). Номера — из библиотеки типов; остальные события не нужны.</summary>
[ComVisible(true), Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(22)] void OnLogonError(int lError);
    [DispId(33)] void OnAutoReconnected();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink : IMsTscAxEvents
{
    public event Action? Connecting;
    public event Action? Connected;
    public event Action? LoginComplete;
    public event Action<int>? Disconnected;
    public event Action<bool>? FullScreenChanged;
    public event Action<int>? FatalError;
    public event Action<int>? LogonError;
    public event Action? AutoReconnected;

    public void OnConnecting() => Connecting?.Invoke();
    public void OnConnected() => Connected?.Invoke();
    public void OnLoginComplete() => LoginComplete?.Invoke();
    public void OnDisconnected(int discReason) => Disconnected?.Invoke(discReason);
    public void OnEnterFullScreenMode() => FullScreenChanged?.Invoke(true);
    public void OnLeaveFullScreenMode() => FullScreenChanged?.Invoke(false);
    public void OnFatalError(int errorCode) => FatalError?.Invoke(errorCode);
    public void OnWarning(int warningCode) { }
    public void OnLogonError(int lError) => LogonError?.Invoke(lError);
    public void OnAutoReconnected() => AutoReconnected?.Invoke();
}

/// <summary>
/// Встроенный клиент RDP Windows (mstscax.dll) — тот же движок, что у mstsc, из той Windows, где запущена программа.
/// Свойства — поздним связыванием (dual-интерфейсы IMsRdpClient*), события — через точку подключения.
/// </summary>
[System.ComponentModel.DesignerCategory("code")]
public sealed class RdpControl : AxHost
{
    /// <summary>
    /// Версии класса клиента, от новой к старой. Нужное программе (AdvancedSettings9, UpdateSessionDisplaySettings)
    /// есть с MsRdpClient9 (Windows 8.1). Набор классов зависит от сборки Windows.
    /// </summary>
    private static readonly (string Name, string Clsid)[] Classes =
    {
        ("MsRdpClient10", "a0c63c30-f08d-4ab4-907c-34905d770c7d"),
        ("MsRdpClient9", "8b918b82-7985-4c24-89df-c33ad2bbfbcd")
    };

    private static (string Name, string Clsid)? _picked;

    /// <summary>Какой класс клиента выбран на этой машине (для строки ошибки и отчёта).</summary>
    public static string ClassName => Pick().Name;

    /// <summary>
    /// Первый класс, который реально создаётся. Одной записи в реестре мало: в Windows 10 1809 CLSID MsRdpClient10
    /// зарегистрирован, а mstscax.dll его не знает (CLASS_E_CLASSNOTAVAILABLE). Пробный объект сразу освобождается.
    /// </summary>
    private static (string Name, string Clsid) Pick()
    {
        if (_picked is { } p) return p;
        var candidates = Classes;
#if DEBUG
        // Проверка на машине разработчика: LINUXOPS_RDP_CLASS=9 — сразу MsRdpClient9; broken — «MsRdpClient10» не создаётся,
        // должен быть выбран MsRdpClient9 (как в Windows 10 1809); none — не создаётся ни один.
        switch (Environment.GetEnvironmentVariable("LINUXOPS_RDP_CLASS"))
        {
            case "9": candidates = new[] { Classes[1] }; break;
            case "broken": candidates = new[] { ("MsRdpClient10", "00000000-0000-0000-0000-00000000dead"), Classes[1] }; break;
            case "none": candidates = new[] { ("MsRdpClient10", "00000000-0000-0000-0000-00000000dead") }; break;
        }
#endif
        var failures = new List<string>();
        foreach (var c in candidates)
        {
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid(c.Clsid), throwOnError: true)!;
                var probe = Activator.CreateInstance(type);
                if (probe is not null && Marshal.IsComObject(probe)) Marshal.FinalReleaseComObject(probe);
                if (failures.Count > 0)
                    Services.CrashLog.Write($"RDP: выбран {c.Name}; не создаются: {string.Join("; ", failures)}", null);
                return (_picked = c).Value;
            }
            catch (Exception ex)
            {
                failures.Add($"{c.Name} (0x{ex.HResult:X8})");
            }
        }
        Services.CrashLog.Write($"RDP: ни один клиент не создаётся: {string.Join("; ", failures)}", null);
        throw new InvalidOperationException(
            $"В этой Windows не создаётся клиент удалённого рабочего стола ({string.Join(", ", failures)}). Используйте «Открыть в mstsc».");
    }

    private ConnectionPointCookie? _cookie;

    public RdpControl() : base(Pick().Clsid)
    {
    }

    public RdpEventSink Sink { get; } = new();

    /// <summary>Объект клиента (IMsRdpClient9/10) для позднего связывания. Есть после создания окна.</summary>
    public dynamic Client => GetOcx() ?? throw new InvalidOperationException("Клиент RDP ещё не создан.");

    public void SetExtended(string name, object value)
    {
        if (GetOcx() is IMsRdpExtendedSettings ext) ext.PutProperty(name, ref value);
        else throw new InvalidOperationException("Клиент RDP не поддерживает расширенные настройки.");
    }

    protected override void CreateSink()
    {
        try
        {
            _cookie = new ConnectionPointCookie(GetOcx()!, Sink, typeof(IMsTscAxEvents));
        }
        catch
        {
            _cookie = null;
        }
    }

    protected override void DetachSink()
    {
        try
        {
            _cookie?.Disconnect();
        }
        catch
        {
            // control уже разрушен
        }

        _cookie = null;
    }
}
