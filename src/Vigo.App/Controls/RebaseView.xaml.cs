using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.System;
using Windows.UI.Text;

namespace Vigo.App.Controls;

/// <summary>
/// Interactive rebase editor (J5.5): list of the commits to replay with their action, result card. Keys of the list
/// (mockup): P R E S F D set the action of the selected row, Alt+↑ / Alt+↓ move it; Échap goes back to the graph.
/// </summary>
public sealed partial class RebaseView : UserControl
{
    private InteractiveRebaseViewModel? _viewModel;

    public RebaseView()
    {
        InitializeComponent();
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            Close();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Interactive rebase shown by the control; set once by the repository view.</summary>
    public InteractiveRebaseViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = value;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Bindings.Update();
        }
    }

    public void Release() => ViewModel = null;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Still collapsed at this point: the keyboard goes to the list once the visibility binding has run.
        if (e.PropertyName == nameof(InteractiveRebaseViewModel.IsOpen) && _viewModel?.IsOpen == true)
            DispatcherQueue.TryEnqueue(() => FocusSelectedRow(FocusState.Programmatic));
    }

    // ---- x:Bind helpers ------------------------------------------------------------------------------------------

    /// <summary>Dot of an action: shown when <paramref name="index"/> is the action of the row.</summary>
    public static Visibility Shows(int actionIndex, int index) => actionIndex == index ? Visibility.Visible : Visibility.Collapsed;

    public static TextDecorations Strike(bool dropped) => dropped ? TextDecorations.Strikethrough : TextDecorations.None;

    /// <summary>Squash and fixup rows are indented under the commit they meld into (mockup).</summary>
    public static Thickness Indent(bool melded) => new(melded ? 20 : 12, 0, 12, 0);

    /// <summary>Message dimmed for a melded commit, more for a dropped one (mockup: text-2, text-3).</summary>
    public static double Dim(bool melded, bool dropped) => dropped ? 0.5 : melded ? 0.75 : 1;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A drag and drop removes the row for a moment (no selection): the selection comes back with it.
        if (_viewModel is not null && RowList.SelectedItem is RebaseRow row) _viewModel.SelectedRow = row;
    }

    // ---- Keyboard ------------------------------------------------------------------------------------------------

    private void OnRowsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_viewModel is not { } vm) return;
        // Typing a new message or choosing in the action list: their own keys.
        if (e.OriginalSource is TextBox or ComboBox or ComboBoxItem) return;

        var alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                   & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (ctrl) return;

        if (alt && e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            vm.MoveSelected(e.Key == VirtualKey.Up ? -1 : 1);
            DispatcherQueue.TryEnqueue(() => FocusSelectedRow(FocusState.Keyboard));
            e.Handled = true;
            return;
        }
        if (alt) return;

        RebaseAction? action = e.Key switch
        {
            VirtualKey.P => RebaseAction.Pick,
            VirtualKey.R => RebaseAction.Reword,
            VirtualKey.E => RebaseAction.Edit,
            VirtualKey.S => RebaseAction.Squash,
            VirtualKey.F => RebaseAction.Fixup,
            VirtualKey.D => RebaseAction.Drop,
            _ => null,
        };
        if (action is not { } chosen) return;
        vm.SetAction(chosen);
        e.Handled = true;
    }

    private void FocusSelectedRow(FocusState state)
    {
        if (_viewModel?.SelectedRow is not { } row) return;
        RowList.SelectedItem = row;
        if (RowList.ContainerFromItem(row) is ListViewItem item) item.Focus(state);
    }

    private void Close() => _viewModel?.CloseCommand.Execute(null);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
