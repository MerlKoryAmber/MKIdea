using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.ViewModels;

public partial class SshFilesViewModel : ObservableObject, IDisposable
{
    private readonly AppSession _session;
    private SftpBrowser? _sftp;
    private int _suppressDrive;
    private bool _suppressHost;

    public SshFilesViewModel(AppSession session)
    {
        _session = session;
        LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public ObservableCollection<HostConfig> Hosts => _session.Hosts;
    public ObservableCollection<FileEntry> LocalFiles { get; } = new();
    public ObservableCollection<FileEntry> RemoteFiles { get; } = new();
    public ObservableCollection<string> Drives { get; } = new();

    public Func<Window?>? Owner { get; set; }

    /// <summary>text, replaceWhole</summary>
    public event Action<string, bool>? TerminalWrite;
    public event Action? ClearInput;
    public event Action? FocusInput;

    public FileEntry? SelectedLocal { get; set; }
    public FileEntry? SelectedRemote { get; set; }

    [ObservableProperty] private HostConfig? selectedHost;
    [ObservableProperty] private bool sudo;
    [ObservableProperty] private bool activeLocal = true;
    [ObservableProperty] private string localPath;
    [ObservableProperty] private string remotePath = "";
    [ObservableProperty] private string? selectedDrive;
    [ObservableProperty] private string termPrompt = "$";
    [ObservableProperty] private string termHostLabel = "";

    public void Init()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
            Drives.Add(d.Name.TrimEnd('\\'));
        ReloadLocal();
        UpdatePrompt();
    }

