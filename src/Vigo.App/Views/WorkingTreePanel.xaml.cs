using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.App.Controls;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Vigo.App.Views;

public sealed partial class WorkingTreePanel : UserControl
{
    public WorkingTreePanel() => InitializeComponent();

    /// <summary>Set once by the repository view, right after its InitializeComponent.</summary>
    public WorkingTreeViewModel ViewModel
    {
        get;
        set
        {
            field = value;
            // The x:Bind bindings were evaluated with a null view model during InitializeComponent.
            Bindings.Update();
            value.CommitForm.PropertyChanged += OnCommitFormPropertyChanged;
            UpdateSummaryLengthState();
        }
    } = null!;

    /// <summary>Merge and rebase of the tab (banner of an operation stopped on conflicts); set once by the repository view.</summary>
    public MergeActionsViewModel? Merge
    {
        get;
        set
        {
            field = value;
            Bindings.Update();
        }
    }

    /// <summary>Conflict editor of the tab ("Ouvrir l'éditeur de conflits" of the banner); set once by the repository view.</summary>
    public ConflictEditorViewModel? Conflicts
    {
        get;
        set
        {
            field = value;
            Bindings.Update();
        }
    }

    /// <summary>Shared layout (Path / Tree mode); set once by the repository view, before loading.</summary>
    public LayoutState? Layout
    {
        get;
        set
        {
            field = value;
            FilesModeSwitch.Layout = value;
        }
    }

    /// <summary>Stash of the tab (header button and its menu); set once by the repository view.</summary>
    public StashActionsViewModel? Stash
    {
        get;
        set
        {
            field = value;
            Bindings.Update();
        }
    }

    /// <summary>Patch files of the tab (J7.12: menu of the stash button and of the file rows); set once by the repository view.</summary>
    public PatchActionsViewModel? Patches { get; set; }

    private void OnStashMenuOpening(object? sender, object e)
    {
        PatchAllItem.IsEnabled = PatchStagedItem.IsEnabled = Patches?.CanCreateFromWorkingTree == true;
        PatchApplyItem.IsEnabled = Patches?.CanApply == true;
    }

    private void OnPatchAllClick(object sender, RoutedEventArgs e) => Patches?.CreateWorkingTreePatchCommand.Execute(PatchScope.All);

    private void OnPatchStagedClick(object sender, RoutedEventArgs e) => Patches?.CreateWorkingTreePatchCommand.Execute(PatchScope.Staged);

    private void OnPatchApplyClick(object sender, RoutedEventArgs e) => Patches?.ApplyPatchCommand.Execute(null);

