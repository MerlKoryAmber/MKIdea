using Renci.SshNet;

var host = args.ElementAtOrDefault(0) ?? "192.168.0.178";
var user = args.ElementAtOrDefault(1) ?? "root";
var key = args.ElementAtOrDefault(2)
          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "radiusproxy_ed25519");

Console.WriteLine($"connect {user}@{host} key={key}");
using var keyFile = new PrivateKeyFile(key);
var auth = new PrivateKeyAuthenticationMethod(user, keyFile);
var info = new ConnectionInfo(host, 22, user, auth) { Timeout = TimeSpan.FromSeconds(20) };
using var client = new SshClient(info);
client.Connect();
using (var cmd = client.CreateCommand("cat /etc/os-release | head -5; echo ---; (dnf -q check-update || true) | head -20; echo SMOKE_OK"))
{
    cmd.CommandTimeout = TimeSpan.FromMinutes(3);
    var output = cmd.Execute();
    Console.WriteLine(output);
    Console.WriteLine($"exit={cmd.ExitStatus}");
    if ((cmd.Error ?? "").Length > 0) Console.WriteLine("stderr=" + cmd.Error);
}
client.Disconnect();
