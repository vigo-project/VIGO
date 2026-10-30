using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.App.Services;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Controls;

/// <summary>
/// Conflict editor of a file (J5.4): XAML header over the code editor (panes Ours / Theirs, editable result), or a
/// card choosing a whole version, or a waiting panel while an external merge tool is open.
/// </summary>
public sealed partial class ConflictView : UserControl
{
    private static readonly EditorConflictLabels Labels = new(
        OursTitle: Loc.Get("Conflict.Editor.Ours"),
        OursDetail: string.Empty,
        TheirsTitle: Loc.Get("Conflict.Editor.Theirs"),
        TheirsDetail: string.Empty,
        TakeAll: Loc.Get("Conflict.Editor.TakeAll"),
        ResultTitle: Loc.Get("Conflict.Editor.Result"),
        ResultDetail: string.Empty,
        LegendOurs: Loc.Get("Conflict.Editor.LegendOurs"),
        LegendTheirs: Loc.Get("Conflict.Editor.LegendTheirs"),
        Take: Loc.Get("Conflict.Editor.TakeBlock"),
        Ours: Loc.Get("Conflict.Editor.TakeOurs"),
        Theirs: Loc.Get("Conflict.Editor.TakeTheirs"),
        Both: Loc.Get("Conflict.Editor.Both"),
        ToggleTip: Loc.Get("Conflict.Editor.ToggleLine"));

    private ConflictEditorViewModel? _viewModel;
    private bool _refreshQueued;
    private int _shownVersion = -1;
    private readonly SortedSet<int> _changedBlocks = [];
    private bool _stateQueued;
    private bool _revealQueued;

