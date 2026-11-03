using LinuxOps.Models;
using LinuxOps.Services;

var host = new HostConfig
{
    Name = "lab-centos",
    Host = args.ElementAtOrDefault(0) ?? "192.168.0.178",
    Port = 22,
    Username = args.ElementAtOrDefault(1) ?? "root",
    Auth = "Key",
    PrivateKeyPath = args.ElementAtOrDefault(2)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "radiusproxy_ed25519")
};

Console.WriteLine($"LAB {host.Username}@{host.Host} key={host.PrivateKeyPath}");

using var ssh = SshRunner.Connect(host);
var distro = ssh.DetectDistro();
Console.WriteLine($"distro: {distro.PrettyName} family={distro.Family} id={distro.Id}");

var ids = new[] { "check-updates", "list-users", "ensure-user-demo" };
var fail = 0;
foreach (var id in ids)
{
    var def = ActionCatalog.LoadAll().First(a => a.Id == id);
    if (!ActionCatalog.Supports(def, distro))
    {
        Console.WriteLine($"[{id}] SKIP unsupported");
        continue;
    }

    var script = ActionCatalog.ResolveScript(def, distro);
    var crlf = script.Contains('\r');
    Console.WriteLine($"[{id}] script_len={script.Length} has_CR={crlf}");
    var r = ssh.Run(id, script);
    var ok = r.Ok;
    if (!ok) fail++;
    Console.WriteLine($"[{id}] {(ok ? "OK" : "FAIL")} exit={r.ExitCode}");
    var preview = (r.StdOut + "\n" + r.StdErr).Replace("\r", "");
    foreach (var line in preview.Split('\n').Where(l => l.Trim().Length > 0).Take(6))
        Console.WriteLine("  " + line);
    if (!string.IsNullOrWhiteSpace(r.StdErr) && !ok)
        Console.WriteLine("  STDERR_TAIL: " + r.StdErr.Split('\n').LastOrDefault()?.Trim());
}

// SFTP smoke
try
{
    using var sftp = LinuxOps.Services.SftpBrowser.Connect(host);
    var entries = sftp.List("/etc");
    Console.WriteLine($"[sftp] /etc entries={entries.Count} cwd sample={entries.FirstOrDefault()?.Name}");
    if (entries.Count == 0) fail++;
}
catch (Exception ex)
{
    Console.WriteLine("[sftp] FAIL " + ex.Message);
    fail++;
}

// Reproduce former crash: touch DistroColorBrush then SaveHosts (JSON must ignore Brush)
try
{
    host.DistroHint = "centos";
    host.Status = "Online";
    _ = host.DistroColorBrush;
    DefaultsStore.SaveHosts(new[] { host });
    Console.WriteLine("[probe-save] OK DistroHint persisted");
}
catch (Exception ex)
{
    Console.WriteLine("[probe-save] FAIL " + ex);
    fail++;
}

Console.WriteLine(fail == 0 ? "LAB_CHECK_OK" : $"LAB_CHECK_FAIL count={fail}");
return fail == 0 ? 0 : 1;
