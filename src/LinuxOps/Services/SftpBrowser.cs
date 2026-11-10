using System.IO;
using LinuxOps.Models;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace LinuxOps.Services;

public sealed class FileEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsParentNav { get; init; }
    public bool IsSymlink { get; init; }
    public bool IsHidden { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    /// <summary>Биты прав (07777). -1 — неизвестно (локальный файл).</summary>
    public int Mode { get; init; } = -1;
    public string Owner { get; init; } = "";
    public string Group { get; init; } = "";

    public string SizeText => IsParentNav ? "" : IsDirectory ? "" : FormatSize(Size);
    public string ModifiedText => IsParentNav || Modified == default ? "" : Modified.ToString("dd.MM.yyyy HH:mm");
    public string Kind => IsParentNav || IsDirectory ? "dir" : "file";
    public string Glyph => IsDirectory || IsParentNav ? "" : "";
    public string RightsText => Mode < 0 || IsParentNav ? "" : (IsSymlink ? "l" : IsDirectory ? "d" : "-") + Rights.ToText(Mode);
    public string OwnerText => IsParentNav || Owner.Length == 0 ? "" : Group.Length == 0 ? Owner : Owner + ":" + Group;
    public string DisplayName => IsSymlink ? Name + "  ↗" : Name;

    public static string FormatSize(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024.0:0.#} KB";
        if (n < 1024L * 1024 * 1024) return $"{n / (1024.0 * 1024.0):0.#} MB";
        return $"{n / (1024.0 * 1024.0 * 1024.0):0.##} GB";
    }
}

public static class Rights
{
    public static string ToText(int mode)
    {
        char B(int bit, char c) => (mode & bit) != 0 ? c : '-';
        var s = new[]
        {
            B(0x100, 'r'), B(0x80, 'w'), B(0x40, 'x'),
            B(0x20, 'r'), B(0x10, 'w'), B(0x8, 'x'),
            B(0x4, 'r'), B(0x2, 'w'), B(0x1, 'x')
        };
        if ((mode & 0x800) != 0) s[2] = s[2] == 'x' ? 's' : 'S';
        if ((mode & 0x400) != 0) s[5] = s[5] == 'x' ? 's' : 'S';
        if ((mode & 0x200) != 0) s[8] = s[8] == 'x' ? 't' : 'T';
        return new string(s);
    }

    public static string ToOctal(int mode) => Convert.ToString(mode & 0xFFF, 8).PadLeft(4, '0');

    public static int FromAttributes(SftpFileAttributes a)
    {
        var m = 0;
        if (a.OwnerCanRead) m |= 0x100;
        if (a.OwnerCanWrite) m |= 0x80;
        if (a.OwnerCanExecute) m |= 0x40;
        if (a.GroupCanRead) m |= 0x20;
        if (a.GroupCanWrite) m |= 0x10;
        if (a.GroupCanExecute) m |= 0x8;
        if (a.OthersCanRead) m |= 0x4;
        if (a.OthersCanWrite) m |= 0x2;
        if (a.OthersCanExecute) m |= 0x1;
        if (a.IsUIDBitSet) m |= 0x800;
        if (a.IsGroupIDBitSet) m |= 0x400;
        if (a.IsStickyBitSet) m |= 0x200;
        return m;
    }
}

/// <summary>Один файл в дереве для копирования: путь относительно корня копирования.</summary>
public sealed record TreeItem(string FullPath, string Relative, bool IsDirectory, long Size, DateTime Modified);

public sealed class SftpBrowser : IDisposable
{
    private readonly SftpClient _client;
    private bool _disposed;
    private Dictionary<int, string> _users = new();
    private Dictionary<int, string> _groups = new();

    private SftpBrowser(SftpClient client)
    {
        _client = client;
        Home = client.WorkingDirectory;
    }

    public static SftpBrowser Connect(HostConfig host)
    {
        var client = new SftpClient(SshAuth.Build(host));
        SshAuth.AttachHostKey(client, host);
        client.Connect();
        return new SftpBrowser(client);
    }

