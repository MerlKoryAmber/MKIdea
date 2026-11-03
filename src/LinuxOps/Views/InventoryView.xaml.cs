using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is InventoryViewModel vm)
                vm.Owner = () => Window.GetWindow(this);
        };
    }

    private void HostsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
        => (DataContext as InventoryViewModel)?.EditCommand.Execute(null);
}
