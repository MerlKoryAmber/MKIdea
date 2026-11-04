using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class ActionsView : UserControl
{
    private ActionsViewModel? _vm;

    public ActionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not ActionsViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.ReadActions = () => vm.Actions.Where(a => a.IsSelected).Select(a => a.Definition).ToList();
            vm.ActionsReloaded += OnActionsReloaded;
        };
    }

    private void OnActionsReloaded()
    {
        _vm?.RefreshPreview();
    }

    private void PlanCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _vm is null || _vm.SuspendSelection) return;
        _vm.PersistPlan();
        _vm.RefreshPreview();
    }

    private static ActionItemView? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ActionItemView;

    /// <summary>Щелчок по карточке — в план / из плана. Двойной — изменить. По галке и значкам — их собственное действие.</summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item || _vm is null) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject, (DependencyObject)sender)) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            // Первый щелчок уже переключил галку — возвращаем, двойной щелчок значит только «изменить».
            item.IsSelected = !item.IsSelected;
            _vm.EditFromCard(item);
            return;
        }

        item.IsSelected = !item.IsSelected;
    }

    private static bool IsInsideButton(DependencyObject? d, DependencyObject stop)
    {
        while (d is not null && !ReferenceEquals(d, stop))
        {
            if (d is ButtonBase) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }

        return false;
    }

    private void CardMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not ActionItemView item) return;
        var remove = menu.Items.OfType<MenuItem>().LastOrDefault();
        if (remove is null) return;
        remove.IsEnabled = !item.Definition.BuiltIn;
        remove.Header = item.Definition.BuiltIn ? "Удалить (встроенное — нельзя)" : "Удалить";
    }

    private void CardEdit_Click(object sender, RoutedEventArgs e) => _vm?.EditFromCard(ItemOf(sender));

    private void CardClone_Click(object sender, RoutedEventArgs e) => _vm?.CloneFromCard(ItemOf(sender));

    private void CardRemove_Click(object sender, RoutedEventArgs e) => _vm?.RemoveFromCard(ItemOf(sender));

    private void HostsList_DoubleClick(object sender, MouseButtonEventArgs e)
        => _vm?.EditHost(HostsList.SelectedItem as HostConfig);

    private void SelectAllHosts_Click(object sender, RoutedEventArgs e)
        => _vm?.SetAllHosts(SelectAllHosts.IsChecked == true);
}