    /// <summary>Домашняя папка пользователя SSH (рабочая папка сразу после входа).</summary>
    public string Home { get; }

    public bool IsConnected => !_disposed && _client.IsConnected;

    public string WorkingDirectory => _client.WorkingDirectory;

    public void ChangeDirectory(string path) => _client.ChangeDirectory(path);

    /// <summary>Имена пользователей и групп по UID/GID (из getent на хосте).</summary>
    public void SetNames(Dictionary<int, string> users, Dictionary<int, string> groups)
    {
        _users = users;
        _groups = groups;
    }

    public IReadOnlyList<FileEntry> List(string? path = null, bool showHidden = true)
    {
        var dir = string.IsNullOrWhiteSpace(path) ? _client.WorkingDirectory : path;
        var list = new List<FileEntry>();
        var cwd = dir.TrimEnd('/');
        if (string.IsNullOrEmpty(cwd)) cwd = "/";
        if (cwd != "/")
            list.Add(new FileEntry { Name = "..", FullPath = Parent(cwd), IsDirectory = true, IsParentNav = true });

        foreach (ISftpFile f in _client.ListDirectory(dir))
        {
            if (f.Name is "." or "..") continue;
            var hidden = f.Name.StartsWith('.');
            if (hidden && !showHidden) continue;
            var attrs = f.Attributes;
            var isDir = f.IsDirectory;
            if (f.IsSymbolicLink)
            {
                // Ссылка на папку должна открываться как папка.
                try { isDir = _client.GetAttributes(f.FullName).IsDirectory; }
                catch { isDir = false; }
            }

            list.Add(new FileEntry
            {
                Name = f.Name,
                FullPath = f.FullName,
                IsDirectory = isDir,
                IsSymlink = f.IsSymbolicLink,
                IsHidden = hidden,
                Size = f.Length,
                Modified = f.LastWriteTime,
                Mode = Rights.FromAttributes(attrs),
                Owner = _users.TryGetValue(f.UserId, out var u) ? u : f.UserId.ToString(),
                Group = _groups.TryGetValue(f.GroupId, out var g) ? g : f.GroupId.ToString()
            });
        }

        return list;
    }

    /// <summary>
    /// Список папки от root (режим «Права: sudo», когда своему пользователю папка закрыта).
    /// Первая строка — канонический путь, дальше по строке на объект.
    /// </summary>
    public const string RootListScript = """
        cd -- "$LINUXOPS_DIR" || exit 2
        pwd -P
        find . -mindepth 1 -maxdepth 1 -printf '%y\t%Y\t%s\t%T@\t%m\t%u\t%g\t%f\n'
        """;

    public static (string Dir, List<FileEntry> Entries) ParseRootList(string stdout)
    {
        var lines = stdout.Replace("\r", "").Split('\n');
        var dir = lines.Length > 0 && lines[0].StartsWith('/') ? lines[0].Trim() : "/";
        var list = new List<FileEntry>();
        if (dir != "/")
            list.Add(new FileEntry { Name = "..", FullPath = Parent(dir), IsDirectory = true, IsParentNav = true });
        foreach (var line in lines.Skip(1))
        {
            var p = line.Split('\t', 8);
            if (p.Length < 8 || p[7].Length == 0) continue;
            var type = p[0];
            var target = p[1];
            var mode = Convert.ToInt32(p[4], 8) | type switch { "d" => 0x4000, "l" => 0xA000, _ => 0x8000 };
            var seconds = double.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
            list.Add(new FileEntry
            {
                Name = p[7],
                FullPath = Join(dir, p[7]),
                IsDirectory = type == "d" || (type == "l" && target == "d"),
                IsSymlink = type == "l",
                IsHidden = p[7].StartsWith('.'),
                Size = long.TryParse(p[2], out var size) ? size : 0,
                Modified = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).LocalDateTime,
                Mode = mode,
                Owner = p[5],
                Group = p[6]
            });
        }