    /// <summary>"Créer un patch de ce fichier…": the side of the row (staged or not) says which changes.</summary>
    private void OnFilePatchClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            Patches?.CreateFilePatchCommand.Execute(new FilePatchRequest(item.Path, item.Side == WorkingDiffSide.Staged ? PatchScope.Staged : PatchScope.Unstaged));
    }

    private void OnCommitFormPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommitFormViewModel.SummaryLengthLevel)) UpdateSummaryLengthState();
    }

    private void UpdateSummaryLengthState() =>
        VisualStateManager.GoToState(this, ViewModel.CommitForm.SummaryLengthLevel switch
        {
            SummaryLengthLevel.Danger => "SummaryDanger",
            SummaryLengthLevel.Warning => "SummaryWarning",
            _ => "SummaryNormal",
        }, true);

    /// <summary>
    /// Ctrl+Entrée commits, Ctrl+Maj+Entrée commits then pushes, also from the multi-line description (which would
    /// insert a line break).
    /// </summary>
    private void OnCommitFormPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (!ctrl) return;

        e.Handled = true;
        var form = ViewModel.CommitForm;
        System.Windows.Input.ICommand command = shift ? form.CommitAndPushCommand : form.CommitCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIf(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Padding of a file row, indented by its level in tree mode; room on the right for the stage button.</summary>
    public static Thickness RowPadding(double indent) => new(10 + indent, 0, 36, 0);

    /// <summary>Padding of a folder row of the tree, its chevron aligned on the change icons of the files.</summary>
    public static Thickness FolderPadding(double indent) => new(10 + indent, 0, 8, 0);

    // The stage / unstage button of a file row shows on hover (or keyboard focus) only; it is the last child of the row grid.
    private static void ShowRowToggle(object sender, bool show)
    {
        if (sender is Grid { Children: [.., Button toggle] }) toggle.Opacity = show ? 1 : 0;
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) => ShowRowToggle(sender, true);

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e) => ShowRowToggle(sender, false);

    private void OnRowFocusChanged(object sender, RoutedEventArgs e) =>
        ShowRowToggle(sender, sender is Grid { Children: [.., Button toggle] } && toggle.FocusState != FocusState.Unfocused);

    private static WorkingDiffSide SideOf(FileGroup group) => group == FileGroup.Staged ? WorkingDiffSide.Staged : WorkingDiffSide.Unstaged;

    public static string FolderToggleGlyph(FileGroup group) => ToggleGlyph(SideOf(group), group == FileGroup.Conflicted);

    public static string FolderToggleText(FileGroup group) => ToggleText(SideOf(group), group == FileGroup.Conflicted);

    /// <summary>Selection mode of a click: Ctrl adds or removes the row, Shift selects up to it, no key replaces.</summary>
    private static FileSelectionMode ModeOfKeys()
    {
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)) return FileSelectionMode.Range;
        return InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) ? FileSelectionMode.Toggle : FileSelectionMode.Replace;
    }

    /// <summary>The first entry of a row menu says how many files it acts on when the row is part of a selection.</summary>
    private void OnRowMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout { Items: [MenuFlyoutItem { Tag: WorkingFileItem item } toggle, ..] }) return;
        var count = ViewModel.SelectionSizeOf(item);
        toggle.Text = count > 1
            ? Loc.Format(item.IsConflicted ? "WorkingTree.Toggle.ResolveMany" : item.Side == WorkingDiffSide.Staged ? "WorkingTree.Toggle.UnstageMany" : "WorkingTree.Toggle.StageMany", count)
            : ToggleText(item.Side, item.IsConflicted);
    }

    /// <summary>Menu of a folder of the tree (right click): menu items carry the folder in their Tag.</summary>
    private static FolderRow? FolderOf(object sender) => (sender as FrameworkElement)?.Tag as FolderRow;

    /// <summary>Discard and ignore concern the changes still to be made: the unstaged group.</summary>
    public static Visibility IsUnstagedGroup(FileGroup group) => group == FileGroup.Unstaged ? Visibility.Visible : Visibility.Collapsed;

    public static string FolderIgnoreText(string path) => Loc.Format("WorkingTree.Folder.Ignore", path + "/");

    /// <summary>Folders and directory patches by the side of the group: staged changes for the staged group, else the unstaged ones.</summary>
    private static PatchScope ScopeOf(FileGroup group) => group == FileGroup.Staged ? PatchScope.Staged : PatchScope.Unstaged;

    /// <summary>Folders the app can open: the working directory of a repository tab.</summary>
    public FolderActionsViewModel? Folders { get; set; }

    private void OnFolderToggleClick(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) ViewModel.ToggleFolderStageCommand.Execute(folder);
    }

    private void OnFolderDiscardClick(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Folders?.DiscardFolderCommand.Execute(folder);
    }

    private void OnFolderIgnoreClick(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Folders?.IgnoreFolderCommand.Execute(folder);
    }

    private void OnFolderStashClick(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Stash?.StashFolderCommand.Execute(folder);
    }

    private void OnFolderPatchClick(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Patches?.CreateFilePatchCommand.Execute(new FilePatchRequest(folder.Path, ScopeOf(folder.Group)));
    }

    private void OnFolderNewFileClick(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Folders?.CreateFileCommand.Execute(folder);
    }

    private void OnFolderOpenClick(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is not { } folder || ViewModel.FullPath(folder) is not { } path) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
    }

    /// <summary>Stage (+) on unstaged rows, unstage (−) on staged rows, mark resolved (✓) on conflicts (mockup).</summary>
    public static string ToggleGlyph(WorkingDiffSide side, bool conflicted) =>
        conflicted ? FluentIcons.Checkmark : side == WorkingDiffSide.Staged ? FluentIcons.Subtract : FluentIcons.Add;

    public static string ToggleText(WorkingDiffSide side, bool conflicted) =>
        Loc.Get(conflicted ? "WorkingTree.Toggle.Resolve" : side == WorkingDiffSide.Staged ? "WorkingTree.Toggle.Unstage" : "WorkingTree.Toggle.Stage");

    public static string ToggleAutomationName(string path, WorkingDiffSide side, bool conflicted) =>
        Loc.Format("WorkingTree.Toggle.Automation", ToggleText(side, conflicted), path);

    /// <summary>An icon with an explicit color is not greyed out by a disabled button: dim it instead.</summary>
    public static double EnabledOpacity(bool enabled) => enabled ? 1 : 0.4;

    public static Visibility CanDiscard(WorkingDiffSide side, bool conflicted) =>
        side == WorkingDiffSide.Unstaged && !conflicted ? Visibility.Visible : Visibility.Collapsed;

    private static WorkingFileItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as WorkingFileItem;

    private void OnFileClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        // Ctrl / Maj + clic only selects; a plain click selects the file alone and shows its diff.
        var mode = ModeOfKeys();
        ViewModel.Select(item, mode);
        if (mode == FileSelectionMode.Replace) ViewModel.OpenFileCommand.Execute(item);
    }

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FolderRow folder) return;
        var mode = ModeOfKeys();
        if (mode == FileSelectionMode.Replace) ViewModel.ToggleFolderCommand.Execute(folder);
        else ViewModel.Select(folder, mode);
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.ToggleStageCommand.Execute(item);
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.DiscardCommand.Execute(item);
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Inspect(item, FileInspectionKind.History);
    }

    private void OnBlameClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Inspect(item, FileInspectionKind.Blame);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var package = new DataPackage();
        package.SetText(item.Path);
        Clipboard.SetContent(package);
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item || ViewModel.FullPath(item) is not { } path) return;
        // A deleted file cannot be selected: open its folder instead.
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = false });
    }
}