    public ConflictView()
    {
        InitializeComponent();
        Editor.ConflictLineToggled += (_, e) => _viewModel?.ToggleLine(e.Block, e.Side, e.Line);
        Editor.ConflictTaken += (_, e) => _viewModel?.TakeBlock(e.Block, e.Take);
        Editor.ConflictAllTaken += (_, side) => _viewModel?.TakeAll(side);
        Editor.TextChanged += (_, _) => _viewModel?.MarkEdited();
        Editor.KeyPressed += OnEditorKey;
        Editor.EditorFailed += (_, _) => QueueRefresh();

        // Wherever the focus is in XAML while the editor is shown; in the page, see OnEditorKey.
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, Close);
        AddAccelerator(VirtualKey.Up, VirtualKeyModifiers.Menu, () => _viewModel?.PreviousBlockCommand.Execute(null));
        AddAccelerator(VirtualKey.Down, VirtualKeyModifiers.Menu, () => _viewModel?.NextBlockCommand.Execute(null));
        KeyboardShortcuts.Add(this, VirtualKey.S, VirtualKeyModifiers.Control, () =>
        {
            _ = ResolveAsync();
            return true;
        });
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>Conflict editor shown by the control; set once by the repository view.</summary>
    public ConflictEditorViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.BlockChanged -= OnBlockChanged;
            }
            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                _viewModel.BlockChanged += OnBlockChanged;
            }
            _shownVersion = -1;
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
            case nameof(ConflictEditorViewModel.CurrentBlock):
                _revealQueued = true;
                QueueState();
                QueueRefresh();
                break;
            case nameof(ConflictEditorViewModel.IsOpen) when _viewModel!.IsOpen:
                // Still collapsed at this point: focus once the visibility binding has run.
                DispatcherQueue.TryEnqueue(Editor.FocusEditor);
                QueueRefresh();
                break;
            default:
                // A change raises a dozen notifications: one refresh for all of them.
                QueueRefresh();
                break;
        }
    }

    private void OnBlockChanged(object? sender, int block)
    {
        _changedBlocks.Add(block);
        QueueState();
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

    /// <summary>Sends the state of the conflicts once per dispatcher pass (a "Tout prendre" changes every conflict).</summary>
    private void QueueState()
    {
        if (_stateQueued) return;
        _stateQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _stateQueued = false;
            var vm = _viewModel;
            var reveal = _revealQueued;
            _revealQueued = false;
            int[] changed = [.. _changedBlocks];
            _changedBlocks.Clear();
            // The whole content is about to be sent again: it carries the state.
            if (vm is not { IsText: true } || vm.Version != _shownVersion) return;
            Editor.SetConflictState(Blocks(vm), vm.CurrentBlock, reveal, changed);
        });
    }

    private void Refresh()
    {
        var vm = _viewModel;
        TitleText.Text = vm?.Title ?? string.Empty;
        SubtitleText.Text = vm?.Subtitle ?? string.Empty;
        var text = vm is { IsText: true, IsLoading: false };
        RemainingText.Text = vm is { AllResolved: false } ? vm.RemainingText : string.Empty;
        ResolvedText.Text = vm is { AllResolved: true } ? vm.RemainingText : string.Empty;
        PositionText.Text = vm?.PositionText ?? string.Empty;
        Navigation.Visibility = Visible(text && vm!.Choices.Count > 0);
        ResetButton.Visibility = Visible(text);
        ResolveButton.Visibility = Visible(text);
        ResolveButton.IsEnabled = vm is { CanMarkResolved: true };
        AbortButton.Visibility = Visible(vm is { CanAbort: true });
        AbortText.Text = vm?.AbortLabel ?? string.Empty;
        AutomationProperties.SetName(AbortButton, AbortText.Text);
        ToolButton.Visibility = Visible(vm is { HasExternalTool: true, IsToolRunning: false });
        ToolTipService.SetToolTip(ToolButton, vm is { HasExternalTool: true } ? Loc.Format("Conflict.OpenInTool", vm.ExternalToolName) : null);

        WholeFileCard.Visibility = Visible(vm is { IsWholeFile: true, IsToolRunning: false });
        WholeFileTitle.Text = vm?.WholeFileTitle ?? string.Empty;
        WholeFileMessage.Text = vm?.WholeFileMessage ?? string.Empty;
        KeepOursText.Text = vm?.KeepOursLabel ?? string.Empty;
        KeepTheirsText.Text = vm?.KeepTheirsLabel ?? string.Empty;
        AutomationProperties.SetName(KeepOursButton, KeepOursText.Text);
        AutomationProperties.SetName(KeepTheirsButton, KeepTheirsText.Text);
        DeleteFileButton.Visibility = Visible(vm is { CanDeleteFile: true });

        ToolPanel.Visibility = Visible(vm is { IsToolRunning: true });
        ToolRing.IsActive = vm is { IsToolRunning: true };
        ToolText.Text = vm is { IsToolRunning: true }
            ? Loc.Format("Conflict.ToolWaiting", vm.Path, vm.ExternalToolName)
            : string.Empty;

        LoadingRing.IsActive = vm is { IsLoading: true };
        var message = Editor.Failed && text
            ? Loc.Get("Editor.Unavailable")
            : vm?.ErrorMessage;
        Message.Text = message ?? string.Empty;
        Message.Visibility = Visible(message is not null);
        Editor.Visibility = Visible(text && !vm!.IsToolRunning);
        ShowContent();
    }

    private void ShowContent()
    {
        var vm = _viewModel;
        if (vm is not { IsOpen: true })
        {
            if (_shownVersion != -1) Editor.Clear();
            _shownVersion = -1;
            return;
        }
        if (vm.Version == _shownVersion) return;
        _shownVersion = vm.Version;
        if (vm is { IsText: true, File: { } file })
        {
            var (oursDetail, theirsDetail) = (vm.OursDetail, vm.TheirsDetail);
            var labels = Labels with
            {
                OursDetail = oursDetail,
                TheirsDetail = theirsDetail,
                ResultDetail = Loc.Format("Conflict.Editor.ResultDetail", System.IO.Path.GetFileName(file.Path)),
            };
            _changedBlocks.Clear();
            Editor.ShowConflict(file.Path, labels, file, Blocks(vm), vm.CurrentBlock);
        }
        else
        {
            Editor.Clear();
        }
    }

    private static List<EditorConflictBlock> Blocks(ConflictEditorViewModel vm)
    {
        var blocks = new List<EditorConflictBlock>(vm.Choices.Count);
        for (var i = 0; i < vm.Choices.Count; i++)
        {
            var choice = vm.Choices[i];
            var hunk = choice.Hunk;
            blocks.Add(new EditorConflictBlock(
                vm.BlockLabel(i, ConflictSide.Ours),
                vm.BlockLabel(i, ConflictSide.Theirs),
                ConflictEditorViewModel.UnresolvedLabel(i),
                choice.IsResolved,
                Enumerable.Range(0, hunk.Ours.Count).Select(l => choice.IsKept(ConflictSide.Ours, l)).ToArray(),
                Enumerable.Range(0, hunk.Theirs.Count).Select(l => choice.IsKept(ConflictSide.Theirs, l)).ToArray(),
                choice.ResultLines));
        }
        return blocks;
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Keys of the conflict editor pressed in the page, then the app's shortcuts.</summary>
    private void OnEditorKey(object? sender, EditorKey key)
    {
        switch (key)
        {
            case { Key: "AltUp" }:
                _viewModel?.PreviousBlockCommand.Execute(null);
                break;
            case { Key: "AltDown" }:
                _viewModel?.NextBlockCommand.Execute(null);
                break;
            case { Key: "Escape" }:
                Close();
                break;
            case { VirtualKey: > 0 }:
                // The app's shortcuts (Ctrl+S, Ctrl+P, F5…): XAML never sees keys pressed in the editor page.
                KeyboardShortcuts.TryInvoke(this, (VirtualKey)key.VirtualKey, key.Modifiers);
                break;
        }
    }

    private void Close() => _viewModel?.CloseCommand.Execute(null);

    /// <summary>"Marquer comme résolu": the text of the result pane is written and staged.</summary>
    private async Task ResolveAsync()
    {
        if (_viewModel is not { CanMarkResolved: true } vm) return;
        if (await Editor.GetConflictResultAsync() is { } text) await vm.MarkResolvedAsync(text);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _viewModel?.PreviousBlockCommand.Execute(null);

    private void OnNextClick(object sender, RoutedEventArgs e) => _viewModel?.NextBlockCommand.Execute(null);

    private void OnResetClick(object sender, RoutedEventArgs e) => _viewModel?.ResetCommand.Execute(null);

    /// <summary>"Annuler la fusion": the banner's command (back to the state before the operation).</summary>
    public event EventHandler? AbortRequested;

    private void OnAbortClick(object sender, RoutedEventArgs e) => AbortRequested?.Invoke(this, EventArgs.Empty);

    private async void OnResolveClick(object sender, RoutedEventArgs e) => await ResolveAsync();

    private void OnToolClick(object sender, RoutedEventArgs e) => _viewModel?.OpenInExternalToolCommand.Execute(null);

    private void OnCancelToolClick(object sender, RoutedEventArgs e) => _viewModel?.CancelToolCommand.Execute(null);

    private void OnKeepOursClick(object sender, RoutedEventArgs e) => _viewModel?.KeepSideCommand.Execute(ConflictSide.Ours);

    private void OnKeepTheirsClick(object sender, RoutedEventArgs e) => _viewModel?.KeepSideCommand.Execute(ConflictSide.Theirs);

    private void OnDeleteFileClick(object sender, RoutedEventArgs e) => _viewModel?.DeleteFileCommand.Execute(null);
}