        return (dir, list);
    }

    public byte[] ReadHead(string remotePath, int maxBytes)
    {
        using var stream = _client.OpenRead(remotePath);
        var n = (int)Math.Min(maxBytes, Math.Max(0, stream.Length));
        var buf = new byte[n];
        var off = 0;
        while (off < n)
        {
            var read = stream.Read(buf, off, n - off);
            if (read == 0) break;
            off += read;
        }

        return off == buf.Length ? buf : buf[..off];
    }

    public void Download(string remotePath, string localPath)
    {
        using var fs = File.Create(localPath);
        _client.DownloadFile(remotePath, fs);
    }

    public async Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
        var relay = progress is null
            ? null
            : new Progress<DownloadFileProgressReport>(r => progress.Report((long)r.TotalBytesDownloaded));
        await _client.DownloadFileAsync(remotePath, fs, relay, cancellationToken);
    }

    public async Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        var relay = progress is null
            ? null
            : new Progress<UploadFileProgressReport>(r => progress.Report((long)r.TotalBytesUploaded));
        await _client.UploadFileAsync(fs, remotePath, true, relay, cancellationToken);
    }

    /// <summary>Все файлы и папки под remotePath (сама папка — с Relative = её имя).</summary>
    public List<TreeItem> Tree(string remotePath)
    {
        var result = new List<TreeItem>();
        var attrs = _client.GetAttributes(remotePath);
        var name = Name(remotePath);
        if (!attrs.IsDirectory)
        {
            result.Add(new TreeItem(remotePath, name, false, attrs.Size, attrs.LastWriteTime));
            return result;
        }

        void Walk(string path, string rel)
        {
            result.Add(new TreeItem(path, rel, true, 0, default));
            foreach (var f in _client.ListDirectory(path))
            {
                if (f.Name is "." or "..") continue;
                var childRel = rel + "/" + f.Name;
                if (f.IsDirectory) Walk(f.FullName, childRel);
                else if (f.IsSymbolicLink) continue; // ссылки внутри дерева не копируем, чтобы не уйти в цикл
                else result.Add(new TreeItem(f.FullName, childRel, false, f.Length, f.LastWriteTime));
            }
        }

        Walk(remotePath, name);
        return result;
    }

    public bool Exists(string remotePath) => _client.Exists(remotePath);

    public (long Size, DateTime Mtime, bool IsDirectory) StatFull(string remotePath)
    {
        var a = _client.GetAttributes(remotePath);
        return (a.Size, a.LastWriteTime, a.IsDirectory);
    }

    public int CountFiles(string remotePath) => Tree(remotePath).Count(t => !t.IsDirectory);

    public void Delete(string remotePath)
    {
        var attrs = _client.GetAttributes(remotePath);
        if (!attrs.IsDirectory)
        {
            _client.DeleteFile(remotePath);
            return;
        }

        foreach (var f in _client.ListDirectory(remotePath).ToList())
        {
            if (f.Name is "." or "..") continue;
            if (f.IsSymbolicLink || !f.IsDirectory) _client.DeleteFile(f.FullName);
            else Delete(f.FullName);
        }

        _client.DeleteDirectory(remotePath);
    }

    /// <summary>Удалить сам объект (ссылку — как ссылку, без захода внутрь).</summary>
    public void DeleteEntry(FileEntry entry)
    {
        if (entry.IsSymlink) _client.DeleteFile(entry.FullPath);
        else Delete(entry.FullPath);
    }

    public void MkDir(string remotePath) => _client.CreateDirectory(remotePath);

    public void EnsureDirectory(string remotePath)
    {
        if (_client.Exists(remotePath)) return;
        _client.CreateDirectory(remotePath);
    }

    public void CreateEmpty(string remotePath)
    {
        using var s = _client.Open(remotePath, FileMode.CreateNew, FileAccess.Write);
    }

    public void Rename(string fromPath, string toPath) => _client.RenameFile(fromPath, toPath);

    public void Chmod(string remotePath, int mode) => _client.ChangePermissions(remotePath, SshNetMode(mode));

    /// <summary>
    /// SSH.NET ждёт права «восьмеричными цифрами в десятичном числе»: 644 значит rw-r--r--.
    /// Биты (0x1A4) напрямую передавать нельзя. setuid/setgid/sticky так не задать.
    /// </summary>
    public static short SshNetMode(int mode) => short.Parse(Convert.ToString(mode & 0x1FF, 8));

    public (long Size, DateTime Mtime) Stat(string remotePath)
    {
        var attrs = _client.GetAttributes(remotePath);
        return (attrs.Size, attrs.LastWriteTime);
    }

    public byte[] ReadAll(string remotePath) => _client.ReadAllBytes(remotePath);

    /// <summary>Имя резервной копии: путь без «/» и время. Копии лежат в ~/.linuxops-bak, а не рядом с файлом:
    /// рядом их подхватили бы cron.d, logrotate.d и другие каталоги *.d.</summary>
    public static string BackupName(string remotePath)
        => remotePath.TrimStart('/').Replace('/', '%') + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss");

    public const string BackupDir = ".linuxops-bak";

    /// <summary>
    /// Записать содержимое в существующий файл на месте (открыть с обрезкой).
    /// Inode, права, владелец, метка SELinux и символические ссылки сохраняются.
    /// Перед записью прежняя версия копируется в ~/.linuxops-bak (папка 700, права копии — как у файла).
    /// </summary>
    public string WriteInPlace(string localPath, string remotePath)
    {
        string note;
        try
        {
            var old = _client.ReadAllBytes(remotePath);
            var mode = Rights.FromAttributes(_client.GetAttributes(remotePath));
            var dir = Join(Home, BackupDir);
            if (!_client.Exists(dir))
            {
                _client.CreateDirectory(dir);
                _client.ChangePermissions(dir, SshNetMode(0x1C0)); // 0700
            }

            var bak = Join(dir, BackupName(remotePath));
            // Сначала пустой файл и права оригинала, потом содержимое: копия /etc/shadow не должна стать 644.
            using (_client.Open(bak, FileMode.Create, FileAccess.Write)) { }
            _client.ChangePermissions(bak, SshNetMode(mode));
            using (var s = _client.Open(bak, FileMode.Truncate, FileAccess.Write))
                s.Write(old, 0, old.Length);
            note = "копия " + bak;
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SftpPermissionDeniedException or Renci.SshNet.Common.SshException)
        {
            note = "без резервной копии: " + ex.Message;
        }

        var data = File.ReadAllBytes(localPath);
        using (var s = _client.Open(remotePath, FileMode.Truncate, FileAccess.Write))
            s.Write(data, 0, data.Length);
        return note;
    }

    /// <summary>Временный файл в /tmp с правами 600 — читать его может только владелец и root.</summary>
    public string Stage(string localPath)
    {
        var remote = "/tmp/linuxops-edit-" + Guid.NewGuid().ToString("N");
        using (_client.Open(remote, FileMode.CreateNew, FileAccess.Write)) { }
        _client.ChangePermissions(remote, SshNetMode(0x180)); // 0600 до записи содержимого
        using var fs = File.Open(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _client.UploadFile(fs, remote, true);
        return remote;
    }

    public static string Parent(string remote)
    {
        var cut = remote.TrimEnd('/');
        var idx = cut.LastIndexOf('/');
        return idx <= 0 ? "/" : cut[..idx];
    }

    public static string Name(string remote)
    {
        var cut = remote.TrimEnd('/');
        var idx = cut.LastIndexOf('/');
        return idx < 0 ? cut : cut[(idx + 1)..];
    }

    public static string Join(string dir, string name) => (dir.TrimEnd('/') + "/" + name).Replace("//", "/");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch { /* связь уже оборвалась */ }

        _client.Dispose();
    }
}
