using System.Collections.ObjectModel;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.ViewModels;

/// <summary>Общий список хостов для Inventory, Actions и SSH.</summary>
public sealed class AppSession
{
    public ObservableCollection<HostConfig> Hosts { get; } = new();

    public void Reload()
    {
        Hosts.Clear();
        foreach (var h in DefaultsStore.Load().Hosts)
            Hosts.Add(h);
    }

    public void Persist() => DefaultsStore.SaveHosts(Hosts);
}
