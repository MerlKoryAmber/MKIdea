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
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string SizeText => IsDirectory || IsParentNav ? "<папка>" : FormatSize(Size);
    public string ModifiedText => IsParentNav || Modified == default ? "—" : Modified.ToString("dd.MM.yyyy HH:mm");
    public string Kind => IsParentNav || IsDirectory ? "dir" : "file";
    public string Glyph => IsDirectory || IsParentNav ? "\uE8B7" : "\uE8A5";

    private static string FormatSize(long n)
    {
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return $"{n / 1024.0:0.#} KB";
        return $"{n / (1024.0 * 1024.0):0.#} MB";
    }
}

public sealed class SftpBrowser : IDisposable
{
    private readonly SftpClient _client;
    private bool _disposed;

    private SftpBrowser(SftpClient client) => _client = client;

    public static SftpBrowser Connect(HostConfig host)
    {
        var client = new SftpClient(SshAuth.Build(host));
        SshAuth.AttachHostKey(client, host);
        client.Connect();
        return new SftpBrowser(client);
    }

    public string WorkingDirectory => _client.WorkingDirectory;

    public void ChangeDirectory(string path) => _client.ChangeDirectory(path);

    public IReadOnlyList<FileEntry> List(string? path = null)
    {
        var dir = string.IsNullOrWhiteSpace(path) ? _client.WorkingDirectory : path;
        var list = new List<FileEntry>();
        var cwd = dir.TrimEnd('/');
        if (string.IsNullOrEmpty(cwd)) cwd = "/";
        if (cwd != "/")
        {
            var idx = cwd.LastIndexOf('/');
            var parent = idx <= 0 ? "/" : cwd[..idx];
            list.Add(new FileEntry
            {
                Name = "..",
                FullPath = parent,
                IsDirectory = true,
                IsParentNav = true
            });
        }

        foreach (ISftpFile f in _client.ListDirectory(dir))
        {
            if (f.Name is "." or "..") continue;
            list.Add(new FileEntry
            {
                Name = f.Name,
                FullPath = f.FullName,
                IsDirectory = f.IsDirectory,
                Size = f.Length,
                Modified = f.LastWriteTime
            });
        }

        return list
            .OrderByDescending(x => x.IsParentNav)
            .ThenByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    public void Upload(string localPath, string remotePath)
    {
        using var fs = File.OpenRead(localPath);
        _client.UploadFile(fs, remotePath, true);
    }

    public async Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        var relay = progress is null
            ? null
            : new Progress<UploadFileProgressReport>(r => progress.Report((long)r.TotalBytesUploaded));
        await _client.UploadFileAsync(fs, remotePath, true, relay, cancellationToken);
    }

    public int CountFiles(string remotePath)
    {
        var attrs = _client.GetAttributes(remotePath);
        if (!attrs.IsDirectory) return 1;
        var n = 0;
        foreach (var f in _client.ListDirectory(remotePath))
        {
            if (f.Name is "." or "..") continue;
            n += f.IsDirectory ? CountFiles(f.FullName) : 1;
        }

        return n;
    }

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
            Delete(f.FullName);
        }

        _client.DeleteDirectory(remotePath);
    }

    public void MkDir(string remotePath) => _client.CreateDirectory(remotePath);

    public void Rename(string fromPath, string toPath) => _client.RenameFile(fromPath, toPath);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_client.IsConnected) _client.Disconnect();
        _client.Dispose();
    }
}
