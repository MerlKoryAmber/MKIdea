using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            vm.CurrentAction = () => ActionsList.SelectedItem as ActionItemView;
            vm.ActionsReloaded += OnActionsReloaded;
        };
    }

    private void OnActionsReloaded()
    {
        _vm?.RefreshPreview();
    }

    private void ActionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _vm.SuspendSelection) return;
        _vm.NoteCursor(ActionsList.SelectedItem as ActionItemView);
    }

    private void PlanCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _vm is null || _vm.SuspendSelection) return;
        _vm.PersistPlan();
        _vm.RefreshPreview();
    }

    private void ActionsList_DoubleClick(object sender, MouseButtonEventArgs e)
        => _vm?.EditFromCard(ActionsList.SelectedItem as ActionItemView);

    private void HostsList_DoubleClick(object sender, MouseButtonEventArgs e)
        => _vm?.EditHost(HostsList.SelectedItem as HostConfig);

    private void SelectAllHosts_Click(object sender, RoutedEventArgs e)
        => _vm?.SetAllHosts(SelectAllHosts.IsChecked == true);
}
