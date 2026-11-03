using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxOps.Services;

namespace LinuxOps.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    public HistoryViewModel() => Reload();

    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    public Func<Window?>? Owner { get; set; }

    [ObservableProperty] private HistoryEntry? selected;
    [ObservableProperty] private string detail = "Выберите запись истории.";

    partial void OnSelectedChanged(HistoryEntry? value)
    {
        Detail = value is null
            ? "Выберите запись истории."
            : string.IsNullOrWhiteSpace(value.Detail) ? value.Preview : value.Detail;
    }

    [RelayCommand]
    public void Reload()
    {
        Entries.Clear();
        foreach (var h in HistoryStore.Load())
            Entries.Add(h);
    }

    [RelayCommand]
    private void Clear()
    {
        var owner = Owner?.Invoke() ?? Application.Current.MainWindow!;
        if (!AppDialog.Confirm(owner, "Очистить историю?"))
            return;
        HistoryStore.Clear();
        Reload();
        Selected = null;
    }
}
