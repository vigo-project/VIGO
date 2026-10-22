using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.App.Controls;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Vigo.App.Views;

public sealed partial class CommitDetailsPanel : UserControl
{
    private double _dragStartHeight;

    public CommitDetailsPanel()
    {
        InitializeComponent();
        MessageSplitter.DragStarted += (_, _) => _dragStartHeight = BodyScroller.ActualHeight;
        // Never taller than the description itself: the grip stays under the last line.
        MessageSplitter.DragDelta += (_, delta) =>
        {
            if (Layout is null) return;
            var content = Math.Max(BodyScroller.ExtentHeight, LayoutLimits.CommitMessage.Minimum);
            Layout.CommitMessageHeight = Math.Min(_dragStartHeight + delta, content);
        };
        MessageSplitter.DragCompleted += (_, _) => _ = Layout?.SaveAsync();
        MessageSplitter.ResetRequested += (_, _) =>
        {
            Layout?.CommitMessageHeight = LayoutLimits.CommitMessage.Default;
            _ = Layout?.SaveAsync();
        };
        // Subscribed while shown only: the layout outlives the tabs.
        Loaded += (_, _) =>
        {
            Layout?.PropertyChanged += OnLayoutPropertyChanged;
            ApplyMessageHeight();
        };
        Unloaded += (_, _) => Layout?.PropertyChanged -= OnLayoutPropertyChanged;
    }

    /// <summary>Set once by the repository view, right after its InitializeComponent.</summary>
    public CommitDetailsViewModel ViewModel
    {
        get;
        set
        {
            field = value;
            // The x:Bind bindings were evaluated with a null view model during InitializeComponent.
            Bindings.Update();
        }
    } = null!;

    /// <summary>Shared layout (description height, Path / Tree mode); set once by the repository view, before loading.</summary>
    public LayoutState? Layout
    {
        get;
        set
        {
            field = value;
            FilesModeSwitch.Layout = value;
        }
    }

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIf(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Padding of a file or folder row, indented by its level in tree mode.</summary>
    public static Thickness RowPadding(double indent) => new(6 + indent, 0, 6, 0);

    public static string BadgeIcon(RefBadgeKind kind) => kind switch
    {
        RefBadgeKind.Head => FluentIcons.Checkmark,
        RefBadgeKind.Branch => FluentIcons.Desktop,
        RefBadgeKind.Remote => FluentIcons.Cloud,
        _ => FluentIcons.Tag,
    };

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutState.CommitMessageHeight)) ApplyMessageHeight();
    }

    private void ApplyMessageHeight() => BodyScroller.MaxHeight = Layout?.CommitMessageHeight ?? LayoutLimits.CommitMessage.Default;

    private void OnCopyShaClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.FullSha.Length == 0) return;
        var package = new DataPackage();
        package.SetText(ViewModel.FullSha);
        Clipboard.SetContent(package);
    }

    private void OnParentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ParentLink parent)
            ViewModel.SelectParentCommand.Execute(parent);
    }

    private void OnFileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ChangedFileItem item) return;
        // The files of a stash can be selected together (Ctrl, Maj); such a click only changes the selection.
        if (ViewModel.SelectFile(item, IsDown(VirtualKey.Control), IsDown(VirtualKey.Shift))) return;
        ViewModel.OpenFileCommand.Execute(item);
    }

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // The first item of a file row's menu applies the selected files of a stash; shown for a stash only.
    private void OnRowMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout { Items: [MenuFlyoutItem { Tag: ChangedFileItem item } apply, ..] }) return;
        ViewModel.EnsureSelected(item);
        apply.Visibility = ViewModel.IsStash ? Visibility.Visible : Visibility.Collapsed;
        apply.Text = ViewModel.ApplySelectionText;
    }

    private void OnApplyFilesClick(object sender, RoutedEventArgs e) => ViewModel.ApplySelection();

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChangedFileItem item) ViewModel.Inspect(item, FileInspectionKind.History);
    }

    private void OnBlameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChangedFileItem item) ViewModel.Inspect(item, FileInspectionKind.Blame);
    }

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FolderRow folder)
            ViewModel.ToggleFolderCommand.Execute(folder);
    }
}
