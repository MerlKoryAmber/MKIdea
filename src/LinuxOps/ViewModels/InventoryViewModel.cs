using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxOps.Models;

namespace LinuxOps.ViewModels;

public partial class InventoryViewModel : ObservableObject
{
    private readonly AppSession _session;

    public InventoryViewModel(AppSession session) => _session = session;

    public ObservableCollection<HostConfig> Hosts => _session.Hosts;

    public Func<Window?>? Owner { get; set; }

    [ObservableProperty] private HostConfig? selectedHost;

    [RelayCommand]
    private void Add() => HostDialogs.TryAdd(OwnerWin(), _session);

    [RelayCommand]
    private void Edit()
    {
        if (SelectedHost is null)
        {
            AppDialog.Info(OwnerWin(), "Нет хоста для правки.");
            return;
        }

        HostDialogs.TryEdit(OwnerWin(), _session, SelectedHost);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (SelectedHost is null)
        {
            AppDialog.Info(OwnerWin(), "Нет хоста для дублирования.");
            return;
        }

        if (!HostDialogs.TryDuplicate(OwnerWin(), _session, SelectedHost)) return;
        SelectedHost = Hosts.Count > 0 ? Hosts[^1] : null;
    }

    [RelayCommand]
    private void Remove()
    {
        if (SelectedHost is null) return;
        HostDialogs.TryRemove(OwnerWin(), _session, SelectedHost);
    }

    private Window OwnerWin() => Owner?.Invoke() ?? Application.Current.MainWindow!;
}
