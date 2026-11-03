using System.Windows;
using System.Windows.Controls;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is HistoryViewModel vm)
                vm.Owner = () => Window.GetWindow(this);
        };
    }
}
