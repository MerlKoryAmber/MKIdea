using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using LinuxOps.Models;
using LinuxOps.Services;
using LinuxOps.Views;
using Renci.SshNet.Common;

namespace LinuxOps.ViewModels;

public partial class SshFilesViewModel : ObservableObject, IDisposable
{
    /// <summary>Каталоги, которые нельзя удалить целиком даже с sudo.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.Ordinal)
    {
        "/", "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib64", "/opt", "/proc", "/root",
        "/run", "/sbin", "/srv", "/sys", "/tmp", "/usr", "/var"
    };

    private readonly AppSession _session;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly List<string> _back = new();
    private readonly List<string> _fwd = new();
    // Общая для всех вкладок: новая вкладка того же хоста открывается там, где была последняя.
    private static readonly Dictionary<string, string> _lastPath = new(StringComparer.OrdinalIgnoreCase);
    private SftpBrowser? _sftp;
    private HostConfig? _host;
    private string _home = "/";
    private int _suppressDrive;
    private bool _nav;
    private CancellationTokenSource? _transfer;
    private List<FileEntry> _localAll = new();
    private List<FileEntry> _remoteAll = new();

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

    /// <summary>F9: терминал на этом хосте, в этой папке.</summary>
    public event Action<HostConfig, string?>? OpenTerminalRequested;
    public event Action<string, string>? ShowText;
    /// <summary>После обновления списка — выделить объект с этим именем.</summary>
    public event Action<bool, string>? SelectRequested;

    public FileEntry? SelectedLocal { get; set; }
    public FileEntry? SelectedRemote { get; set; }
    public HostConfig? ConnectedHost => _sftp is null ? null : _host;

