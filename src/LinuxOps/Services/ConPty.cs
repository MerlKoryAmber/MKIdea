using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LinuxOps.Services;

/// <summary>
/// Псевдоконсоль Windows (ConPTY, Windows 10 1809+): процесс думает, что у него настоящая консоль, а её вывод приходит
/// потоком VT (как от ssh), ввод уходит VT. Так работает терминал VS Code и Windows Terminal.
/// </summary>
public sealed class ConPty : IDisposable
{
    private IntPtr _hpc;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Process _proc;
    private int _disposed;

    private ConPty(IntPtr hpc, FileStream input, FileStream output, Process proc)
    {
        _hpc = hpc;
        _input = input;
        _output = output;
        _proc = proc;
    }

    public Process Process => _proc;
    public Stream Output => _output;

    /// <summary>Запустить командную строку в новой псевдоконсоли cols×rows.</summary>
    public static ConPty Start(string commandLine, string workingDirectory, int cols, int rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        var hr = CreatePseudoConsole(new Coord((short)cols, (short)rows), inRead, outWrite, 0, out var hpc);
        // Концы, переданные псевдоконсоли, ей и принадлежат.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0)
        {
            inWrite.Dispose();
            outRead.Dispose();
            throw new Win32Exception(hr, "Псевдоконсоль Windows недоступна (нужна Windows 10 1809 или новее)");
        }

        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrs = Marshal.AllocHGlobal(attrSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref attrSize)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)ProcThreadAttributePseudoConsole, hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            var si = new StartupInfoEx { lpAttributeList = attrs };
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            // Пустые стандартные дескрипторы: иначе процесс унаследует перенаправленный вывод родителя мимо псевдоконсоли.
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref si, out var pi))
            {
                var err = new Win32Exception();
                ClosePseudoConsole(hpc);
                inWrite.Dispose();
                outRead.Dispose();
                throw err;
            }

            CloseHandle(pi.hThread);
            Process proc;
            try { proc = Process.GetProcessById(pi.dwProcessId); }
            finally { CloseHandle(pi.hProcess); }

            return new ConPty(hpc, new FileStream(inWrite, FileAccess.Write, 1), new FileStream(outRead, FileAccess.Read, 1), proc);
        }
        finally
        {
            DeleteProcThreadAttributeList(attrs);
            Marshal.FreeHGlobal(attrs);
        }
    }

    public void Write(string data)
    {
        if (_disposed != 0 || data.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(data);
        try
        {
            _input.Write(bytes, 0, bytes.Length);
            _input.Flush();
        }
        catch (IOException) { /* процесс завершился */ }
        catch (ObjectDisposedException) { }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed != 0 || _hpc == IntPtr.Zero) return;
        ResizePseudoConsole(_hpc, new Coord((short)cols, (short)rows));
    }

    /// <summary>Закрыть консоль и всё, что в ней запущено (вместе с внешними программами).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); }
        catch { /* уже завершён */ }
        var hpc = _hpc;
        _hpc = IntPtr.Zero;
        // ClosePseudoConsole ждёт, пока вывод дочитают, — закрываем в фоне, поток чтения ещё работает.
        Task.Run(() =>
        {
            if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
            try { _input.Dispose(); } catch { }
            try { _output.Dispose(); } catch { }
        });
    }

    // ---------- Win32 ----------

    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseStdHandles = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        public readonly short X;
        public readonly short Y;
        public Coord(short x, short y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hpc, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hpc);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pAttr, IntPtr tAttr, bool inherit, uint flags,
        IntPtr env, string? cwd, ref StartupInfoEx si, out ProcessInformation pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}
