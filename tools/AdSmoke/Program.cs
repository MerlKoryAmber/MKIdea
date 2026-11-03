using AdServerGroups.Models;
using AdServerGroups.Services;

if (args.Length < 2)
{
    Console.WriteLine("Usage: AdSmoke <DOMAIN\\user> <password>");
    return 1;
}

var settings = new AppSettings();
var creds = new SessionCredentials { Username = args[0], Password = args[1] };
using var ad = new AdDirectoryService(settings, creds);
ad.TestConnection();
Console.WriteLine("OK connect");
foreach (var s in ad.FindWindowsServers(settings.DefaultContainerDn))
    Console.WriteLine($"{s.Name} | {s.OperatingSystem}");
var name = SettingsStore.ResolveGroupName(settings, "SRV-APP01");
Console.WriteLine($"Group {name} exists={ad.GroupExists(name)}");
ad.EnsureGroup(name, SettingsStore.ResolveGroupDescription(settings, "SRV-APP01"));
Console.WriteLine($"Ensure done exists={ad.GroupExists(name)}");
return 0;