    [ObservableProperty] private string transferStatus = "";
    [ObservableProperty] private double transferPercent;
    [ObservableProperty] private bool transferring;
    [ObservableProperty] private HostConfig? selectedHost;
    [ObservableProperty] private bool sudo;
    [ObservableProperty] private bool activeLocal = true;
    [ObservableProperty] private string localPath;
    [ObservableProperty] private string remotePath = "";
    [ObservableProperty] private string? selectedDrive;
    [ObservableProperty] private bool connected;
    [ObservableProperty] private string lastOperation = "";
    [ObservableProperty] private bool showHidden = true;
    [ObservableProperty] private string remoteTitle = "Удалённо";
    [ObservableProperty] private string localSort = "Name";
    [ObservableProperty] private bool localSortDesc;
    [ObservableProperty] private string remoteSort = "Name";
    [ObservableProperty] private bool remoteSortDesc;

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
            SelectedHost = Hosts.FirstOrDefault(h => h.IsSelected) ?? Hosts[0];
    }

    partial void OnShowHiddenChanged(bool value)
    {
        ApplyLocal();
        ApplyRemote();
    }

    partial void OnSelectedDriveChanged(string? value)
    {
        if (_suppressDrive > 0 || string.IsNullOrWhiteSpace(value)) return;
        var path = value + "\\";
        if (!Directory.Exists(path)) return;
        if (LocalPath.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return;
        LocalPath = path;
        ReloadLocal();
    }

    // ---------- Подключение ----------

    public async Task Connect(HostConfig? host = null)
    {
        host ??= SelectedHost;
        if (host is null)
        {
            AppDialog.Info(OwnerWin(), "Выберите хост в списке слева.", "SSH Files");
            return;
        }

        SelectedHost = host;
        if (_sftp is { IsConnected: true } && ReferenceEquals(_host, host)) return;

        try
        {
            TransferStatus = $"подключение к {host.Title}…";
            if (!FlushEdits()) return;
            await _io.WaitAsync();
            try
            {
                CloseSftp();
                var browser = await Task.Run(() => SftpBrowser.Connect(host));
                _sftp = browser;
                _host = host;
                _home = browser.WorkingDirectory;
                host.Status = "Online";
                var start = _lastPath.TryGetValue(host.Host, out var last) ? last : _home;
                await Task.Run(() =>
                {
                    try { browser.ChangeDirectory(start); }
                    catch { /* остаёмся в домашней папке */ }
                });
                _back.Clear();
                _fwd.Clear();
                await FillRemoteAsync(record: true);
            }
            finally
            {
                _io.Release();
            }

            Connected = true;
            RemoteTitle = $"Удалённо · {host.Title}";
            OnPropertyChanged(nameof(ConnectedHost));
            LastOperation = $"подключено {host.Username}@{host.Host}";
            _ = LoadNamesAsync(host);
        }
        catch (Exception ex)
        {
            host.Status = "Offline";
            AppDialog.Info(OwnerWin(), ex.Message, "SSH Files");
        }
        finally
        {
            TransferStatus = "";
        }
    }

    /// <summary>Имена владельцев и дистрибутив — фоном, список уже показан.</summary>
    private async Task LoadNamesAsync(HostConfig host)
    {
        try
        {
            var (users, groups, distro) = await Task.Run(() =>
            {
                using var ssh = SshRunner.Connect(host);
                var r = ssh.Run("files-names", "getent passwd | cut -d: -f1,3; echo ---; getent group | cut -d: -f1,3", 30);
                var u = new Dictionary<int, string>();
                var g = new Dictionary<int, string>();
                var target = u;
                foreach (var raw in r.StdOut.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line == "---") { target = g; continue; }
                    var parts = line.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var id)) target.TryAdd(id, parts[0]);
                }

                return (u, g, ssh.DetectDistro());
            });
            if (!ReferenceEquals(_host, host) || _sftp is null) return;
            _sftp.SetNames(users, groups);
            host.DistroHint = distro.Id;
            _session.Persist();
            await Refresh();
        }
        catch
        {
            // Имена — только для красоты: остаются UID/GID.
        }
    }

    public void Disconnect()
    {
        if (!FlushEdits()) return;
        CloseSftp();
        RemoteFiles.Clear();
        _remoteAll.Clear();
        RemotePath = "";
        RemoteTitle = "Удалённо";
        _back.Clear();
        _fwd.Clear();
        LastOperation = "отключено";
    }

    /// <summary>Закрытие вкладки: несохранённые правки — загрузить или явно бросить. false — вкладка остаётся.</summary>
    public bool Close()
    {
        if (Transferring)
        {
            if (!AppDialog.Confirm(OwnerWin(), "Идёт передача файлов. Прервать и закрыть вкладку?", "SSH Files")) return false;
            CancelTransfer();
        }

        if (!FlushEdits()) return false;
        CloseSftp();
        return true;
    }

    public void Dispose()
    {
        FlushEdits(force: true);
        CloseSftp();
    }

    /// <summary>Переподключение без диалогов (вызывающий может держать _io). true — список снова живой.</summary>
    private async Task<bool> TryReconnect(HostConfig host)
    {
        var path = RemotePath;
        try
        {
            var browser = await Task.Run(() => SftpBrowser.Connect(host));
            var old = _sftp;
            _sftp = browser;
            try { old?.Dispose(); } catch { /* уже закрыт */ }
            if (_rootDir is null && path.Length > 0)
            {
                try { await Task.Run(() => browser.ChangeDirectory(path)); }
                catch { /* остаёмся в домашней папке */ }
            }

            await FillRemoteAsync(record: false);
            Connected = true;
            _ = LoadNamesAsync(host);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CloseSftp()
    {
        if (_sftp is not null && _host is not null && RemotePath.Length > 0)
            _lastPath[_host.Host] = RemotePath;
        _sftp?.Dispose();
        _sftp = null;
        _rootDir = null;
        Connected = false;
        OnPropertyChanged(nameof(ConnectedHost));
    }

    // ---------- Списки ----------

    public async Task Refresh()
    {
        ReloadLocal();
        if (_sftp is null) return;
        await _io.WaitAsync();
        try { await FillRemoteAsync(record: false); }
        catch (Exception ex) { await LostConnection(ex); }
        finally { _io.Release(); }
    }

    public void ReloadLocal()
    {
        var list = new List<FileEntry>();
        try
        {
            if (!Directory.Exists(LocalPath))
                LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var root = Path.GetPathRoot(LocalPath)?.TrimEnd('\\');
            _suppressDrive++;
            try
            {
                if (root is not null && Drives.Contains(root)) SelectedDrive = root;
            }
            finally { _suppressDrive--; }

            var parent = Directory.GetParent(LocalPath);
            if (parent is not null)
                list.Add(new FileEntry { Name = "..", FullPath = parent.FullName, IsDirectory = true, IsParentNav = true });

            var dir = new DirectoryInfo(LocalPath);
            foreach (var info in dir.EnumerateFileSystemInfos())
            {
                var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                list.Add(new FileEntry
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = isDir,
                    IsHidden = (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0,
                    IsSymlink = (info.Attributes & FileAttributes.ReparsePoint) != 0,
                    Size = info is FileInfo fi ? fi.Length : 0,
                    Modified = info.LastWriteTime
                });
            }
        }
        catch (Exception ex)
        {
            LastOperation = "ошибка: " + ex.Message;
        }

        _localAll = list;
        ApplyLocal();
    }

    public void SortBy(bool local, string key)
    {
        if (local)
        {
            if (LocalSort == key) LocalSortDesc = !LocalSortDesc;
            else { LocalSort = key; LocalSortDesc = false; }
            ApplyLocal();
        }
        else
        {
            if (RemoteSort == key) RemoteSortDesc = !RemoteSortDesc;
            else { RemoteSort = key; RemoteSortDesc = false; }
            ApplyRemote();
        }
    }

    private void ApplyLocal() => Fill(LocalFiles, _localAll, LocalSort, LocalSortDesc);

    private void ApplyRemote() => Fill(RemoteFiles, _remoteAll, RemoteSort, RemoteSortDesc);

    private void Fill(ObservableCollection<FileEntry> target, List<FileEntry> all, string key, bool desc)
    {
        var visible = all.Where(f => f.IsParentNav || ShowHidden || !f.IsHidden);
        var parent = visible.Where(f => f.IsParentNav);
        var rest = visible.Where(f => !f.IsParentNav);
        IOrderedEnumerable<FileEntry> ordered = rest.OrderByDescending(f => f.IsDirectory);
        ordered = key switch
        {
            "Size" => desc ? ordered.ThenByDescending(f => f.Size) : ordered.ThenBy(f => f.Size),
            "Modified" => desc ? ordered.ThenByDescending(f => f.Modified) : ordered.ThenBy(f => f.Modified),
            "Rights" => desc ? ordered.ThenByDescending(f => f.RightsText) : ordered.ThenBy(f => f.RightsText),
            "Owner" => desc ? ordered.ThenByDescending(f => f.OwnerText) : ordered.ThenBy(f => f.OwnerText),
            _ => desc ? ordered.ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase) : ordered.ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        };
        ordered = ordered.ThenBy(f => f.Name, StringComparer.Ordinal);
        target.Clear();
        foreach (var f in parent.Concat(ordered)) target.Add(f);
    }

    private async Task FillRemoteAsync(bool record)
    {
        if (_sftp is null) return;
        var sftp = _sftp;
        IReadOnlyList<FileEntry> entries;
        string cwd;
        if (_rootDir is not null)
            (cwd, entries) = await RootList(_rootDir);
        else
        {
            try
            {
                entries = await Task.Run(() => sftp.List(showHidden: true));
                cwd = sftp.WorkingDirectory;
            }
            catch (SftpPermissionDeniedException) when (Sudo && _host is not null)
            {
                (cwd, entries) = await RootList(sftp.WorkingDirectory);
            }
        }

        _remoteAll = entries.ToList();
        ApplyRemote();
        RemotePath = cwd;
        if (_host is not null) _lastPath[_host.Host] = cwd;
        if (!record || _nav) return;
        _fwd.Clear();
        if (_back.Count == 0 || _back[^1] != cwd)
            _back.Add(cwd);
    }

    partial void OnSudoChanged(bool value)
    {
        // Выключили sudo в папке, открытой от root, — назад к последней своей папке.
        if (value || _rootDir is null) return;
        _rootDir = null;
        _ = Refresh();
    }

    /// <summary>Папка, которую смотрим от root (sudo), или null — обычный SFTP.</summary>
    private string? _rootDir;

    private async Task<(string Dir, IReadOnlyList<FileEntry> Entries)> RootList(string dir)
    {
        var host = _host!;
        var r = await Task.Run(() =>
        {
            using var ssh = SshRunner.Connect(host);
            return ssh.RunAsRoot("files-list", SftpBrowser.RootListScript, 60, host,
                new Dictionary<string, string> { ["LINUXOPS_DIR"] = dir });
        });
        if (r.ExitCode != 0)
            throw new InvalidOperationException(Short((r.StdErr + "\n" + r.StdOut).Trim()) is { Length: > 0 } t ? t : $"exit {r.ExitCode}");
        var (canonical, entries) = SftpBrowser.ParseRootList(r.StdOut);
        _rootDir = canonical;
        return (canonical, entries);
    }

    private async Task LostConnection(Exception ex)
    {
        if (_sftp is { IsConnected: true })
        {
            LastOperation = "ошибка: " + Short(ex.Message);
            return;
        }

        // Сессию закрыл сервер или сеть — пробуем тихо подключиться заново и вернуться в ту же папку.
        if (_host is { } again && await TryReconnect(again))
        {
            LastOperation = "связь восстановлена (" + Short(ex.Message) + ") — повторите действие";
            return;
        }

        CloseSftp();
        LastOperation = "связь потеряна: " + Short(ex.Message);
        AppDialog.Info(OwnerWin(), "Связь с хостом потеряна. Дважды щёлкните хост слева, чтобы подключиться снова.\n\n" + ex.Message, "SSH Files");
        await Task.CompletedTask;
    }

    // ---------- Навигация ----------

    public void LocalUp()
    {
        var parent = Directory.GetParent(LocalPath);
        if (parent is null) return;
        var from = Path.GetFileName(LocalPath.TrimEnd('\\'));
        LocalPath = parent.FullName;
        ReloadLocal();
        SelectRequested?.Invoke(true, from);
    }

    public void LocalHome()
    {
        LocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ReloadLocal();
    }

    public async Task RemoteUp()
    {
        var cur = RemotePath.TrimEnd('/');
        if (_sftp is null || string.IsNullOrEmpty(cur) || cur == "/") return;
        await GoRemote(SftpBrowser.Parent(cur));
        SelectRequested?.Invoke(false, SftpBrowser.Name(cur));
    }

    public Task RemoteHome() => GoRemote(_home);

    public Task RemoteRoot() => GoRemote("/");

    public async Task GoRemote(string path)
    {
        if (_sftp is null || string.IsNullOrWhiteSpace(path)) return;
        var target = path.Trim();
        if (target == "~" ) target = _home;
        else if (target.StartsWith("~/", StringComparison.Ordinal)) target = SftpBrowser.Join(_home, target[2..]);
        var prevRoot = _rootDir;
        try
        {
            await _io.WaitAsync();
            try
            {
                var sftp = _sftp;
                try
                {
                    await Task.Run(() => sftp.ChangeDirectory(target));
                    _rootDir = null;
                }
                catch (SftpPermissionDeniedException) when (Sudo && _host is not null)
                {
                    // Своему пользователю папка закрыта — в режиме sudo смотрим её от root.
                    _rootDir = target.StartsWith('/') ? target : SftpBrowser.Join(RemotePath, target);
                }

                await FillRemoteAsync(record: true);
            }
            finally { _io.Release(); }
        }
        catch (Exception ex)
        {
            if (ex is SftpPathNotFoundException or SftpPermissionDeniedException)
                AppDialog.Info(OwnerWin(), $"Не удалось открыть {target}:\n{ex.Message}"
                    + (ex is SftpPermissionDeniedException && !Sudo ? "\n\nДля чужих папок выберите «Права: sudo»." : ""), "SSH Files");
            else if (ex is InvalidOperationException && _rootDir is not null && _rootDir != prevRoot)
            {
                _rootDir = prevRoot;
                AppDialog.Info(OwnerWin(), $"Не удалось открыть {target} от root:\n{ex.Message}", "SSH Files");
            }
            else await LostConnection(ex);
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
            await GoRemote(path);
        }
        finally { _nav = false; }
    }

    public void ApplyTypedPath(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Directory.Exists(p))
        {
            LastOperation = "нет такой папки: " + p;
            return;
        }

        LocalPath = Path.GetFullPath(p);
        ReloadLocal();
    }

    /// <summary>Enter / двойной щелчок слева: папка — открыть, файл — открыть программой Windows.</summary>
    public void OpenLocal(FileEntry? f)
    {
        if (f is null) return;
        if (f.IsDirectory || f.IsParentNav)
        {
            if (f.IsParentNav) { LocalUp(); return; }
            LocalPath = f.FullPath;
            ReloadLocal();
            return;
        }

        try { Process.Start(new ProcessStartInfo(f.FullPath) { UseShellExecute = true }); }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "Открыть"); }
    }

    /// <summary>Enter / двойной щелчок справа: папка — открыть, файл — правка.</summary>
    public async Task OpenRemote(FileEntry? f)
    {
        if (_sftp is null || f is null) return;
        if (f.IsParentNav) { await RemoteUp(); return; }
        if (f.IsDirectory) { await GoRemote(f.FullPath); return; }
        SelectedRemote = f;
        ActiveLocal = false;
        await EditFile();
    }

    // ---------- Копирование ----------

    /// <summary>F5: из активной панели в другую. Папки копируются целиком.</summary>
    public async Task CopyActive(IReadOnlyList<FileEntry> local, IReadOnlyList<FileEntry> remote)
    {
        if (ActiveLocal) await UploadPaths(local.Where(f => !f.IsParentNav).Select(f => f.FullPath).ToList());
        else await DownloadEntries(remote);
    }

    public async Task UploadPaths(IReadOnlyList<string> localPaths, string? remoteDir = null)
    {
        if (_sftp is null || _host is null)
        {
            AppDialog.Info(OwnerWin(), "Сначала подключитесь к хосту: двойной щелчок в списке слева.", "Копирование");
            return;
        }

        var roots = localPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (roots.Count == 0)
        {
            AppDialog.Info(OwnerWin(), "Выберите файлы или папки слева.", "Копирование");
            return;
        }

        var targetDir = remoteDir ?? RemotePath;
        var plan = new List<(string Local, string Rel, bool Dir, long Size, DateTime Mtime)>();
        foreach (var root in roots)
        {
            var name = Path.GetFileName(root.TrimEnd('\\'));
            if (Directory.Exists(root))
            {
                plan.Add((root, name, true, 0, default));
                foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                    plan.Add((d, name + "/" + Path.GetRelativePath(root, d).Replace('\\', '/'), true, 0, default));
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    plan.Add((f, name + "/" + Path.GetRelativePath(root, f).Replace('\\', '/'), false, fi.Length, fi.LastWriteTime));
                }
            }
            else
            {
                var fi = new FileInfo(root);
                plan.Add((root, name, false, fi.Length, fi.LastWriteTime));
            }
        }

        var files = plan.Count(p => !p.Dir);
        var total = Math.Max(1, plan.Where(p => !p.Dir).Sum(p => p.Size));
        if (!ConfirmTransfer("Загрузить на сервер", plan.Count(p => !p.Dir), total, targetDir, roots.Count, plan.Count(p => p.Dir)))
            return;

        using var cts = StartTransfer();
        var done = 0L;
        var index = 0;
        var copied = 0;
        var skipped = 0;
        OverwriteChoice? all = null;
        var sftp = _sftp;
        var host = _host;
        await _io.WaitAsync();
        try
        {
            foreach (var item in plan)
            {
                cts.Token.ThrowIfCancellationRequested();
                var remote = SftpBrowser.Join(targetDir, item.Rel);
                if (item.Dir)
                {
                    await TryOrRoot(
                        () => Task.Run(() => sftp.EnsureDirectory(remote)),
                        "mkdir -p -- \"$LINUXOPS_P\"", new() { ["LINUXOPS_P"] = remote });
                    continue;
                }

                index++;
                var exists = await Task.Run(() => sftp.Exists(remote));
                if (exists)
                {
                    var choice = all ?? AskOverwrite(remote, await Task.Run(() => Describe(sftp, remote)), Describe(item.Size, item.Mtime), files > 1);
                    if (choice == OverwriteChoice.Cancel) break;
                    if (choice is OverwriteChoice.OverwriteAll or OverwriteChoice.SkipAll) all = choice;
                    if (choice is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
                    {
                        skipped++;
                        done += item.Size;
                        continue;
                    }
                }

                var name = Path.GetFileName(item.Local);
                var baseDone = done;
                var progress = new Progress<long>(n => Report("↑", index, files, name, baseDone + n, total));
                Report("↑", index, files, name, baseDone, total);
                await TryOrRoot(
                    () => sftp.UploadAsync(item.Local, remote, progress, cts.Token),
                    null, null,
                    rootFallback: () => Task.Run(() => UploadElevated(sftp, host, item.Local, remote, exists)));
                done += item.Size;
                copied++;
            }

            LastOperation = $"загружено {copied} файл(ов) в {targetDir}" + (skipped > 0 ? $", пропущено {skipped}" : "");
        }
        catch (OperationCanceledException)
        {
            LastOperation = $"копирование отменено, загружено {copied}";
        }
        catch (Exception ex)
        {
            LastOperation = "ошибка загрузки: " + Short(ex.Message);
            AppDialog.Info(OwnerWin(), ex.Message, "Загрузка");
        }
        finally
        {
            try { await FillRemoteAsync(record: false); } catch { /* список обновится вручную */ }
            _io.Release();
            EndTransfer();
        }
    }

    public async Task DownloadEntries(IReadOnlyList<FileEntry> entries, string? localDir = null)
    {
        if (_sftp is null || _host is null)
        {
            AppDialog.Info(OwnerWin(), "Сначала подключитесь к хосту: двойной щелчок в списке слева.", "Копирование");
            return;
        }

        var roots = entries.Where(f => !f.IsParentNav).ToList();
        if (roots.Count == 0)
        {
            AppDialog.Info(OwnerWin(), "Выберите файлы или папки справа.", "Копирование");
            return;
        }

        var targetDir = localDir ?? LocalPath;
        var sftp = _sftp;
        var host = _host;
        await _io.WaitAsync();
        List<TreeItem> plan;
        try
        {
            TransferStatus = "подсчёт файлов…";
            plan = await Task.Run(() => roots.SelectMany(r => r.IsDirectory
                ? sftp.Tree(r.FullPath)
                : new List<TreeItem> { new(r.FullPath, r.Name, false, r.Size, r.Modified) }).ToList());
        }
        catch (Exception ex)
        {
            _io.Release();
            TransferStatus = "";
            AppDialog.Info(OwnerWin(), ex.Message, "Скачивание");
            return;
        }

        var files = plan.Count(p => !p.IsDirectory);
        var total = Math.Max(1, plan.Where(p => !p.IsDirectory).Sum(p => p.Size));
        if (!ConfirmTransfer("Скачать на компьютер", files, total, targetDir, roots.Count, plan.Count(p => p.IsDirectory)))
        {
            _io.Release();
            TransferStatus = "";
            return;
        }

        using var cts = StartTransfer();
        var done = 0L;
        var index = 0;
        var copied = 0;
        var skipped = 0;
        OverwriteChoice? all = null;
        string? partial = null;
        try
        {
            foreach (var item in plan)
            {
                cts.Token.ThrowIfCancellationRequested();
                var local = Path.Combine(targetDir, string.Join('\\', item.Relative.Split('/').Select(SafeWindowsName)));
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(local);
                    continue;
                }

                index++;
                if (File.Exists(local))
                {
                    var fi = new FileInfo(local);
                    var choice = all ?? AskOverwrite(local, Describe(fi.Length, fi.LastWriteTime), Describe(item.Size, item.Modified), files > 1);
                    if (choice == OverwriteChoice.Cancel) break;
                    if (choice is OverwriteChoice.OverwriteAll or OverwriteChoice.SkipAll) all = choice;
                    if (choice is OverwriteChoice.Skip or OverwriteChoice.SkipAll)
                    {
                        skipped++;
                        done += item.Size;
                        continue;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                var name = SftpBrowser.Name(item.FullPath);
                var baseDone = done;
                var progress = new Progress<long>(n => Report("↓", index, files, name, baseDone + n, total));
                Report("↓", index, files, name, baseDone, total);
                partial = local;
                await TryOrRoot(
                    () => sftp.DownloadAsync(item.FullPath, local, progress, cts.Token),
                    null, null,
                    rootFallback: () => Task.Run(() => RemoteEditSession.DownloadElevated(sftp, host, item.FullPath, local)));
                partial = null;
                done += item.Size;
                copied++;
            }

            LastOperation = $"скачано {copied} файл(ов) в {targetDir}" + (skipped > 0 ? $", пропущено {skipped}" : "");
        }
        catch (OperationCanceledException)
        {
            LastOperation = $"копирование отменено, скачано {copied}";
        }
        catch (Exception ex)
        {
            LastOperation = "ошибка скачивания: " + Short(ex.Message);
            AppDialog.Info(OwnerWin(), ex.Message, "Скачивание");
        }
        finally
        {
            if (partial is not null)
            {
                try { File.Delete(partial); } catch { /* файл занят */ }
            }

            _io.Release();
            EndTransfer();
            ReloadLocal();
        }
    }

    private bool ConfirmTransfer(string title, int files, long bytes, string target, int roots, int dirs)
    {
        // Один файл — без лишнего вопроса, как в WinSCP с выключенным подтверждением.
        if (files <= 1 && dirs == 0) return true;
        var text = $"{title}: {files} файл(ов), {FileEntry.FormatSize(bytes)}" +
                   (dirs > 0 ? $", папок: {dirs}" : "") + $"\nКуда: {target}";
        return AppDialog.Confirm(OwnerWin(), text, title);
    }

    private OverwriteChoice AskOverwrite(string target, string existing, string incoming, bool many)
        => FileDialogs.AskOverwrite(OwnerWin(), target, existing, incoming, many);

    private static string Describe(long size, DateTime mtime) => $"{FileEntry.FormatSize(size)}, {mtime:dd.MM.yyyy HH:mm}";

    private static string Describe(SftpBrowser sftp, string remote)
    {
        var s = sftp.StatFull(remote);
        return Describe(s.Size, s.Mtime);
    }

    private CancellationTokenSource StartTransfer()
    {
        _transfer = new CancellationTokenSource();
        Transferring = true;
        TransferPercent = 0;
        return _transfer;
    }

    private void EndTransfer()
    {
        _transfer = null;
        Transferring = false;
        TransferPercent = 0;
        TransferStatus = "";
    }

    public void CancelTransfer() => _transfer?.Cancel();

    private void Report(string arrow, int index, int count, string name, long done, long total)
    {
        TransferPercent = Math.Clamp(done * 100.0 / total, 0, 100);
        TransferStatus = $"{arrow} {index} из {count} · {name} · {TransferPercent:0}%";
    }

    private static void UploadElevated(SftpBrowser sftp, HostConfig host, string local, string remote, bool exists)
    {
        var staged = sftp.Stage(local);
        // Существующий файл — cat поверх (права и владелец прежние), новый — cp и 644.
        var script = exists
            ? "set -euo pipefail\ntrap 'rm -f -- \"$LINUXOPS_SRC\"' EXIT\ncat -- \"$LINUXOPS_SRC\" > \"$LINUXOPS_DST\""
            : "set -euo pipefail\ntrap 'rm -f -- \"$LINUXOPS_SRC\"' EXIT\ncp -- \"$LINUXOPS_SRC\" \"$LINUXOPS_DST\"\nchmod 644 -- \"$LINUXOPS_DST\"";
        using var ssh = SshRunner.Connect(host);
        var r = ssh.RunAsRoot("files-upload", script, 120, host, new Dictionary<string, string>
        {
            ["LINUXOPS_SRC"] = staged,
            ["LINUXOPS_DST"] = remote
        });
        if (r.ExitCode != 0) throw new InvalidOperationException(Short((r.StdErr + "\n" + r.StdOut).Trim()));
    }

    // ---------- Операции ----------

    /// <summary>
    /// Сначала по SFTP от своего пользователя. Нет прав и выбран режим sudo — то же через root.
    /// </summary>
    private async Task TryOrRoot(Func<Task> sftpAction, string? rootScript, Dictionary<string, string>? env, Func<Task>? rootFallback = null)
    {
        try
        {
            await sftpAction();
        }
        catch (SftpPermissionDeniedException) when (Sudo && _host is not null && (rootScript is not null || rootFallback is not null))
        {
            if (rootFallback is not null) await rootFallback();
            else await RunRoot("files-op", rootScript!, env ?? new());
        }
    }

    private async Task RunRoot(string id, string script, Dictionary<string, string> env, bool root = true)
    {
        var host = _host!;
        var r = await Task.Run(() =>
        {
            using var ssh = SshRunner.Connect(host);
            return root
                ? ssh.RunAsRoot(id, "set -euo pipefail\n" + script, 120, host, env)
                : ssh.Run(id, "set -euo pipefail\n" + script, 120, false, host, env);
        });
        if (r.ExitCode != 0)
            throw new InvalidOperationException(Short((r.StdErr + "\n" + r.StdOut).Trim()) is { Length: > 0 } t ? t : $"exit {r.ExitCode}");
    }

    public async Task RenameActive(IReadOnlyList<FileEntry> selected)
    {
        var f = selected.FirstOrDefault(x => !x.IsParentNav) ?? Active();
        if (f is null || f.IsParentNav) return;
        var hint = ActiveLocal ? "Новое имя:" : "Новое имя или путь (например ../old/" + f.Name + "):";
        var name = TextPrompt.Ask(OwnerWin(), "Переименовать / перенести", hint, f.Name);
        if (string.IsNullOrWhiteSpace(name) || name == f.Name) return;
        try
        {
            if (ActiveLocal)
            {
                var dest = Path.IsPathRooted(name) ? name : Path.Combine(Path.GetDirectoryName(f.FullPath)!, name);
                if (f.IsDirectory) Directory.Move(f.FullPath, dest);
                else File.Move(f.FullPath, dest);
                ReloadLocal();
                SelectRequested?.Invoke(true, Path.GetFileName(dest));
            }
            else
            {
                if (_sftp is null) return;
                var dir = SftpBrowser.Parent(f.FullPath);
                var dest = name.StartsWith('/') ? name : SftpBrowser.Join(dir, name);
                dest = Normalize(dest);
                var sftp = _sftp;
                await _io.WaitAsync();
                try
                {
                    await TryOrRoot(() => Task.Run(() => sftp.Rename(f.FullPath, dest)),
                        "mv -- \"$LINUXOPS_A\" \"$LINUXOPS_B\"", new() { ["LINUXOPS_A"] = f.FullPath, ["LINUXOPS_B"] = dest });
                    await FillRemoteAsync(record: false);
                }
                finally { _io.Release(); }
                SelectRequested?.Invoke(false, SftpBrowser.Name(dest));
            }

            LastOperation = $"переименовано {f.Name} → {name}";
        }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "Переименовать"); }
    }

    public async Task MkDir()
    {
        var name = TextPrompt.Ask(OwnerWin(), "Новая папка", "Имя папки:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            if (ActiveLocal)
            {
                Directory.CreateDirectory(Path.Combine(LocalPath, name));
                ReloadLocal();
                SelectRequested?.Invoke(true, name);
            }
            else
            {
                if (_sftp is null) { NeedConnect(); return; }
                var path = SftpBrowser.Join(RemotePath, name);
                var sftp = _sftp;
                await _io.WaitAsync();
                try
                {
                    await TryOrRoot(() => Task.Run(() => sftp.MkDir(path)),
                        "mkdir -- \"$LINUXOPS_P\"", new() { ["LINUXOPS_P"] = path });
                    await FillRemoteAsync(record: false);
                }
                finally { _io.Release(); }
                SelectRequested?.Invoke(false, name);
            }

            LastOperation = "создана папка " + name;
        }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "Новая папка"); }
    }

    /// <summary>Shift+F4: новый пустой файл и сразу правка.</summary>
    public async Task NewFile()
    {
        var name = TextPrompt.Ask(OwnerWin(), "Новый файл", "Имя файла:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            if (ActiveLocal)
            {
                var path = Path.Combine(LocalPath, name);
                if (!File.Exists(path)) File.WriteAllBytes(path, Array.Empty<byte>());
                ReloadLocal();
                SelectRequested?.Invoke(true, name);
                SelectedLocal = LocalFiles.FirstOrDefault(f => f.Name == name);
                await EditFile();
                return;
            }

            if (_sftp is null) { NeedConnect(); return; }
            var remote = SftpBrowser.Join(RemotePath, name);
            var sftp = _sftp;
            await _io.WaitAsync();
            try
            {
                await TryOrRoot(() => Task.Run(() => sftp.CreateEmpty(remote)),
                    "touch -- \"$LINUXOPS_P\"", new() { ["LINUXOPS_P"] = remote });
                await FillRemoteAsync(record: false);
            }
            finally { _io.Release(); }

            SelectRequested?.Invoke(false, name);
            SelectedRemote = RemoteFiles.FirstOrDefault(f => f.Name == name);
            await EditFile();
        }
        catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "Новый файл"); }
    }

    public async Task DeleteActive(IReadOnlyList<FileEntry> selected)
    {
        var items = selected.Where(f => !f.IsParentNav).ToList();
        if (items.Count == 0 && Active() is { IsParentNav: false } one) items.Add(one);
        if (items.Count == 0) return;

        var names = string.Join("\n", items.Take(8).Select(f => (f.IsDirectory ? "📁 " : "") + f.Name));
        if (items.Count > 8) names += $"\n… и ещё {items.Count - 8}";

        if (ActiveLocal)
        {
            if (!AppDialog.Confirm(OwnerWin(), $"Удалить в корзину ({items.Count}):\n{names}", "Удалить")) return;
            try
            {
                foreach (var f in items)
                {
                    if (f.IsDirectory)
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(f.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }

                LastOperation = $"в корзину: {items.Count}";
            }
            catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "Удалить"); }

            ReloadLocal();
            return;
        }

        if (_sftp is null) { NeedConnect(); return; }
        var blocked = items.FirstOrDefault(f => Protected.Contains(f.FullPath.TrimEnd('/') is { Length: > 0 } p ? p : "/"));
        if (blocked is not null)
        {
            AppDialog.Info(OwnerWin(), $"{blocked.FullPath} — системный каталог. LinuxOps не удаляет его целиком.", "Удалить");
            return;
        }

        var where = _host is null ? "" : $" на {_host.Title}";
        var warn = Sudo ? "\n\nРежим sudo: то, на что не хватит прав, будет удалено от root." : "";
        if (!AppDialog.Confirm(OwnerWin(), $"Удалить{where} безвозвратно ({items.Count}):\n{names}{warn}", "Удалить")) return;

        var sftp = _sftp;
        var deleted = 0;
        await _io.WaitAsync();
        try
        {
            TransferStatus = "удаление…";
            foreach (var f in items)
            {
                await TryOrRoot(() => Task.Run(() => sftp.DeleteEntry(f)),
                    "rm -rf -- \"$LINUXOPS_P\"", new() { ["LINUXOPS_P"] = f.FullPath });
                deleted++;
            }

            LastOperation = $"удалено {deleted}";
        }
        catch (Exception ex)
        {
            LastOperation = $"удалено {deleted}, ошибка: {Short(ex.Message)}";
            AppDialog.Info(OwnerWin(), ex.Message + (Sudo ? "" : "\n\nДля чужих файлов выберите «Права: sudo»."), "Удалить");
        }
        finally
        {
            try { await FillRemoteAsync(record: false); } catch { /* обновится вручную */ }
            _io.Release();
            TransferStatus = "";
        }
    }

    /// <summary>Alt+Enter: права и владелец (chmod/chown, по желанию рекурсивно).</summary>
    public async Task Properties(IReadOnlyList<FileEntry> selected)
    {
        if (ActiveLocal)
        {
            var lf = selected.FirstOrDefault(x => !x.IsParentNav) ?? SelectedLocal;
            if (lf is null) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + lf.FullPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "Свойства"); }
            return;
        }

        if (_sftp is null) { NeedConnect(); return; }
        var items = selected.Where(f => !f.IsParentNav).ToList();
        if (items.Count == 0 && SelectedRemote is { IsParentNav: false } s) items.Add(s);
        if (items.Count == 0) return;
        var first = items[0];
        var title = items.Count == 1 ? first.FullPath : $"{items.Count} объектов в {RemotePath}";
        var sameMode = items.All(i => i.Mode == first.Mode) ? first.Mode : -1;
        var owner = items.All(i => i.OwnerText == first.OwnerText) ? first.OwnerText : "";
        var result = FileDialogs.AskProperties(OwnerWin(), title, sameMode, owner, items.Any(i => i.IsDirectory), Sudo);
        if (result is null || (result.Mode is null && result.Owner is null)) return;

        var script = new System.Text.StringBuilder();
        script.Append("while IFS= read -r p; do\n  [ -n \"$p\" ] || continue\n");
        var r = result.Recursive ? "-R " : "";
        if (result.Mode is int m) script.Append($"  chmod {r}{Rights.ToOctal(m)} -- \"$p\"\n");
        if (result.Owner is string o)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(o, @"^[A-Za-z0-9._-]+(:[A-Za-z0-9._-]*)?$"))
            {
                AppDialog.Info(OwnerWin(), "Владелец — в виде user или user:group.", "Свойства");
                return;
            }

            script.Append($"  chown {r}-- '{o}' \"$p\"\n");
        }

        script.Append("done <<< \"$LINUXOPS_PATHS\"\n");
        try
        {
            TransferStatus = "права…";
            await RunRoot("files-chmod", script.ToString(),
                new() { ["LINUXOPS_PATHS"] = string.Join("\n", items.Select(i => i.FullPath)) },
                root: Sudo);
            LastOperation = "права изменены: " + (items.Count == 1 ? first.Name : items.Count + " объектов");
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message + (Sudo ? "" : "\n\nДля чужих файлов выберите «Права: sudo»."), "Свойства");
        }
        finally
        {
            TransferStatus = "";
            await Refresh();
        }
    }

    public void CopyPath(IReadOnlyList<FileEntry> selected)
    {
        var paths = selected.Where(f => !f.IsParentNav).Select(f => f.FullPath).ToList();
        if (paths.Count == 0) paths.Add(ActiveLocal ? LocalPath : RemotePath);
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
            LastOperation = "путь скопирован";
        }
        catch { /* буфер занят */ }
    }

    public void OpenTerminal()
    {
        var host = ConnectedHost ?? SelectedHost;
        if (host is null)
        {
            AppDialog.Info(OwnerWin(), "Выберите хост в списке слева.", "Терминал");
            return;
        }

        OpenTerminalRequested?.Invoke(host, ReferenceEquals(host, ConnectedHost) ? RemotePath : null);
    }

    // ---------- Просмотр и правка ----------

    public async Task ViewFile()
    {
        var f = Active();
        if (f is null || f.IsDirectory || f.IsParentNav)
        {
            AppDialog.Info(OwnerWin(), "Выберите файл.", "Просмотр");
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
                if (_sftp is null) { NeedConnect(); return; }
                var sftp = _sftp;
                await _io.WaitAsync();
                try
                {
                    byte[] got;
                    try { got = await Task.Run(() => sftp.ReadHead(f.FullPath, max + 1)); }
                    catch (SftpPermissionDeniedException) when (Sudo && _host is not null)
                    {
                        var tmp = Path.GetTempFileName();
                        var host = _host;
                        try
                        {
                            await Task.Run(() => RemoteEditSession.DownloadElevated(sftp, host, f.FullPath, tmp));
                            got = (await Task.Run(() => ReadHeadLocal(tmp, max))).Bytes;
                        }
                        finally { File.Delete(tmp); }
                    }

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
            AppDialog.Info(OwnerWin(), ex.Message + (ex is SftpPermissionDeniedException && !Sudo ? "\n\nВыберите «Права: sudo»." : ""), "Просмотр");
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
            AppDialog.Info(OwnerWin(), "Выберите файл.", "Правка");
            return;
        }

        var editor = DefaultsStore.F4Editor;
        if (ActiveLocal)
        {
            try
            {
                Process.Start(new ProcessStartInfo(editor, "\"" + f.FullPath + "\"") { UseShellExecute = true });
                LastOperation = "правка " + f.FullPath;
            }
            catch (Exception ex) { AppDialog.Info(OwnerWin(), ex.Message, "Правка"); }
            return;
        }

        if (_sftp is null || _host is not HostConfig host) { NeedConnect(); return; }
        if (OpenEdits.FirstOrDefault(s => s.RemotePath == f.FullPath && ReferenceEquals(s.Host, host)) is { } open)
        {
            AppDialog.Info(OwnerWin(), "Этот файл уже открыт. Он в списке правок внизу.", "Правка");
            return;
        }

        if (f.Size > 20L * 1024 * 1024 &&
            !AppDialog.Confirm(OwnerWin(), $"{f.Name} — {FileEntry.FormatSize(f.Size)}. Открыть в редакторе всё равно?", "Правка"))
            return;

        var sftp = _sftp;
        try
        {
            var local = RemoteEditSession.LocalCopyPath(host.Host, f.FullPath);
            await _io.WaitAsync();
            (long Size, DateTime Mtime) stat;
            try
            {
                try
                {
                    await sftp.DownloadAsync(f.FullPath, local, null, CancellationToken.None);
                }
                catch (SftpPermissionDeniedException)
                {
                    if (!Sudo)
                    {
                        AppDialog.Info(OwnerWin(), $"Нет прав на чтение {f.FullPath}.\nВыберите «Права: sudo» и откройте снова.", "Правка");
                        return;
                    }

                    await Task.Run(() => RemoteEditSession.DownloadElevated(sftp, host, f.FullPath, local));
                }

                stat = await Task.Run(() => sftp.Stat(f.FullPath));
            }
            finally { _io.Release(); }

            var session = new RemoteEditSession(
                host, f.FullPath, local, stat.Size, stat.Mtime, editor, launchEditor: true,
                s => RemoteEditSession.AskSave(OwnerWin(), s.RemotePath, s.HostLabel),
                SaveSession);
            session.Closed += s => Application.Current?.Dispatcher.BeginInvoke(() => OpenEdits.Remove(s));
            OpenEdits.Add(session);
            LastOperation = "правка " + f.FullPath + (Sudo ? " (sudo)" : "");
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), ex.Message, "Правка");
        }
    }

    public async Task SaveEdit(RemoteEditSession session)
    {
        if (await session.Commit(ask: false))
            LastOperation = "загружено " + session.RemotePath;
    }

    public async Task CloseEdit(RemoteEditSession session)
    {
        if (session.Dirty && !await session.Commit())
        {
            if (!AppDialog.Confirm(OwnerWin(), $"{session.RemotePath} не сохранён на сервере.\nЗакрыть правку? Изменения пропадут.", "Правка"))
                return;
        }

        session.Dispose();
    }

    private RemoteSaveResult SaveSession(RemoteEditSession session)
    {
        var sftp = _sftp;
        if (sftp is null || !ReferenceEquals(_host, session.Host))
            return new RemoteSaveResult { Detail = $"нет подключения к {session.Host.Title}. Подключитесь и нажмите «Сохранить»." };
        _io.Wait();
        try
        {
            var result = RemoteEditSession.SaveBack(
                sftp, session.Host, Sudo, session.LocalPath, session.RemotePath,
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
                    Host = session.Host.Display,
                    Action = session.RemotePath,
                    Status = result.Saved ? "OK" : "ERR",
                    Preview = $"{session.RemotePath} · {result.Size} байт · {result.Detail}"
                }
            });
            if (result.Saved)
            {
                var now = sftp.Stat(session.RemotePath);
                session.NoteRemote(now.Size, now.Mtime, RemoteEditSession.FileHash(session.LocalPath));
                Application.Current?.Dispatcher.BeginInvoke(() => LastOperation = "загружено " + session.RemotePath + " · " + result.Detail);
            }
            else if (!result.Conflict)
                Application.Current?.Dispatcher.BeginInvoke(() => AppDialog.Info(OwnerWin(), result.Detail, "Сохранение"));
            return result;
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => AppDialog.Info(OwnerWin(), ex.Message, "Сохранение"));
            return new RemoteSaveResult { Detail = ex.Message };
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>Перед отключением: несохранённые правки — загрузить или явно бросить. false — отключение отменено.</summary>
    private bool FlushEdits(bool force = false)
    {
        var list = OpenEdits.ToList();
        if (list.Count == 0) return true;
        var dirty = list.Where(s => s.Dirty).ToList();
        if (dirty.Count > 0 && !force)
        {
            var text = "Не сохранено на сервере:\n" + string.Join("\n", dirty.Select(s => s.RemotePath)) +
                       "\n\nДа — загрузить сейчас. Нет — остаться подключённым.";
            if (!AppDialog.Confirm(OwnerWin(), text, "Правка")) return false;
            foreach (var session in dirty)
            {
                var r = SaveSession(session);
                if (!r.Saved) return false;
            }
        }

        foreach (var session in list)
            session.Dispose();
        return true;
    }

    // ---------- Разное ----------

    private FileEntry? Active() => ActiveLocal ? SelectedLocal : SelectedRemote;

    private void NeedConnect() => AppDialog.Info(OwnerWin(), "Сначала подключитесь к хосту: двойной щелчок в списке слева.", "SSH Files");

    private static string SafeWindowsName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(p);
        }

        return "/" + string.Join('/', parts);
    }

    private static string Short(string text)
    {
        var line = text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 200 ? line[..200] : line;
    }

    private Window OwnerWin() => Owner?.Invoke() ?? Application.Current.MainWindow!;
}
