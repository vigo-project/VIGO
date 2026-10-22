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
/// Diff of a file: XAML header (path, counts, staging and hunk buttons, layout) over the code editor (ADR-038), which
/// shows the unified diff (staging of hunks and lines) or both whole versions side by side.
/// </summary>
public sealed partial class DiffView : UserControl
{
    private DiffViewModel? _viewModel;
    private bool _refreshQueued;

    // What the editor shows, to send it again only when it changes (a diff can be large).
    private object? _shownContent;
    private bool _shownEditable;
    private string? _shownKey;
    private DiffImages? _shownImages;

    public DiffView()
    {
        InitializeComponent();
        Editor.LineToggled += (_, e) => _viewModel?.ToggleDocumentLine(e.Line, e.Extend);
        Editor.HunkInvoked += (_, e) =>
        {
            if (e.Discard) _viewModel?.DiscardHunkCommand.Execute(e.Hunk);
            else _viewModel?.ApplyHunkCommand.Execute(e.Hunk);
        };
        Editor.KeyPressed += OnEditorKey;
        Editor.EditorFailed += (_, _) => QueueRefresh();
        ImageDiff.ModeChanged += (_, mode) =>
        {
            if (_viewModel is { } vm) vm.ImageMode = mode;
        };

        // Accelerators work wherever the focus is in XAML (e.g. still on the file list of the right panel), but only
        // while the diff is visible: a collapsed element does not handle them. In the editor, see OnEditorKey.
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None, () => _viewModel?.CloseCommand.Execute(null));
        AddAccelerator(VirtualKey.Up, VirtualKeyModifiers.Menu, () => _viewModel?.PreviousHunkCommand.Execute(null));
        AddAccelerator(VirtualKey.Down, VirtualKeyModifiers.Menu, () => _viewModel?.NextHunkCommand.Execute(null));
        KeyboardShortcuts.Add(this, VirtualKey.M, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ToggleLayout);
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

