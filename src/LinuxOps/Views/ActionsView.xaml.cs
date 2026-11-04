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
            vm.PropertyChanged += (_, pe) =>
            {
                // Живой вывод RUN — держим низ видимым, как в консоли.
                if (pe.PropertyName == nameof(ActionsViewModel.Detail) && vm.SelectedResult is { Status: "RUN" })
                    Dispatcher.BeginInvoke(DetailBox.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
            };
        };
    }

    private void OnActionsReloaded()
    {
        _vm?.RefreshPreview();
    }

    private void PlanCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _vm is null || _vm.SuspendSelection || _bulk) return;
        _vm.PersistPlan();
        _vm.RefreshPreview();
    }

    private static ActionItemView? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ActionItemView;

    private ActionItemView? _anchor;
    private HashSet<ActionItemView>? _beforeClick;
    private bool _bulk;

    /// <summary>
    /// Как в проводнике: щелчок (и по галке тоже) — в плане только это действие, повторный щелчок по единственному — снять.
    /// Ctrl — добавить или убрать одно, Shift — диапазон от предыдущего щелчка. Двойной щелчок — «Изменить», план не меняется.
    /// </summary>
    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item || _vm is null) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject, (DependencyObject)sender)) return;
        e.Handled = true;
        var all = _vm.Actions.ToList();
        if (e.ClickCount == 2)
        {
            if (_beforeClick is not null) SetPlan(all, a => _beforeClick.Contains(a));
            _vm.EditFromCard(item);
            return;
        }

        _beforeClick = all.Where(a => a.IsSelected).ToHashSet();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && _anchor is not null && all.Contains(_anchor))
        {
            var a = all.IndexOf(_anchor);
            var b = all.IndexOf(item);
            var (lo, hi) = a < b ? (a, b) : (b, a);
            var keep = mods.HasFlag(ModifierKeys.Control) ? _beforeClick : new HashSet<ActionItemView>();
            SetPlan(all, x => keep.Contains(x) || (all.IndexOf(x) >= lo && all.IndexOf(x) <= hi));
            return;
        }

        _anchor = item;
        if (mods.HasFlag(ModifierKeys.Control))
            SetPlan(all, x => ReferenceEquals(x, item) ? !x.IsSelected : x.IsSelected);
        else
        {
            var onlyThis = item.IsSelected && _beforeClick.Count == 1;
            SetPlan(all, x => !onlyThis && ReferenceEquals(x, item));
        }
    }

    /// <summary>Поменять план разом: сохранить и пересчитать предпросмотр один раз.</summary>
    private void SetPlan(List<ActionItemView> all, Func<ActionItemView, bool> inPlan)
    {
        _bulk = true;
        try
        {
            foreach (var a in all) a.IsSelected = inPlan(a);
        }
        finally
        {
            _bulk = false;
        }

        _vm?.PersistPlan();
        _vm?.RefreshPreview();
    }

    private static bool IsInsideButton(DependencyObject? d, DependencyObject stop)
    {
        while (d is not null && !ReferenceEquals(d, stop))
        {
            // Галка «В плане» — часть карточки и ведёт себя так же; свои клики — только у значков.
            if (d is ButtonBase and not CheckBox) return true;
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
