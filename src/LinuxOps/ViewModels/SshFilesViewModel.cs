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
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly List<string> _back = new();
    private readonly List<string> _fwd = new();
    private SftpBrowser? _sftp;
    private int _suppressDrive;
    private bool _suppressHost;
    private bool _nav;

    public SshFilesViewModel(AppSession session)
    {
        _session = session;
        LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public ObservableCollection<HostConfig> Hosts => _session.Hosts;
    public ObservableCollection<FileEntry> LocalFiles { get; } = new();
    public ObservableCollection<FileEntry> RemoteFiles { get; } = new();
    public ObservableCollection<string> Drives { get; } = new();
    public ObservableCollection<RemoteEditSession> OpenEdits { get; } = new();

    public Func<Window?>? Owner { get; set; }

    public event Action<HostConfig?>? OpenTerminalRequested;
    public event Action<string, string>? ShowText;

    public FileEntry? SelectedLocal { get; set; }
    public FileEntry? SelectedRemote { get; set; }

    [ObservableProperty] private string transferStatus = "";
    [ObservableProperty] private HostConfig? selectedHost;
    [ObservableProperty] private bool sudo;
    [ObservableProperty] private bool activeLocal = true;
    [ObservableProperty] private string localPath;
    [ObservableProperty] private string remotePath = "";
    [ObservableProperty] private string? selectedDrive;
    [ObservableProperty] private bool connected;
    [ObservableProperty] private string lastOperation = "";

    public void Init()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
            Drives.Add(d.Name.TrimEnd('\\'));
        ReloadLocal();
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
        }
    }

    partial void OnSelectedHostChanged(HostConfig? value)
    {
        if (_suppressHost) return;
        FlushEdits();
        CloseSftp(flush: false);
        _back.Clear();
        _fwd.Clear();
        RemotePath = "";
        RemoteFiles.Clear();
        LastOperation = "Подключитесь к хосту (Connect).";
    }

    partial void OnSelectedDriveChanged(string? value)
    {
        if (_suppressDrive > 0 || string.IsNullOrWhiteSpace(value)) return;
        var path = value.EndsWith(':') ? value + "\\" : value + "\\";
        if (!Directory.Exists(path)) return;
        if (LocalPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return;
        LocalPath = path;
        ReloadLocal();
    }

    public async Task Connect()
    {
        if (SelectedHost is not HostConfig host)
        {
            AppDialog.Info(OwnerWin(), "Выберите хост.", "SSH Files");
            return;
        }

        try
        {
            TransferStatus = "подключение…";
            FlushEdits();
            await _io.WaitAsync();
            try
            {
                CloseSftp(flush: false);
                var browser = await Task.Run(() => SftpBrowser.Connect(host));
                _sftp = browser;
                host.Status = "Online";
                await Task.Run(() =>
                {
                    try { browser.ChangeDirectory("/etc"); }
                    catch
                    {
                        try { browser.ChangeDirectory(browser.WorkingDirectory); }
                        catch { /* leave cwd */ }
                    }
                });
                await FillRemoteAsync(record: true);
            }
            finally
            {
                _io.Release();
            }

            var distro = await Task.Run(() =>
            {
                using var ssh = SshRunner.Connect(host);
                return ssh.DetectDistro();
            });
            host.DistroHint = distro.Id;
            _session.Persist();
            Connected = true;
            LastOperation = $"подключено {host.Username}@{host.Host} {RemotePath}";
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "SSH Files");
        }
        finally
        {
            TransferStatus = "";
        }
    }

    public async Task Refresh()
    {
        ReloadLocal();
        if (_sftp is null) return;
        await _io.WaitAsync();
        try { await FillRemoteAsync(record: false); }
        finally { _io.Release(); }
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

    public async Task RemoteUp()
    {
        if (_sftp is null) return;
        try
        {
            await _io.WaitAsync();
            try
            {
                var cur = _sftp.WorkingDirectory.TrimEnd('/');
                if (string.IsNullOrEmpty(cur) || cur == "/") return;
                var idx = cur.LastIndexOf('/');
                var parent = idx <= 0 ? "/" : cur[..idx];
                await Task.Run(() => _sftp.ChangeDirectory(parent));
                await FillRemoteAsync(record: true);
            }
            finally { _io.Release(); }
        }
        catch (Exception ex)
        {
            Term("cd .. error: " + ex.Message + "\n");
        }
    }

    public async Task GoRemote(string path)
    {
        if (_sftp is null || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await _io.WaitAsync();
            try
            {
                await Task.Run(() => _sftp.ChangeDirectory(path.Trim()));
                await FillRemoteAsync(record: true);
            }
            finally { _io.Release(); }
        }
        catch (Exception ex)
        {
            Term("cd error: " + ex.Message + "\n");
        }
    }

    public async Task RemoteBack()
    {
        if (_sftp is null || _back.Count < 2) return;
        var current = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _fwd.Add(current);
        await Jump(_back[^1]);
    }

    public async Task RemoteForward()
    {
        if (_sftp is null || _fwd.Count == 0) return;
        var next = _fwd[^1];
        _fwd.RemoveAt(_fwd.Count - 1);
        _back.Add(next);
        await Jump(next);
    }

    private async Task Jump(string path)
    {
        try
        {
            _nav = true;
            await _io.WaitAsync();
            try
            {
                await Task.Run(() => _sftp!.ChangeDirectory(path));
                await FillRemoteAsync(record: false);
            }
            finally { _io.Release(); }
        }
        catch (Exception ex)
        {
            Term("cd error: " + ex.Message + "\n");
        }
        finally { _nav = false; }
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

    public async Task OpenRemote(FileEntry? f)
    {
        if (_sftp is null || f is null || (!f.IsDirectory && !f.IsParentNav)) return;
        try
        {
            await _io.WaitAsync();
            try
            {
                await Task.Run(() => _sftp.ChangeDirectory(f.FullPath));
                await FillRemoteAsync(record: true);
            }
            finally { _io.Release(); }
        }
        catch (Exception ex)
        {
            Term("cd error: " + ex.Message + "\n");
        }
    }

    public Task Upload() => UploadPaths(SelectedLocal is FileEntry local ? new[] { local.FullPath } : Array.Empty<string>());

    public async Task UploadPaths(IReadOnlyList<string> localPaths)
    {
        if (_sftp is null)
        {
            AppDialog.Info(OwnerWin(), "Сначала Connect.", "SSH Files");
            return;
        }

        var files = localPaths.Where(p => File.Exists(p)).ToList();
        if (files.Count == 0)
        {
            AppDialog.Info(OwnerWin(), "Выберите локальный файл.", "SSH Files");
            return;
        }

        try
        {
            await _io.WaitAsync();
            try
            {
                foreach (var local in files)
                {
                    var name = Path.GetFileName(local);
                    var remote = (_sftp.WorkingDirectory.TrimEnd('/') + "/" + name).Replace("//", "/");
                    var progress = new Progress<long>(n => TransferStatus = $"↑ {name} · {n / 1024} КБ");
                    TransferStatus = $"↑ {name}";
                    await _sftp.UploadAsync(local, remote, progress, CancellationToken.None);
                    Term($"upload {name} → {remote}\n");
                }

                await FillRemoteAsync(record: false);
            }
            finally { _io.Release(); }
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "Upload");
        }
        finally
        {
            TransferStatus = "";
        }
    }

    public Task Download() => DownloadEntries(SelectedRemote is FileEntry remote ? new[] { remote } : Array.Empty<FileEntry>());

    public async Task DownloadEntries(IReadOnlyList<FileEntry> entries)
    {
        if (_sftp is null)
        {
            AppDialog.Info(OwnerWin(), "Сначала Connect.", "SSH Files");
            return;
        }

        var files = entries.Where(f => !f.IsDirectory && !f.IsParentNav).ToList();
        if (files.Count == 0)
        {
            AppDialog.Info(OwnerWin(), "Выберите удалённый файл.", "SSH Files");
            return;
        }

        try
        {
            await _io.WaitAsync();
            try
            {
                foreach (var remote in files)
                {
                    var local = Path.Combine(LocalPath, remote.Name);
                    var progress = new Progress<long>(n => TransferStatus = $"↓ {remote.Name} · {n / 1024} КБ");
                    TransferStatus = $"↓ {remote.Name}";
                    await _sftp.DownloadAsync(remote.FullPath, local, progress, CancellationToken.None);
                    Term($"download {remote.FullPath} → {local}\n");
                }
            }
            finally { _io.Release(); }
            ReloadLocal();
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "Download");
        }
        finally
        {
            TransferStatus = "";
        }
    }

    public async Task DownloadTo(FileEntry remote, string localPath)
    {
        if (_sftp is null) return;
        await _io.WaitAsync();
        try
        {
            var progress = new Progress<long>(n => TransferStatus = $"↓ {remote.Name} · {n / 1024} КБ");
            await _sftp.DownloadAsync(remote.FullPath, localPath, progress, CancellationToken.None);
        }
        finally
        {
            _io.Release();
            TransferStatus = "";
        }
    }

    public async Task DeleteRemote()
    {
        if (_sftp is null || SelectedRemote is not FileEntry remote || remote.IsParentNav)
            return;
        int files;
        await _io.WaitAsync();
        try { files = await Task.Run(() => remote.IsDirectory ? _sftp.CountFiles(remote.FullPath) : 1); }
        finally { _io.Release(); }
        var ask = remote.IsDirectory
            ? $"Удалить {remote.Name}? Внутри файлов: {files}."
            : $"Удалить {remote.Name}?";
        if (!AppDialog.Confirm(OwnerWin(), ask))
            return;
        try
        {
            await _io.WaitAsync();
            try
            {
                TransferStatus = $"удаление {remote.Name}";
                await Task.Run(() => _sftp.Delete(remote.FullPath));
                Term($"delete {remote.FullPath}\n");
                await FillRemoteAsync(record: false);
            }
            finally { _io.Release(); }
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "Delete");
        }
        finally
        {
            TransferStatus = "";
        }
    }

    public void OpenTerminal() => OpenTerminalRequested?.Invoke(SelectedHost);

    public async Task ViewFile()
    {
        var f = Active();
        if (f is null || f.IsDirectory || f.IsParentNav)
        {
            AppDialog.Info(OwnerWin(), "Выберите файл для View.", "F3");
            return;
        }

        const int max = 1024 * 1024;
        try
        {
            byte[] bytes;
            bool cut;
            if (ActiveLocal)
            {
                (bytes, cut) = await Task.Run(() => ReadHeadLocal(f.FullPath, max));
            }
            else
            {
                if (_sftp is null)
                {
                    AppDialog.Info(OwnerWin(), "Сначала Connect.", "F3");
                    return;
                }

                await _io.WaitAsync();
                try
                {
                    var got = await Task.Run(() => _sftp.ReadHead(f.FullPath, max + 1));
                    cut = got.Length > max;
                    bytes = cut ? got[..max] : got;
                }
                finally { _io.Release(); }
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (cut) text += "\n… обрезано до 1 МБ";
            ShowText?.Invoke(f.FullPath, text);
            LastOperation = "просмотр " + f.FullPath;
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "F3");
        }
    }

    private static (byte[] Bytes, bool Cut) ReadHeadLocal(string path, int max)
    {
        using var fs = File.OpenRead(path);
        var n = (int)Math.Min(max + 1L, fs.Length);
        var buf = new byte[n];
        var off = 0;
        while (off < n)
        {
            var read = fs.Read(buf, off, n - off);
            if (read == 0) break;
            off += read;
        }

        var cut = off > max;
        return (cut ? buf[..max] : buf[..off], cut);
    }

    public async Task EditFile()
    {
        var f = Active();
        if (f is null || f.IsDirectory || f.IsParentNav)
        {
            AppDialog.Info(OwnerWin(), "Выберите файл для Edit.", "F4");
            return;
        }

        var editor = DefaultsStore.F4Editor;
        if (ActiveLocal)
        {
            try
            {
                Process.Start(new ProcessStartInfo(editor, "\"" + f.FullPath + "\"") { UseShellExecute = false });
                LastOperation = "правка " + f.FullPath;
            }
            catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "F4"); }
            return;
        }

        if (_sftp is null || SelectedHost is not HostConfig host)
        {
            AppDialog.Info(OwnerWin(), "Сначала Connect.", "F4");
            return;
        }

        if (OpenEdits.Any(s => s.RemotePath == f.FullPath))
        {
            AppDialog.Info(OwnerWin(), "Этот файл уже открыт.", "F4");
            return;
        }

        try
        {
            var local = RemoteEditSession.LocalCopyPath(host.Host, f.FullPath);
            try
            {
                await DownloadTo(f, local);
            }
            catch (Renci.SshNet.Common.SftpPermissionDeniedException) when (Sudo)
            {
                await Task.Run(() => RemoteEditSession.DownloadElevated(_sftp, host, f.FullPath, local));
            }

            var stat = await Task.Run(() => _sftp.Stat(f.FullPath));
            var session = new RemoteEditSession(
                host.Display, f.FullPath, local, stat.Size, stat.Mtime, editor, launchEditor: true,
                s => RemoteEditSession.AskSave(OwnerWin(), s.RemotePath, s.HostLabel),
                s => RemoteEditSession.AskConflict(OwnerWin()),
                SaveSession);
            session.Closed += s => Application.Current?.Dispatcher.BeginInvoke(() => OpenEdits.Remove(s));
            OpenEdits.Add(session);
            LastOperation = "правка " + f.FullPath;
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "F4");
        }
    }

    public void CloseEdit(RemoteEditSession session)
    {
        if (session.Dirty)
            session.Commit(fromClose: true).GetAwaiter().GetResult();
        session.Dispose();
    }

    private RemoteSaveResult SaveSession(RemoteEditSession session)
    {
        if (_sftp is null || SelectedHost is not HostConfig host)
            return new RemoteSaveResult { Detail = "нет подключения" };
        _io.Wait();
        try
        {
            var result = RemoteEditSession.SaveBack(
                _sftp, host, Sudo, session.LocalPath, session.RemotePath,
                session.RemoteSize, session.RemoteMtime,
                () =>
                {
                    var app = Application.Current;
                    if (app is null) return false;
                    return app.Dispatcher.CheckAccess()
                        ? RemoteEditSession.AskConflict(OwnerWin())
                        : app.Dispatcher.Invoke(() => RemoteEditSession.AskConflict(OwnerWin()));
                });
            HistoryStore.Append(new[]
            {
                new HistoryEntry
                {
                    Mode = "edit",
                    Host = host.Display,
                    Action = session.RemotePath,
                    Status = result.Saved ? "OK" : "ERR",
                    Preview = $"{session.RemotePath} · {result.Size} байт · {result.Detail}"
                }
            });
            if (result.Saved)
            {
                var now = _sftp.Stat(session.RemotePath);
                session.NoteRemote(now.Size, now.Mtime, RemoteEditSession.FileHash(session.LocalPath));
                LastOperation = "загружено " + session.RemotePath;
            }
            else if (result.Conflict)
                LastOperation = result.Detail;
            else
                Application.Current?.Dispatcher.BeginInvoke(() => AppDialog.Info(OwnerWin(), result.Detail, "F4"));
            return result;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task CopyActive()
    {
        if (ActiveLocal) await Upload();
        else await Download();
    }

    public async Task RenameActive()
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
                await _io.WaitAsync();
                try
                {
                    await Task.Run(() => _sftp.Rename(f.FullPath, dest));
                    await FillRemoteAsync(record: false);
                }
                finally { _io.Release(); }
            }

            Term($"rename {f.Name} → {name}\n");
        }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "F6"); }
    }

    public async Task MkDir()
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
                await _io.WaitAsync();
                try
                {
                    await Task.Run(() => _sftp.MkDir(path));
                    await FillRemoteAsync(record: false);
                }
                finally { _io.Release(); }
            }

            Term($"mkdir {name}\n");
        }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "F7"); }
    }

    public async Task DeleteActive()
    {
        if (!ActiveLocal)
        {
            await DeleteRemote();
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
        FlushEdits();
        CloseSftp(flush: false);
        RemoteFiles.Clear();
        RemotePath = "";
        _back.Clear();
        _fwd.Clear();
        LastOperation = "отключено";
    }

    public void Dispose()
    {
        FlushEdits();
        CloseSftp(flush: false);
    }

    private void FlushEdits()
    {
        var list = OpenEdits.ToList();
        if (list.Count == 0) return;
        var dirty = list.Where(s => s.Dirty).ToList();
        if (dirty.Count > 0 && _sftp is not null && SelectedHost is not null)
        {
            var text = "Есть несохранённые изменения:\n" + string.Join("\n", dirty.Select(s => s.RemotePath)) + "\nЗагрузить на хост?";
            if (AppDialog.Confirm(OwnerWin(), text, "Правка"))
            {
                foreach (var session in dirty)
                    SaveSession(session);
            }
        }

        foreach (var session in list)
            session.Dispose();
    }

    private FileEntry? Active() => ActiveLocal ? SelectedLocal : SelectedRemote;

    private async Task FillRemoteAsync(bool record)
    {
        if (_sftp is null) return;
        var entries = await Task.Run(() => _sftp.List());
        var cwd = _sftp.WorkingDirectory;
        RemoteFiles.Clear();
        foreach (var f in entries)
            RemoteFiles.Add(f);
        RemotePath = cwd;
        if (!record || _nav) return;
        _fwd.Clear();
        if (_back.Count == 0 || _back[^1] != cwd)
            _back.Add(cwd);
    }

    private void CloseSftp(bool flush)
    {
        if (flush) FlushEdits();
        _sftp?.Dispose();
        _sftp = null;
        Connected = false;
    }

    private void Term(string text)
    {
        var line = text.Replace("\r", "").Trim();
        if (line.Length == 0) return;
        var first = line.Split('\n')[0].Trim();
        LastOperation = first.Length > 200 ? first[..200] : first;
    }

    private Window OwnerWin() => Owner?.Invoke() ?? Application.Current.MainWindow!;
}
