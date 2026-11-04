using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;

namespace LinuxOps.Services;

public enum EditSaveChoice { Save, Later, Always }

public sealed class RemoteSaveResult
{
    public bool Saved { get; init; }
    public bool Conflict { get; init; }
    public long Size { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>Одна открытая правка удалённого файла. Следит за локальной копией и просит загрузить её назад.</summary>
public sealed class RemoteEditSession : IDisposable, INotifyPropertyChanged
{
    /// <summary>Редактор, который вышел быстрее, передал файл уже открытому окну (Notepad++, VS Code, Блокнот Win11).</summary>
    private static readonly TimeSpan HandOffWindow = TimeSpan.FromSeconds(5);

    private readonly Func<RemoteEditSession, EditSaveChoice> _askSave;
    private readonly Func<RemoteEditSession, RemoteSaveResult> _save;
    private readonly DateTime _started = DateTime.UtcNow;
    private FileSystemWatcher? _watch;
    private Process? _process;
    private long _token;
    private int _busy;
    private bool _closed;
    private bool _dirty;
    private string _state = "открыт";

    public RemoteEditSession(
        HostConfig host,
        string remotePath,
        string localPath,
        long remoteSize,
        DateTime remoteMtime,
        string editor,
        bool launchEditor,
        Func<RemoteEditSession, EditSaveChoice> askSave,
        Func<RemoteEditSession, RemoteSaveResult> save)
    {
        Host = host;
        HostLabel = host.Display;
        RemotePath = remotePath;
        LocalPath = localPath;
        RemoteSize = remoteSize;
        RemoteMtime = remoteMtime;
        _askSave = askSave;
        _save = save;
        Hash = FileHash(localPath);
        var dir = Path.GetDirectoryName(localPath)!;
        _watch = new FileSystemWatcher(dir, Path.GetFileName(localPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watch.Changed += OnFs;
        _watch.Created += OnFs;
        _watch.Renamed += OnFs;
        if (!launchEditor) return;
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = editor,
            Arguments = "\"" + localPath + "\"",
            UseShellExecute = true
        });
        if (_process is null) return;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Application.Current?.Dispatcher.BeginInvoke(OnEditorExited);
    }

    public HostConfig Host { get; }
    public string HostLabel { get; }
    public string RemotePath { get; }
    public string LocalPath { get; }
    public string Title => $"{RemotePath}  ·  {Host.Title}";
    public long RemoteSize { get; private set; }
    public DateTime RemoteMtime { get; private set; }
    public string Hash { get; private set; }
    public bool AlwaysSave { get; set; }

    public bool Dirty
    {
        get => _dirty;
        private set { _dirty = value; OnPropertyChanged(); }
    }

    public string State
    {
        get => _state;
        private set { _state = value; OnPropertyChanged(); }
    }

    public event Action<RemoteEditSession>? Closed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void NoteRemote(long size, DateTime mtime, string hash)
    {
        RemoteSize = size;
        RemoteMtime = mtime;
        Hash = hash;
        Dirty = false;
        State = "сохранён " + DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>Загрузить правку. ask=false — без вопроса (кнопка «Сохранить»). Возвращает true, если сохранено.</summary>
    public async Task<bool> Commit(bool ask = true)
    {
        if (_closed) return false;
        if (ask && !AlwaysSave)
        {
            var choice = _askSave(this);
            if (choice == EditSaveChoice.Later)
            {
                State = "изменён — не сохранён";
                return false;
            }

            if (choice == EditSaveChoice.Always) AlwaysSave = true;
        }

        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;
        try
        {
            State = "сохранение…";
            var result = await Task.Run(() => _save(this));
            if (!result.Saved)
                State = result.Conflict ? "не сохранён: файл на сервере изменился" : "не сохранён: " + Short(result.Detail);
            return result.Saved;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        if (_watch is not null)
        {
            _watch.EnableRaisingEvents = false;
            _watch.Dispose();
            _watch = null;
        }

        try
        {
            if (File.Exists(LocalPath)) File.Delete(LocalPath);
            var dir = Path.GetDirectoryName(LocalPath);
            if (dir is not null && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch { /* редактор ещё держит файл */ }

        Closed?.Invoke(this);
    }

    private async void OnEditorExited()
    {
        if (_closed) return;
        // Процесс вышел сразу — файл ушёл в уже открытое окно редактора. Правку не закрываем:
        // её закроет кнопка «Закрыть правку».
        if (DateTime.UtcNow - _started < HandOffWindow)
        {
            State = "открыт в редакторе";
            return;
        }

        if (Dirty && !await Commit()) return; // «Не сейчас» или ошибка — копия остаётся, правка в списке
        Dispose();
    }

    private void OnFs(object sender, FileSystemEventArgs e)
    {
        var token = Interlocked.Increment(ref _token);
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            if (token != Volatile.Read(ref _token) || _closed) return;
            if (!WaitReadable()) return;
            string hash;
            try { hash = FileHash(LocalPath); }
            catch { return; }
            if (string.Equals(hash, Hash, StringComparison.Ordinal)) return;
            var app = Application.Current;
            if (app is null) return;
            await app.Dispatcher.InvokeAsync(async () =>
            {
                Dirty = true;
                State = "изменён";
                await Commit();
            });
        });
    }

    private bool WaitReadable()
    {
        for (var i = 0; i < 25; i++)
        {
            try
            {
                using var fs = new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public static string FileHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static string LocalCopyPath(string host, string remotePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "LinuxOpsEdit", Safe(host), ShortHash(host + "|" + remotePath));
        Directory.CreateDirectory(root);
        return Path.Combine(root, Path.GetFileName(remotePath));
    }

    public static bool RemoteChanged(long openedSize, DateTime openedMtime, long nowSize, DateTime nowMtime)
        => openedSize != nowSize || Math.Abs((openedMtime - nowMtime).TotalSeconds) >= 2;

    public static bool IsSudoers(string remotePath)
        => remotePath == "/etc/sudoers" || remotePath.StartsWith("/etc/sudoers.d/", StringComparison.Ordinal);

    public static RemoteSaveResult SaveBack(
        SftpBrowser sftp,
        HostConfig host,
        bool sudo,
        string localPath,
        string remotePath,
        long openedSize,
        DateTime openedMtime,
        Func<bool> confirmConflict)
    {
        var size = new FileInfo(localPath).Length;
        var now = sftp.Stat(remotePath);
        if (RemoteChanged(openedSize, openedMtime, now.Size, now.Mtime) && !confirmConflict())
            return new RemoteSaveResult { Conflict = true, Detail = "отменено: файл на сервере изменился", Size = size };

        // sudoers всегда через visudo -cf, даже под root без sudo.
        if (sudo || IsSudoers(remotePath))
        {
            var staged = sftp.Stage(localPath);
            var detail = ApplyElevated(host, staged, remotePath);
            return new RemoteSaveResult { Saved = detail.StartsWith("загружено", StringComparison.Ordinal), Detail = detail, Size = size };
        }

        try
        {
            var note = sftp.WriteInPlace(localPath, remotePath);
            return new RemoteSaveResult { Saved = true, Detail = "загружено, " + note, Size = size };
        }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException)
        {
            return new RemoteSaveResult
            {
                Detail = "Нет прав на запись. Выберите «Права: sudo» и нажмите «Сохранить» у этой правки.",
                Size = size
            };
        }
    }

    /// <summary>Чтение файла, который пользователь SFTP не может открыть. Копия в /tmp: владелец — пользователь SSH, права 600.</summary>
    public static void DownloadElevated(SftpBrowser sftp, HostConfig host, string remote, string local)
    {
        var staged = "/tmp/linuxops-read-" + Guid.NewGuid().ToString("N");
        using var ssh = SshRunner.Connect(host);
        var result = ssh.RunAsRoot("f4-read", """
            set -euo pipefail
            umask 077
            cp -- "$LINUXOPS_DST" "$LINUXOPS_SRC"
            chown -- "$LINUXOPS_USER" "$LINUXOPS_SRC"
            chmod 600 -- "$LINUXOPS_SRC"
            """, 30, host, new Dictionary<string, string>
        {
            ["LINUXOPS_SRC"] = staged,
            ["LINUXOPS_DST"] = remote,
            ["LINUXOPS_USER"] = host.Username
        });
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StdErr) ? "Не удалось прочитать файл" : result.StdErr.Trim());
        try
        {
            sftp.Download(staged, local);
        }
        finally
        {
            try { sftp.Delete(staged); }
            catch
            {
                // Последняя попытка — через sudo, чтобы копия не осталась в /tmp.
                ssh.RunAsRoot("f4-clean", "rm -f -- \"$LINUXOPS_SRC\"", 15, host,
                    new Dictionary<string, string> { ["LINUXOPS_SRC"] = staged });
            }
        }
    }

    /// <summary>
    /// Записать временный файл на место через root: прежняя версия — в /root/.linuxops-bak,
    /// затем cat поверх (inode, права, владелец, метки SELinux сохраняются).
    /// </summary>
    public static string ApplyElevated(HostConfig host, string staged, string dest)
    {
        const string script = """
            set -euo pipefail
            trap 'rm -f -- "$LINUXOPS_SRC"' EXIT
            case "$LINUXOPS_DST" in
              /etc/sudoers|/etc/sudoers.d/*)
                visudo -cf "$LINUXOPS_SRC"
                ;;
            esac
            b="$(getent passwd root | cut -d: -f6)/.linuxops-bak"
            install -d -m 700 -- "$b"
            cp --preserve=mode,ownership -- "$LINUXOPS_DST" "$b/$LINUXOPS_BAK"
            cat -- "$LINUXOPS_SRC" > "$LINUXOPS_DST"
            echo "$b/$LINUXOPS_BAK"
            """;
        using var ssh = SshRunner.Connect(host);
        var result = ssh.RunAsRoot("f4-save", script, 60, host, new Dictionary<string, string>
        {
            ["LINUXOPS_SRC"] = staged,
            ["LINUXOPS_DST"] = dest,
            ["LINUXOPS_BAK"] = SftpBrowser.BackupName(dest)
        });
        if (result.ExitCode == 0) return "загружено, копия " + result.StdOut.Trim().Split('\n').Last();
        var text = (result.StdErr + "\n" + result.StdOut).Trim();
        return string.IsNullOrWhiteSpace(text) ? "ошибка загрузки, exit " + result.ExitCode : text;
    }

    public static EditSaveChoice AskSave(Window owner, string path, string host)
    {
        var choice = EditSaveChoice.Later;
        var window = new Window
        {
            Title = "Сохранить на сервер",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Сохранить {path} на {host}?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string caption, EditSaveChoice value, bool isDefault = false)
        {
            var button = new Button { Content = caption, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), IsDefault = isDefault };
            button.Click += (_, _) =>
            {
                choice = value;
                window.DialogResult = true;
            };
            row.Children.Add(button);
        }

        Add("Сохранить", EditSaveChoice.Save, isDefault: true);
        Add("Не сейчас", EditSaveChoice.Later);
        Add("Сохранять без вопроса", EditSaveChoice.Always);
        panel.Children.Add(row);
        window.Content = panel;
        window.ShowDialog();
        return choice;
    }

    public static bool AskConflict(Window owner)
        => AppDialog.Confirm(owner, "Файл на сервере изменился после открытия.\nПерезаписать его вашей версией?", "Конфликт");

    private static string Short(string text)
    {
        var line = text.Replace("\r", "").Split('\n')[0];
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    private static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => bad.Contains(c) ? '_' : c).ToArray();
        var text = new string(chars);
        return string.IsNullOrWhiteSpace(text) ? "host" : text;
    }

    private static string ShortHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
