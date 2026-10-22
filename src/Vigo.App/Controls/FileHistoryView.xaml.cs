using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.App.Services;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Controls;

/// <summary>
/// History of a file (J5.6): list of its commits, read-only diff of the selected one (or of two selected versions) in
/// the code editor, unified or side by side (Ctrl+Maj+M). Échap goes back to the graph.
/// </summary>
public sealed partial class FileHistoryView : UserControl
{
    private FileHistoryViewModel? _viewModel;
    private bool _refreshQueued;
    private bool _syncingSelection;

    // What the editor shows (FileDiff or DiffSides), to send it again only when it changes.
    private object? _shown;

    public FileHistoryView()
    {
        InitializeComponent();
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            Close();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        KeyboardShortcuts.Add(this, VirtualKey.M, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => _viewModel?.ToggleLayoutCommand.Execute(null));
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Editor.KeyPressed += OnEditorKey;
        Editor.EditorFailed += (_, _) => QueueRefresh();
    }

    /// <summary>File history shown by the control; set once by the repository view.</summary>
    public FileHistoryViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = value;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _shown = null;
            Bindings.Update();
            QueueRefresh();
        }
    }

    /// <summary>Closes the editor page when the tab is closed for good.</summary>
    public void Release()
    {
        ViewModel = null;
        Editor.Release();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FileHistoryViewModel.IsOpen) when _viewModel!.IsOpen:
                // Still collapsed at this point: the keyboard goes to the list once the visibility binding has run.
                DispatcherQueue.TryEnqueue(() => HistoryList.Focus(FocusState.Programmatic));
                QueueRefresh();
                break;
            case nameof(FileHistoryViewModel.Diff) or nameof(FileHistoryViewModel.DiffMessage) or nameof(FileHistoryViewModel.IsOpen)
                or nameof(FileHistoryViewModel.Sides) or nameof(FileHistoryViewModel.Layout):
                QueueRefresh();
                break;
            case nameof(FileHistoryViewModel.SelectedEntry):
                // The list follows the model (first commit selected when the history opens); a selection made in the
                // list (several commits compared) is left as it is.
                if (_viewModel!.SelectedEntry is { } row && !HistoryList.SelectedItems.Contains(row))
                {
                    _syncingSelection = true;
                    HistoryList.SelectedItems.Clear();
                    HistoryList.SelectedItems.Add(row);
                    _syncingSelection = false;
                    HistoryList.ScrollIntoView(row);
                }
                break;
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        var vm = _viewModel;
        var diff = vm is { IsOpen: true } ? vm.Diff : null;
        var message = Editor.Failed && diff is not null
            ? Loc.Get("Editor.Unavailable")
            : vm?.DiffMessage;
        DiffMessage.Text = message ?? string.Empty;
        DiffMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        // Side by side: both whole versions (read with the diff; the unified diff until they are there).
        object? content = message is not null || diff is null ? null : vm!.IsSideBySide ? (object?)vm.Sides ?? diff : diff;
        Editor.Visibility = content is null || Editor.Failed ? Visibility.Collapsed : Visibility.Visible;
        if (ReferenceEquals(content, _shown)) return;
        _shown = content;
        switch (content)
        {
            case DiffSides sides:
                Editor.ShowSideBySide(sides);
                break;
            case FileDiff shown:
                Editor.ShowDiff(shown.Path, DiffDocument.Build(shown), [], staging: null, keepScroll: false);
                break;
            default:
                Editor.Clear();
                break;
        }
    }

    /// <summary>Keys pressed in the editor page: Échap, then the app's shortcuts.</summary>
    private void OnEditorKey(object? sender, EditorKey key)
    {
        if (key.Key == "Escape") Close();
        else if (key.VirtualKey > 0) KeyboardShortcuts.TryInvoke(this, (VirtualKey)key.VirtualKey, key.Modifiers);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nothing selected: the list was cleared (history reloaded), the model chooses again.
        if (_viewModel is null || _syncingSelection || HistoryList.SelectedItems.Count == 0) return;
        _viewModel.SelectEntries(HistoryList.SelectedItems.OfType<FileHistoryRow>().ToList());
    }

    private void Close() => _viewModel?.CloseCommand.Execute(null);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