    /// <summary>Diff shown by the control; set once by the repository view.</summary>
    public DiffViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.SelectionChanged -= OnSelectionChanged;
            }
            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                _viewModel.SelectionChanged += OnSelectionChanged;
            }
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
            case nameof(DiffViewModel.CurrentHunk):
                HunkText.Text = _viewModel!.HunkDisplay;
                if (_viewModel.CurrentHunk >= 0) Editor.RevealHunk(_viewModel.CurrentHunk);
                break;
            case nameof(DiffViewModel.IsOpen) when _viewModel!.IsOpen:
                // Still collapsed at this point: focus once the visibility binding has run.
                DispatcherQueue.TryEnqueue(Editor.FocusEditor);
                break;
            default:
                // A diff change raises a dozen notifications: one refresh for all of them.
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

    /// <summary>Header from the view model, then the editor content if it changed.</summary>
    private void Refresh()
    {
        var vm = _viewModel;
        Status.Letter = vm?.StatusLetter ?? string.Empty;
        PathText.Text = vm?.PathDisplay ?? string.Empty;
        SideText.Text = vm?.SideDisplay ?? string.Empty;
        SideBadge.Visibility = SideText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AdditionsText.Text = vm?.AdditionsDisplay ?? string.Empty;
        DeletionsText.Text = vm?.DeletionsDisplay ?? string.Empty;
        HunkText.Text = vm?.HunkDisplay ?? string.Empty;
        LayoutButton.IsSideBySide = vm?.IsSideBySide == true;
        LayoutButton.Command = vm?.ToggleLayoutCommand;
        HunkNavigation.Visibility = vm?.IsSideBySide == true ? Visibility.Collapsed : Visibility.Visible;
        var message = Editor.Failed && vm?.Diff is not null
            ? Loc.Get("Editor.Unavailable")
            : vm?.ErrorMessage ?? vm?.PlaceholderMessage;
        Message.Text = message ?? string.Empty;
        Message.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        var imageDiff = vm is { IsImageDiff: true, ErrorMessage: null };
        LoadingRing.IsActive = vm is { IsLoading: true }
            || vm is { IsSideBySide: true, Sides: null, Diff.IsBinary: false, ErrorMessage: null }
            || vm is { IsImageDiff: true, Images: null, ErrorMessage: null };
        // The image comparison replaces the code editor (and the line / hunk buttons) for an image file.
        LayoutButton.Visibility = imageDiff ? Visibility.Collapsed : Visibility.Visible;
        if (imageDiff)
        {
            HunkNavigation.Visibility = Visibility.Collapsed;
            AdditionsText.Text = DeletionsText.Text = string.Empty;
        }
        UpdateSelectionButtons();
        ShowContent();
        ShowImages(imageDiff ? vm!.Images : null);
    }

    private void ShowImages(DiffImages? images)
    {
        var show = images is not null;
        ImageDiff.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (ReferenceEquals(images, _shownImages)) return;
        _shownImages = images;
        if (_viewModel is { } vm) ImageDiff.Mode = vm.ImageMode;
        _ = ImageDiff.ShowAsync(images);
    }

    private void ShowContent()
    {
        var vm = _viewModel;
        object? content = vm switch
        {
            null or { Diff: null } => null,
            { ErrorMessage: not null } or { PlaceholderMessage: not null } or { IsImageDiff: true } => null,
            { IsSideBySide: true } => vm.Sides,
            _ => vm.Document,
        };
        var editable = vm is { CanEditLines: true, IsSideBySide: false };
        if (ReferenceEquals(content, _shownContent) && editable == _shownEditable) return;

        // Same file and side reread (after staging a hunk, or the file changed on disk): keep the scroll position.
        var key = vm?.File is { } file ? $"{vm.SideDisplay}|{file.Path}" : null;
        var keepScroll = key is not null && key == _shownKey;
        _shownKey = key;
        _shownContent = content;
        _shownEditable = editable;

        switch (content)
        {
            case DiffSides sides:
                Editor.ShowSideBySide(sides);
                break;
            case DiffDocument document:
                Editor.ShowDiff(vm!.File!.Path, document, vm.SelectedDocumentLines, editable ? Staging(vm) : null, keepScroll);
                break;
            default:
                Editor.Clear();
                break;
        }
    }

    private static EditorStaging Staging(DiffViewModel vm)
    {
        var staged = vm.IsStagedSide;
        var keys = Loc.Get(vm.CanDiscardLines
            ? staged ? "Diff.Staging.Keys.UnstageDiscard" : "Diff.Staging.Keys.StageDiscard"
            : staged ? "Diff.Staging.Keys.Unstage" : "Diff.Staging.Keys.Stage");
        return new EditorStaging(
            vm.HunkActionLabel,
            vm.CanDiscardLines ? Loc.Get("Diff.Discard.Confirm") : null,
            Loc.Format(staged ? "Diff.Staging.Hint.Unstage" : "Diff.Staging.Hint.Stage", keys),
            Loc.Get("Diff.Staging.SelectLine"));
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        UpdateSelectionButtons();
        if (_viewModel is { } vm && ReferenceEquals(_shownContent, vm.Document)) Editor.SetSelection(vm.SelectedDocumentLines);
    }

    private void UpdateSelectionButtons()
    {
        var vm = _viewModel;
        var editable = vm is { CanEditLines: true, IsSideBySide: false };
        var show = editable && vm!.HasSelection;
        ApplySelectionButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DiscardSelectionButton.Visibility = show && vm!.CanDiscardLines ? Visibility.Visible : Visibility.Collapsed;
        ApplySelectionText.Text = vm?.SelectionActionLabel ?? string.Empty;
        DiscardSelectionText.Text = vm?.DiscardSelectionLabel ?? string.Empty;

        ApplyHunkButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        DiscardHunkButton.Visibility = editable && vm!.CanDiscardLines ? Visibility.Visible : Visibility.Collapsed;
        var applyHunk = Loc.Get(vm is { IsStagedSide: true } ? "Diff.CurrentHunk.Unstage" : "Diff.CurrentHunk.Stage");
        ApplyHunkIcon.Glyph = vm is { IsStagedSide: true } ? FluentIcons.Subtract : FluentIcons.Add;
        ToolTipService.SetToolTip(ApplyHunkButton, applyHunk + " (S)");
        AutomationProperties.SetName(ApplyHunkButton, applyHunk);
    }

    /// <summary>Keys of the diff pressed in the editor, then the app's shortcuts.</summary>
    private void OnEditorKey(object? sender, EditorKey key)
    {
        var vm = _viewModel;
        switch (key)
        {
            case { Key: "S", Control: false, Alt: false }:
                ApplyCurrent(useSelection: true);
                break;
            case { Key: "Delete", Control: false, Alt: false }:
                DiscardCurrent(useSelection: true);
                break;
            case { Key: "AltUp" }:
                vm?.PreviousHunkCommand.Execute(null);
                break;
            case { Key: "AltDown" }:
                vm?.NextHunkCommand.Execute(null);
                break;
            case { Key: "Escape" }:
                vm?.CloseCommand.Execute(null);
                break;
            case { VirtualKey: > 0 }:
                // The app's shortcuts (Ctrl+P, F5, Ctrl+Z…): XAML never sees keys pressed in the editor page.
                KeyboardShortcuts.TryInvoke(this, (VirtualKey)key.VirtualKey, key.Modifiers);
                break;
        }
    }

    private void ToggleLayout() => _viewModel?.ToggleLayoutCommand.Execute(null);

    private void OnApplyHunkClick(object sender, RoutedEventArgs e) => ApplyCurrent(useSelection: false);

    private void OnDiscardHunkClick(object sender, RoutedEventArgs e) => DiscardCurrent(useSelection: false);

    /// <summary>S: the selected lines if any, else the current hunk.</summary>
    private void ApplyCurrent(bool useSelection)
    {
        if (_viewModel is not { CanEditLines: true, IsSideBySide: false } vm) return;
        if (useSelection && vm.HasSelection) vm.ApplySelectionCommand.Execute(null);
        else vm.ApplyHunkCommand.Execute(Math.Max(0, vm.CurrentHunk));
    }

    /// <summary>Delete: the selected lines if any, else the current hunk (after confirmation).</summary>
    private void DiscardCurrent(bool useSelection)
    {
        if (_viewModel is not { CanDiscardLines: true, IsSideBySide: false } vm) return;
        if (useSelection && vm.HasSelection) vm.DiscardSelectionCommand.Execute(null);
        else vm.DiscardHunkCommand.Execute(Math.Max(0, vm.CurrentHunk));
    }

    private void OnApplySelectionClick(object sender, RoutedEventArgs e) => _viewModel?.ApplySelectionCommand.Execute(null);

    private void OnDiscardSelectionClick(object sender, RoutedEventArgs e) => _viewModel?.DiscardSelectionCommand.Execute(null);

    private void OnCloseClick(object sender, RoutedEventArgs e) => _viewModel?.CloseCommand.Execute(null);

    private void OnPreviousHunkClick(object sender, RoutedEventArgs e) => _viewModel?.PreviousHunkCommand.Execute(null);

    private void OnNextHunkClick(object sender, RoutedEventArgs e) => _viewModel?.NextHunkCommand.Execute(null);

}
