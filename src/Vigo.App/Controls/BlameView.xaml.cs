using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.App.Services;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Controls;

/// <summary>
/// Blame of a file (J5.6): the code editor shows the file and the commit column; the selected commit (caret line) is
/// described on the right. Échap goes back to the graph.
/// </summary>
public sealed partial class BlameView : UserControl
{
    private BlameViewModel? _viewModel;
    private int _shownText = -1;
    private int _shownBlame = -1;
    private bool _refreshQueued;

    public BlameView()
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
        Editor.KeyPressed += OnEditorKey;
        Editor.EditorFailed += (_, _) => QueueRefresh();
        Editor.BlameLineSelected += (_, line) => _viewModel?.SelectLine(line);
    }

    /// <summary>Blame shown by the control; set once by the repository view.</summary>
    public BlameViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = value;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _shownText = _shownBlame = -1;
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
            case nameof(BlameViewModel.IsOpen) when _viewModel!.IsOpen:
                // Still collapsed at this point: focus once the visibility binding has run.
                DispatcherQueue.TryEnqueue(Editor.FocusEditor);
                QueueRefresh();
                break;
            case nameof(BlameViewModel.SelectedCommit):
                ShowSelection();
                break;
            default:
                // A batch of blamed lines raises several notifications: one refresh for all of them.
                QueueRefresh();
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
        var text = vm is { IsOpen: true } ? vm.Text : null;
        var message = Editor.Failed && text is not null
            ? Loc.Get("Editor.Unavailable")
            : vm?.ErrorMessage;
        Message.Text = message ?? string.Empty;
        Message.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        Editor.Visibility = text is null || Editor.Failed ? Visibility.Collapsed : Visibility.Visible;

        if (vm is null || text is null)
        {
            if (_shownText != -1) Editor.Clear();
            _shownText = _shownBlame = -1;
        }
        else
        {
            if (vm.TextVersion != _shownText)
            {
                _shownText = vm.TextVersion;
                _shownBlame = -1;
                Editor.ShowBlame(vm.Path ?? string.Empty, text);
            }
            if (vm.BlameVersion != _shownBlame)
            {
                _shownBlame = vm.BlameVersion;
                var commits = vm.Commits.Select(c => new EditorBlameCommit(c.Summary, c.DateText, c.AuthorInitials, c.AuthorColorIndex, c.Age,
                    c.Commit.IsUncommitted ? c.Summary : $"{c.ShortSha} · {c.AuthorName} · {c.FullDateText}\n{c.Summary}")).ToList();
                Editor.SetBlameState(commits, vm.LineCommits, vm.SelectedIndex);
            }
        }
        ShowSelection();
    }

    /// <summary>Card of the selected commit, and its lines highlighted in the page.</summary>
    private void ShowSelection()
    {
        var item = _viewModel?.SelectedCommit;
        NoSelection.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        SelectionCard.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is not null)
        {
            SummaryText.Text = item.Summary;
            AvatarText.Text = item.AuthorInitials;
            Avatar.Background = LaneBrushes.For(item.AuthorColorIndex);
            AuthorText.Text = item.AuthorName;
            DateText.Text = item.FullDateText;
            ShaText.Text = item.Commit.IsUncommitted ? "—" : item.Commit.Sha.ToString();
            AutomationProperties.SetName(SelectionCard, $"{item.Summary}, {item.AuthorName}");
        }
        if (_shownBlame != -1) Editor.SetBlameSelection(_viewModel?.SelectedIndex ?? -1);
    }

    /// <summary>Keys pressed in the editor page: Échap, then the app's shortcuts.</summary>
    private void OnEditorKey(object? sender, EditorKey key)
    {
        if (key.Key == "Escape") Close();
        else if (key.VirtualKey > 0) KeyboardShortcuts.TryInvoke(this, (VirtualKey)key.VirtualKey, key.Modifiers);
    }

    private void Close() => _viewModel?.CloseCommand.Execute(null);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnWorkingTreeClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } vm) await vm.ShowRevisionAsync(null);
    }

    private async void OnHeadClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } vm) await vm.ShowRevisionAsync("HEAD");
    }
}
