using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Views;

/// <summary>
/// Overlay of the command palette. A new <see cref="CommandPaletteViewModel"/> is given at each opening (the entries
/// are read then); the overlay hides itself when an entry is picked, on Échap or on a click outside.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private CommandPaletteViewModel? _viewModel;
    private UIElement? _focusBack;

    public CommandPalette()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    // Placeholder of the XAML (prefixes of the search), shown again after a picker.
    private string? DefaultPlaceholder { get; set; }

    /// <summary>Shows the palette over the window, with <paramref name="viewModel"/>'s query typed in the box.</summary>
    public void Open(CommandPaletteViewModel viewModel)
    {
        Detach();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Closed += OnViewModelClosed;
        _focusBack = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as UIElement : null;

        Results.ItemsSource = viewModel.Items;
        DefaultPlaceholder ??= QueryBox.PlaceholderText;
        QueryBox.PlaceholderText = viewModel.Placeholder ?? DefaultPlaceholder;
        QueryBox.Text = viewModel.Query;
        QueryBox.SelectionStart = QueryBox.Text.Length;
        UpdateSelection();
        UpdateNoResult();
        Visibility = Visibility.Visible;
        QueryBox.Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        if (!IsOpen) return;
        Detach();
        Visibility = Visibility.Collapsed;
        Results.ItemsSource = null;
        // Back to where the keyboard was (graph, file list…), when it is still in the tree.
        if (_focusBack is { XamlRoot: not null } back) back.Focus(FocusState.Programmatic);
        _focusBack = null;
    }

    private void Detach()
    {
        if (_viewModel is null) return;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Closed -= OnViewModelClosed;
        _viewModel = null;
    }

    private void OnViewModelClosed(object? sender, EventArgs e) => Close();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CommandPaletteViewModel.SelectedIndex):
                UpdateSelection();
                break;
            case nameof(CommandPaletteViewModel.HasNoResult) or nameof(CommandPaletteViewModel.NoResultText):
                UpdateNoResult();
                break;
            case nameof(CommandPaletteViewModel.Placeholder):
                QueryBox.PlaceholderText = _viewModel?.Placeholder ?? DefaultPlaceholder;
                break;
            case nameof(CommandPaletteViewModel.Query) when _viewModel is { } vm && QueryBox.Text != vm.Query:
                // Emptied by the model: second step of an entry (a file to pick, J5.6).
                QueryBox.Text = vm.Query;
                break;
        }
    }

    private void UpdateSelection()
    {
        if (_viewModel is null) return;
        var index = _viewModel.SelectedIndex;
        if (Results.SelectedIndex != index) Results.SelectedIndex = index;
        if (index >= 0 && index < _viewModel.Items.Count) Results.ScrollIntoView(_viewModel.Items[index]);
    }

    private void UpdateNoResult()
    {
        if (_viewModel is null) return;
        NoResult.Text = _viewModel.NoResultText;
        NoResult.Visibility = _viewModel.HasNoResult ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = _viewModel.HasNoResult ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null && _viewModel.Query != QueryBox.Text) _viewModel.Query = QueryBox.Text;
        // The first row is highlighted again even when the index did not change (no notification then).
        UpdateSelection();
    }

    private async void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_viewModel is not { } viewModel) return;
        switch (e.Key)
        {
            case VirtualKey.Down:
                viewModel.MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                viewModel.MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                await viewModel.RunSelectedAsync();
                break;
        }
    }

    // The list never takes the keyboard: the search box keeps it, the selection follows ↑ / ↓ and the pointer.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is not null && Results.SelectedIndex >= 0) _viewModel.SelectedIndex = Results.SelectedIndex;
    }

    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (_viewModel is not null && e.ClickedItem is PaletteItem item) await _viewModel.RunAsync(item);
    }

    private void OnDimmerTapped(object sender, TappedRoutedEventArgs e) => Close();
}
