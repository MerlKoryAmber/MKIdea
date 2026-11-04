using System.Diagnostics;
using System.IO;
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
public sealed class RemoteEditSession : IDisposable
{
    private readonly Func<RemoteEditSession, EditSaveChoice> _askSave;
    private readonly Func<RemoteEditSession, bool> _askConflict;
    private readonly Func<RemoteEditSession, RemoteSaveResult> _save;
    private FileSystemWatcher? _watch;
    private Process? _process;
    private long _token;
    private int _busy;
    private bool _closed;

    public RemoteEditSession(
        string hostLabel,
        string remotePath,
        string localPath,
        long remoteSize,
        DateTime remoteMtime,
        string editor,
        bool launchEditor,
        Func<RemoteEditSession, EditSaveChoice> askSave,
        Func<RemoteEditSession, bool> askConflict,
        Func<RemoteEditSession, RemoteSaveResult> save)
    {
        HostLabel = hostLabel;
        RemotePath = remotePath;
        LocalPath = localPath;
        RemoteSize = remoteSize;
        RemoteMtime = remoteMtime;
        _askSave = askSave;
        _askConflict = askConflict;
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
        var psi = new ProcessStartInfo
        {
            FileName = editor,
            Arguments = "\"" + localPath + "\"",
            UseShellExecute = false
        };
        _process = Process.Start(psi);
        if (_process is null) return;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            var app = Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(async () =>
            {
                if (Dirty) await Commit(fromClose: true);
                Dispose();
            });
        };
    }

    public string HostLabel { get; }
    public string RemotePath { get; }
    public string LocalPath { get; }
    public string Title => RemotePath;
    public long RemoteSize { get; private set; }
    public DateTime RemoteMtime { get; private set; }
    public string Hash { get; private set; }
    public bool Dirty { get; private set; }
    public bool AlwaysSave { get; set; }

    public event Action<RemoteEditSession>? Closed;

    public void NoteRemote(long size, DateTime mtime, string hash)
    {
        RemoteSize = size;
        RemoteMtime = mtime;
        Hash = hash;
        Dirty = false;
    }

    public async Task Commit(bool fromClose)
    {
        if (_closed) return;
        if (!Dirty && fromClose) return;
        var choice = AlwaysSave ? EditSaveChoice.Always : _askSave(this);
        if (choice == EditSaveChoice.Later) return;
        if (choice == EditSaveChoice.Always) AlwaysSave = true;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var result = await Task.Run(() => _save(this));
            if (result.Saved)
            {
                Dirty = false;
                try { Hash = FileHash(LocalPath); } catch { /* файл уже закрыт */ }
            }
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
            Dirty = true;
            var app = Application.Current;
            if (app is null)
            {
                await Commit(fromClose: false);
                return;
            }

            await app.Dispatcher.InvokeAsync(async () => await Commit(fromClose: false));
        });
    }

    private bool WaitReadable()
    {
        for (var i = 0; i < 25; i++)
        {
            try
            {
                using var fs = new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length >= 0) return true;
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
        var root = Path.Combine(Path.GetTempPath(), "LinuxOpsEdit", Safe(host), ShortHash(remotePath));
        Directory.CreateDirectory(root);
        return Path.Combine(root, Path.GetFileName(remotePath));
    }

    public static bool RemoteChanged(long openedSize, DateTime openedMtime, long nowSize, DateTime nowMtime)
        => openedSize != nowSize || Math.Abs((openedMtime - nowMtime).TotalSeconds) >= 2;

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

        string detail;
        if (sudo || !sftp.CanCreateIn(Parent(remotePath)))
        {
            var staged = sftp.Stage(localPath);
            detail = ApplyElevated(host, staged, remotePath);
        }
        else
        {
            sftp.UploadReplace(localPath, remotePath);
            detail = "загружено";
        }

        var ok = detail == "загружено";
        return new RemoteSaveResult { Saved = ok, Detail = detail, Size = size, Conflict = false };
    }

    public static void DownloadElevated(SftpBrowser sftp, HostConfig host, string remote, string local)
    {
        var staged = "/tmp/linuxops-read-" + Guid.NewGuid().ToString("N");
        using var ssh = SshRunner.Connect(host);
        var result = ssh.Run("f4-read", """
            set -euo pipefail
            cp -a -- "$LINUXOPS_DST" "$LINUXOPS_SRC"
            chmod 644 -- "$LINUXOPS_SRC"
            """, 30, sudo: true, host, new Dictionary<string, string>
        {
            ["LINUXOPS_SRC"] = staged,
            ["LINUXOPS_DST"] = remote
        });
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StdErr) ? "Не удалось прочитать файл" : result.StdErr.Trim());
        try { sftp.Download(staged, local); }
        finally { try { sftp.Delete(staged); } catch { /* временный файл уже снят */ } }
    }

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
            cp --preserve=mode,ownership -- "$LINUXOPS_DST" "$LINUXOPS_DST.linuxops.bak"
            cat -- "$LINUXOPS_SRC" > "$LINUXOPS_DST"
            """;
        using var ssh = SshRunner.Connect(host);
        var result = ssh.Run("f4-save", script, 60, sudo: true, host, new Dictionary<string, string>
        {
            ["LINUXOPS_SRC"] = staged,
            ["LINUXOPS_DST"] = dest
        });
        if (result.ExitCode == 0) return "загружено";
        var text = (result.StdErr + "\n" + result.StdOut).Trim();
        return string.IsNullOrWhiteSpace(text) ? "ошибка загрузки, exit " + result.ExitCode : text;
    }

    public static EditSaveChoice AskSave(Window owner, string path, string host)
    {
        var choice = EditSaveChoice.Later;
        var window = new Window
        {
            Title = "Сохранить",
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
        void Add(string caption, EditSaveChoice value)
        {
            var button = new Button { Content = caption, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90, Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (_, _) =>
            {
                choice = value;
                window.DialogResult = true;
            };
            row.Children.Add(button);
        }

        Add("Сохранить", EditSaveChoice.Save);
        Add("Не сейчас", EditSaveChoice.Later);
        Add("Сохранять без вопроса для этого файла", EditSaveChoice.Always);
        panel.Children.Add(row);
        window.Content = panel;
        window.ShowDialog();
        return choice;
    }

    public static bool AskConflict(Window owner)
    {
        var overwrite = false;
        var window = new Window
        {
            Title = "Конфликт",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Файл на сервере изменился после открытия",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var yes = new Button { Content = "Перезаписать", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4) };
        var no = new Button { Content = "Отменить", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), IsCancel = true };
        yes.Click += (_, _) => { overwrite = true; window.DialogResult = true; };
        no.Click += (_, _) => { window.DialogResult = false; };
        row.Children.Add(yes);
        row.Children.Add(no);
        panel.Children.Add(row);
        window.Content = panel;
        window.ShowDialog();
        return overwrite;
    }

    private static string Parent(string remote)
    {
        var cut = remote.TrimEnd('/');
        var idx = cut.LastIndexOf('/');
        return idx <= 0 ? "/" : cut[..idx];
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
}
