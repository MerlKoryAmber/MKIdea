using System.Windows;
using System.Windows.Controls;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not InventoryViewModel vm) return;
            vm.Owner = () => Window.GetWindow(this);
            TreeView.Bind(vm.Session);
            TreeView.SelectionChanged += _ => Sync(vm);
            TreeView.Rebuilt += () => Sync(vm);
            TreeView.ItemActivated += _ => vm.EditCommand.Execute(null);
        };
    }

    private void Sync(InventoryViewModel vm)
    {
        vm.Selected = TreeView.SelectedNode;
        vm.TargetFolder = TreeView.TargetFolder;
    }
}
