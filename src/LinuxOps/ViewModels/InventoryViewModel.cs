using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxOps.Models;

namespace LinuxOps.ViewModels;

/// <summary>Inventory: дерево хостов Linux и vCenter с папками. Выделение и «куда добавлять» даёт дерево.</summary>
public partial class InventoryViewModel : ObservableObject
{
    public InventoryViewModel(AppSession session) => Session = session;

    public AppSession Session { get; }

    public Func<Window?>? Owner { get; set; }

    /// <summary>Выделенный узел дерева (папка или ресурс).</summary>
    public TreeNode? Selected { get; set; }

    /// <summary>Папка для новых ресурсов: выделенная папка или папка выделенного ресурса.</summary>
    public HostFolder? TargetFolder { get; set; }

    [RelayCommand]
    private void AddHost() => HostDialogs.TryAdd(OwnerWin(), Session, TargetFolder?.Id);

    [RelayCommand]
    private void AddVCenter() => HostDialogs.TryAddVCenter(OwnerWin(), Session, TargetFolder?.Id);

    [RelayCommand]
    private void AddFolder() => HostDialogs.TryAddFolder(OwnerWin(), Session, TargetFolder?.Id);

    [RelayCommand]
    private void Edit()
    {
        switch (Selected)
        {
            case ItemNode i:
                HostDialogs.TryEdit(OwnerWin(), Session, i.Item);
                break;
            case FolderNode f:
                HostDialogs.TryRenameFolder(OwnerWin(), Session, f.Folder);
                break;
            default:
                AppDialog.Info(OwnerWin(), "Выделите хост, vCenter или папку.");
                break;
        }
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is ItemNode i) HostDialogs.TryDuplicate(OwnerWin(), Session, i.Item);
        else AppDialog.Info(OwnerWin(), Selected is FolderNode ? "Папку дублировать нельзя — выделите хост или vCenter." : "Выделите хост или vCenter.");
    }

    [RelayCommand]
    private void Remove()
    {
        switch (Selected)
        {
            case ItemNode i:
                HostDialogs.TryRemove(OwnerWin(), Session, i.Item);
                break;
            case FolderNode f:
                HostDialogs.TryRemoveFolder(OwnerWin(), Session, f.Folder);
                break;
        }
    }

    private Window OwnerWin() => Owner?.Invoke() ?? Application.Current.MainWindow!;
}
