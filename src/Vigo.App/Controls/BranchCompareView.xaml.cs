using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Controls;

/// <summary>
/// Comparison of two branches: files grouped by category, filters, A → B diff of a file in the code editor. Échap goes
/// back to the graph.
/// </summary>
public sealed partial class BranchCompareView : UserControl
{
    private BranchCompareViewModel? _viewModel;

    public BranchCompareView()
    {
        InitializeComponent();
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            _viewModel?.CloseCommand.Execute(null);
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Comparison shown by the control; set once by the repository view.</summary>
    public BranchCompareViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    public void Release() => ViewModel = null;

    /// <summary>Name of a side, or an invitation to pick one.</summary>
    public static string Shown(string name) => name.Length == 0 ? Loc.Get("Compare.Pick") : name;

    private void OnCloseClick(object sender, RoutedEventArgs e) => _viewModel?.CloseCommand.Execute(null);

    private void OnFileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BranchCompareRow row) _viewModel?.OpenFileCommand.Execute(row);
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        // The button toggled itself: the view model decides the state and sets it back through the binding.
        if (sender is FrameworkElement { DataContext: BranchCompareChip chip }) _viewModel?.ToggleChipCommand.Execute(chip);
    }

    private void OnSideAOpening(object sender, object e) => _viewModel?.PickerA.Refresh();

    private void OnSideBOpening(object sender, object e) => _viewModel?.PickerB.Refresh();

    private void OnSideAItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BranchPickerItem item) _viewModel?.PickerA.ChooseCommand.Execute(item);
        SideAFlyout.Hide();
    }

    private void OnSideBItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BranchPickerItem item) _viewModel?.PickerB.ChooseCommand.Execute(item);
        SideBFlyout.Hide();
    }
}
