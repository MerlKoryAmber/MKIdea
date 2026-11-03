using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class ActionsView : UserControl
{
    private ActionsViewModel? _vm;
    private bool _restoring;

    public ActionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not ActionsViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.ReadActions = ReadActions;
            vm.CurrentAction = () => ActionsList.SelectedItem as ActionItemView;
            vm.ActionsReloaded += OnActionsReloaded;
        };
    }

    private IReadOnlyList<ActionDefinition> ReadActions()
        => ActionsList.SelectedItems.Cast<ActionItemView>().Select(a => a.Definition).ToList();

    private void OnActionsReloaded()
    {
        _restoring = true;
        Dispatcher.BeginInvoke(() =>
        {
            ActionsList.SelectedItems.Clear();
            if (_vm is null)
            {
                _restoring = false;
                return;
            }

            foreach (var item in _vm.Actions.Where(a => a.IsSelected))
                ActionsList.SelectedItems.Add(item);
            _restoring = false;
            _vm.SyncSelection(ActionsList.SelectedItems.Cast<ActionItemView>(), persist: true);
        });
    }

    private void ActionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring || _vm is null || _vm.SuspendSelection) return;
        _vm.SyncSelection(ActionsList.SelectedItems.Cast<ActionItemView>(), IsLoaded);
    }

    private void ActionsList_DoubleClick(object sender, MouseButtonEventArgs e)
        => _vm?.EditFromCard(ActionsList.SelectedItem as ActionItemView);

    private void HostsList_DoubleClick(object sender, MouseButtonEventArgs e)
        => _vm?.EditHost(HostsList.SelectedItem as HostConfig);

    private void SelectAllHosts_Click(object sender, RoutedEventArgs e)
        => _vm?.SetAllHosts(SelectAllHosts.IsChecked == true);
}
