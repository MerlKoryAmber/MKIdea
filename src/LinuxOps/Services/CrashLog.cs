using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace LinuxOps.Services;

/// <summary>
/// Журнал ошибок LinuxOps.crash.log рядом с exe. Ошибка .NET в окне — запись в журнал и сообщение, программа
/// продолжает работать. Падение в чужом нативном коде (ActiveX RDP и т. п.) не остановить, но в журнал попадут
/// код исключения, модуль и, для отложенной загрузки DLL, какой функции не хватило.
/// </summary>
public static class CrashLog
{
    public static string LogPath { get; } = Path.Combine(
        Path.GetDirectoryName(DefaultsStore.DefaultsPath) ?? AppContext.BaseDirectory, "LinuxOps.crash.log");

    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherException;
        // Ошибки внутри WinForms (встроенный RDP) — не английское окно WinForms, а журнал и наше сообщение.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => Report("Ошибка во встроенном компоненте", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Необработанная ошибка, программа закрывается", e.ExceptionObject as Exception, includeNative: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Ошибка фоновой задачи", e.Exception);
            e.SetObserved();
        };
        NativeTrap.Install();
    }

    private static bool _showing;

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Report("Ошибка в окне", e.Exception);
    }

    private static void Report(string what, Exception ex)
    {
        Write(what, ex);
        if (_showing) return;
        _showing = true;
        try
        {
            var owner = Application.Current?.MainWindow;
            var text = $"Произошла ошибка: {ex.Message}\n\nПрограмма продолжает работать. Подробности — в журнале:\n{LogPath}";
            if (owner is { IsLoaded: true }) AppDialog.Info(owner, text, "Ошибка");
            else MessageBox.Show(text, "LinuxOps — ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // окно ошибки не открылось — запись в журнале уже есть
        }
        finally
        {
            _showing = false;
        }
    }

    public static void Write(string what, Exception? ex, bool includeNative = false)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {what}");
            sb.AppendLine($"LinuxOps {typeof(CrashLog).Assembly.GetName().Version} · {RuntimeInformation.OSDescription} · {Environment.OSVersion.Version} · .NET {Environment.Version}");
            if (includeNative && NativeTrap.Last is { } native) sb.AppendLine("Нативное исключение: " + native);
            if (ex is not null) sb.AppendLine(ex.ToString());
            lock (Gate)
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, sb.ToString() + Environment.NewLine);
            }
        }
        catch
        {
            // журнал не записался — молча: ошибка журнала не должна ронять программу
        }
    }

    /// <summary>
    /// Запоминает последнее нативное исключение (не .NET): код, модуль, для отложенной загрузки — DLL и функцию.
    /// Только запоминает в памяти, в файл — при падении. Само исключение не обрабатывает.
    /// </summary>
    private static class NativeTrap
    {
        private delegate int Handler(IntPtr pointers);
        private static Handler? _keep;
        public static string? Last { get; private set; }

        [DllImport("kernel32.dll")] private static extern IntPtr AddVectoredExceptionHandler(uint first, Handler h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder name, int size);

        private const uint ClrException = 0xE0434352;
        private const uint CppException = 0xE06D7363;

        public static void Install()
        {
            _keep = Trap;
            AddVectoredExceptionHandler(0, _keep);
        }

        private static int Trap(IntPtr pointers)
        {
            try
            {
                var rec = Marshal.ReadIntPtr(pointers);
                var code = (uint)Marshal.ReadInt32(rec);
                // .NET-исключения видны в журнале и так; C++-исключения и отладочные коды — внутренние дела библиотек.
                if (code is ClrException or CppException or 0x40010006 or 0x4001000A or 0x406D1388) return 0;
                var address = Marshal.ReadIntPtr(rec, 16);
                var text = $"0x{code:X8} в {Module(address)}";
                if (code is 0xC06D007E or 0xC06D007F)
                {
                    // DelayLoadInfo: szDll +24, dlp.fImportByName +32, dlp.szProcName/dwOrdinal +40
                    var info = Marshal.ReadIntPtr(rec, 32);
                    var dll = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(info, 24));
                    var proc = Marshal.ReadInt32(info, 32) != 0
                        ? Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(info, 40))
                        : "#" + Marshal.ReadInt32(info, 40);
                    text += $" · отложенная загрузка: {dll}!{proc}" + (code == 0xC06D007E ? " (нет DLL)" : " (нет функции)");
                }
                Last = text;
            }
            catch
            {
                // в обработчике исключений — ничего не бросать
            }
            return 0; // EXCEPTION_CONTINUE_SEARCH: исключение идёт дальше как обычно
        }

        private static string Module(IntPtr address)
        {
            // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | UNCHANGED_REFCOUNT
            if (!GetModuleHandleEx(0x4 | 0x2, address, out var module) || module == IntPtr.Zero) return "?";
            var sb = new StringBuilder(520);
            GetModuleFileName(module, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