    public void EnterDocuments()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(docs) && !string.Equals(LocalPath, docs, StringComparison.OrdinalIgnoreCase))
        {
            LocalPath = docs;
            ReloadLocal();
        }

        if (SelectedHost is null && Hosts.Count > 0)
        {
            _suppressHost = true;
            SelectedHost = Hosts.FirstOrDefault(h => h.IsSelected) ?? Hosts[0];
            _suppressHost = false;
            UpdatePrompt();
        }
    }

    partial void OnSelectedHostChanged(HostConfig? value)
    {
        if (_suppressHost) return;
        CloseSftp();
        RemotePath = "";
        RemoteFiles.Clear();
        Term("Подключитесь к хосту (Connect).", replace: true);
        UpdatePrompt();
    }

    partial void OnSudoChanged(bool value) => UpdatePrompt();

    partial void OnSelectedDriveChanged(string? value)
    {
        if (_suppressDrive > 0 || string.IsNullOrWhiteSpace(value)) return;
        var path = value.EndsWith(':') ? value + "\\" : value + "\\";
        if (!Directory.Exists(path)) return;
        if (LocalPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return;
        LocalPath = path;
        ReloadLocal();
    }

    public void UpdatePrompt()
    {
        if (SelectedHost is HostConfig h)
        {
            var mark = Sudo ? "#" : "$";
            TermPrompt = $"{h.Username}@{h.Host}:~{mark}";
            TermHostLabel = $"{h.Display} · {(Sudo ? "sudo" : "user")}";
        }
        else
        {
            TermPrompt = Sudo ? "#" : "$";
            TermHostLabel = "";
        }
    }

    public void Connect()
    {
        if (SelectedHost is not HostConfig host)
        {
            AppDialog.Info(OwnerWin(), "Выберите хост.", "SSH Files");
            return;
        }

        try
        {
            CloseSftp();
            _sftp = SftpBrowser.Connect(host);
            host.Status = "Online";
            try { _sftp.ChangeDirectory("/etc"); }
            catch
            {
                try { _sftp.ChangeDirectory(_sftp.WorkingDirectory); }
                catch { /* leave cwd */ }
            }

            RefreshRemote();
            UpdatePrompt();
            Term("", replace: true);
            var mark = Sudo ? "#" : "$";
            using var ssh = SshRunner.Connect(host);
            var d = ssh.DetectDistro();
            host.DistroHint = d.Id;
            _session.Persist();

            string? sudoPass = null;
            if (Sudo && string.Equals(host.Auth, "Password", StringComparison.OrdinalIgnoreCase))
                sudoPass = host.Password;
            var cwd = _sftp.WorkingDirectory;
            var lsCmd = $"ls -la -- '{cwd.Replace("'", "'\\''")}' | head -n 15";
            var r = ssh.Run("term", lsCmd, 45, Sudo, sudoPass);
            Term($"{host.Username}@{host.Host}:{cwd}{mark} {lsCmd}\n");
            if (!string.IsNullOrWhiteSpace(r.StdOut))
                Term(r.StdOut.TrimEnd() + "\n");
            if (!string.IsNullOrWhiteSpace(r.StdErr))
                Term(r.StdErr.TrimEnd() + "\n");
            FocusInput?.Invoke();
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "SSH Files");
        }
    }

    public void Refresh()
    {
        ReloadLocal();
        if (_sftp is not null) RefreshRemote();
    }

    public void ReloadLocal()
    {
        LocalFiles.Clear();
        try
        {
            if (!Directory.Exists(LocalPath))
                LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var root = Path.GetPathRoot(LocalPath)?.TrimEnd('\\');
            _suppressDrive++;
            try
            {
                if (root is not null && Drives.Contains(root))
                    SelectedDrive = root;
                else if (Drives.Count > 0 && string.IsNullOrEmpty(SelectedDrive))
                    SelectedDrive = Drives[0];
            }
            finally
            {
                _suppressDrive--;
            }

            var parent = Directory.GetParent(LocalPath);
            if (parent is not null)
            {
                LocalFiles.Add(new FileEntry
                {
                    Name = "..",
                    FullPath = parent.FullName,
                    IsDirectory = true,
                    IsParentNav = true
                });
            }

            foreach (var dir in Directory.EnumerateDirectories(LocalPath))
            {
                var info = new DirectoryInfo(dir);
                LocalFiles.Add(new FileEntry
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = true,
                    Modified = info.LastWriteTime
                });
            }

            foreach (var file in Directory.EnumerateFiles(LocalPath))
            {
                var info = new FileInfo(file);
                LocalFiles.Add(new FileEntry
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = false,
                    Size = info.Length,
                    Modified = info.LastWriteTime
                });
            }
        }
        catch (Exception ex)
        {
            Term("local error: " + ex.Message + "\n");
        }
    }

    public void LocalUp()
    {
        try
        {
            var parent = Directory.GetParent(LocalPath);
            if (parent is null) return;
            LocalPath = parent.FullName;
            ReloadLocal();
        }
        catch { /* ignore */ }
    }

    public void RemoteUp()
    {
        if (_sftp is null) return;
        try
        {
            var cur = _sftp.WorkingDirectory.TrimEnd('/');
            if (string.IsNullOrEmpty(cur) || cur == "/") return;
            var idx = cur.LastIndexOf('/');
            var parent = idx <= 0 ? "/" : cur[..idx];
            _sftp.ChangeDirectory(parent);
            RefreshRemote();
        }
        catch (Exception ex)
        {
            Term("cd .. error: " + ex.Message + "\n");
        }
    }

    public void ApplyTypedPath(string path)
    {
        var p = path.Trim();
        if (!Directory.Exists(p)) return;
        LocalPath = p;
        ReloadLocal();
    }

    public void OpenLocal(FileEntry? f)
    {
        if (f is null || (!f.IsDirectory && !f.IsParentNav)) return;
        LocalPath = f.FullPath;
        ReloadLocal();
    }

    public void OpenRemote(FileEntry? f)
    {
        if (_sftp is null || f is null || (!f.IsDirectory && !f.IsParentNav)) return;
        try
        {
            _sftp.ChangeDirectory(f.FullPath);
            RefreshRemote();
        }
        catch (Exception ex)
        {
            Term("cd error: " + ex.Message + "\n");
        }
    }

    public void Upload()
    {
        if (_sftp is null)
        {
            AppDialog.Info(OwnerWin(), "Сначала Connect.", "SSH Files");
            return;
        }

        if (SelectedLocal is not FileEntry local || local.IsDirectory || local.IsParentNav)
        {
            AppDialog.Info(OwnerWin(), "Выберите локальный файл.", "SSH Files");
            return;
        }

        var remote = (_sftp.WorkingDirectory.TrimEnd('/') + "/" + local.Name).Replace("//", "/");
        try
        {
            _sftp.Upload(local.FullPath, remote);
            Term($"upload {local.Name} → {remote}\n");
            RefreshRemote();
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "Upload");
        }
    }

    public void Download()
    {
        if (_sftp is null)
        {
            AppDialog.Info(OwnerWin(), "Сначала Connect.", "SSH Files");
            return;
        }

        if (SelectedRemote is not FileEntry remote || remote.IsDirectory || remote.IsParentNav)
        {
            AppDialog.Info(OwnerWin(), "Выберите удалённый файл.", "SSH Files");
            return;
        }

        var local = Path.Combine(LocalPath, remote.Name);
        try
        {
            _sftp.Download(remote.FullPath, local);
            Term($"download {remote.FullPath} → {local}\n");
            ReloadLocal();
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "Download");
        }
    }

    public void DeleteRemote()
    {
        if (_sftp is null || SelectedRemote is not FileEntry remote || remote.IsParentNav)
            return;
        if (!AppDialog.Confirm(OwnerWin(), $"Удалить {remote.Name}?"))
            return;
        try
        {
            _sftp.Delete(remote.FullPath);
            Term($"delete {remote.FullPath}\n");
            RefreshRemote();
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "Delete");
        }
    }

    public void RunTerminal(string? raw)
    {
        if (SelectedHost is not HostConfig host) return;
        var cmd = raw?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(cmd)) return;
        try
        {
            using var ssh = SshRunner.Connect(host);
            string? sudoPass = null;
            if (Sudo && string.Equals(host.Auth, "Password", StringComparison.OrdinalIgnoreCase))
                sudoPass = host.Password;
            var r = ssh.Run("term", cmd, 60, Sudo, sudoPass);
            var mark = Sudo ? "#" : "$";
            Term($"{host.Username}@{host.Host}:~{mark} {cmd}\n");
            if (!string.IsNullOrWhiteSpace(r.StdOut))
                Term(r.StdOut.TrimEnd() + "\n");
            if (!string.IsNullOrWhiteSpace(r.StdErr))
                Term(r.StdErr.TrimEnd() + "\n");
            if (r.ExitCode != 0)
                Term($"[exit {r.ExitCode}]\n");
            ClearInput?.Invoke();
        }
        catch (Exception ex)
        {
            Term("error: " + ex.Message + "\n");
        }
    }

    public void ViewFile()
    {
        var f = Active();
        if (f is null || f.IsDirectory)
        {
            AppDialog.Info(OwnerWin(), "Выберите файл для View.", "F3");
            return;
        }

        if (ActiveLocal)
        {
            try
            {
                var text = File.ReadAllText(f.FullPath);
                if (text.Length > 8000) text = text[..8000] + "\n…";
                Term($"--- view {f.Name} ---\n{text.TrimEnd()}\n");
            }
            catch (Exception ex) { Term("view error: " + ex.Message + "\n"); }
        }
        else
        {
            if (SelectedHost is null) return;
            RunTerminal($"sed -n '1,200p' -- '{f.FullPath.Replace("'", "'\\''")}'");
        }
    }

    public void EditFile()
    {
        var f = Active();
        if (f is null || f.IsDirectory)
        {
            AppDialog.Info(OwnerWin(), "Выберите файл для Edit.", "F4");
            return;
        }

        if (ActiveLocal)
        {
            try
            {
                Process.Start(new ProcessStartInfo(f.FullPath) { UseShellExecute = true });
            }
            catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "F4"); }
        }
        else
            Term("F4 Edit remote: скачайте файл (F5) и правьте локально.\n");
    }

    public void CopyActive()
    {
        if (ActiveLocal) Upload();
        else Download();
    }

    public void RenameActive()
    {
        var f = Active();
        if (f is null) return;
        var name = TextPrompt.Ask(OwnerWin(), "Rename", "Новое имя:", f.Name);
        if (string.IsNullOrWhiteSpace(name) || name == f.Name) return;
        try
        {
            if (ActiveLocal)
            {
                var dest = Path.Combine(Path.GetDirectoryName(f.FullPath)!, name);
                if (f.IsDirectory) Directory.Move(f.FullPath, dest);
                else File.Move(f.FullPath, dest);
                ReloadLocal();
            }
            else
            {
                if (_sftp is null) return;
                var parent = f.FullPath.TrimEnd('/');
                var idx = parent.LastIndexOf('/');
                var dest = (idx <= 0 ? "/" : parent[..idx]) + "/" + name;
                dest = dest.Replace("//", "/");
                _sftp.Rename(f.FullPath, dest);
                RefreshRemote();
            }

            Term($"rename {f.Name} → {name}\n");
        }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "F6"); }
    }

    public void MkDir()
    {
        var name = TextPrompt.Ask(OwnerWin(), "MkDir", "Имя папки:", "new-folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            if (ActiveLocal)
            {
                Directory.CreateDirectory(Path.Combine(LocalPath, name));
                ReloadLocal();
            }
            else
            {
                if (_sftp is null)
                {
                    AppDialog.Info(OwnerWin(), "Сначала Connect.", "F7");
                    return;
                }

                var path = (_sftp.WorkingDirectory.TrimEnd('/') + "/" + name).Replace("//", "/");
                _sftp.MkDir(path);
                RefreshRemote();
            }

            Term($"mkdir {name}\n");
        }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "F7"); }
    }

    public void DeleteActive()
    {
        if (!ActiveLocal)
        {
            DeleteRemote();
            return;
        }

        if (SelectedLocal is not FileEntry local) return;
        if (!AppDialog.Confirm(OwnerWin(), $"Удалить {local.Name}?"))
            return;
        try
        {
            if (local.IsDirectory) Directory.Delete(local.FullPath, true);
            else File.Delete(local.FullPath);
            Term($"delete local {local.Name}\n");
            ReloadLocal();
        }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "F8"); }
    }

    public void Disconnect()
    {
        CloseSftp();
        RemoteFiles.Clear();
        RemotePath = "";
        Term("disconnected\n");
        UpdatePrompt();
    }

    public void Dispose() => CloseSftp();

    private FileEntry? Active() => ActiveLocal ? SelectedLocal : SelectedRemote;

    private void RefreshRemote()
    {
        if (_sftp is null) return;
        RemoteFiles.Clear();
        foreach (var f in _sftp.List())
            RemoteFiles.Add(f);
        RemotePath = _sftp.WorkingDirectory;
    }

    private void CloseSftp()
    {
        _sftp?.Dispose();
        _sftp = null;
    }

    private void Term(string text, bool replace = false) => TerminalWrite?.Invoke(text, replace);

    private Window OwnerWin() => Owner?.Invoke() ?? Application.Current.MainWindow!;
}
