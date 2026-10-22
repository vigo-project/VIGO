using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Controls;

/// <summary>Issue (J6.10a, J6.10b): header, description and conversation, with comments. Échap goes back to the graph.</summary>
public sealed partial class IssueView : UserControl
{
    private const double MaxConversationWidth = 900;

    private IssueViewModel? _viewModel;

    public IssueView()
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
        ActualThemeChanged += (_, _) => UpdateStateBadge();
    }

    /// <summary>Issue shown by the control; set once by the repository view.</summary>
    public IssueViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = value;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Bindings.Update();
            UpdateStateBadge();
        }
    }

    public void Release() => ViewModel = null;

    public static Visibility Inverse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IssueViewModel.Detail) or nameof(IssueViewModel.Issue)) UpdateStateBadge();
    }

    // Colors from DesignTokens: a brush read from the resources cannot be cast under AOT (see CLAUDE.md).
    private void UpdateStateBadge()
    {
        var light = ActualTheme == ElementTheme.Light;
        var text = _viewModel?.StateText ?? string.Empty;
        StateLabel.Text = text;
        StateBadge.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var (back, fore) = _viewModel?.State == IssueState.Closed
            ? (light ? DesignTokens.Light.AccentSoft2 : DesignTokens.Dark.AccentSoft2, light ? DesignTokens.Light.AccentText : DesignTokens.Dark.AccentText)
            : (light ? DesignTokens.Light.SuccessSoft : DesignTokens.Dark.SuccessSoft, light ? DesignTokens.Light.Success : DesignTokens.Dark.Success);
        StateBadge.Background = new SolidColorBrush(back);
        StateLabel.Foreground = new SolidColorBrush(fore);
    }

    // The pages get exactly the width of the view, and the conversation column is centered by hand (see PullRequestView).
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        Header.Width = width;
        Pages.Width = width;
        ConversationScroll.Width = width;
        var column = Math.Min(MaxConversationWidth, width);
        ConversationColumn.Width = column;
        ConversationColumn.Margin = new Thickness(Math.Max(0, (width - column) / 2), 0, 0, 0);
    }

    /// <summary>Reactions are shown as plain chips when the app cannot react (the buttons version is shown otherwise).</summary>
    public static Visibility ReadOnlyReactions(bool hasReactions, bool canReact) => hasReactions && !canReact ? Visibility.Visible : Visibility.Collapsed;

    private static TimelineRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as TimelineRow;

    // A reaction already given: clicking it adds the account's own, or removes it.
    private void OnReactionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReactionChip chip) _ = _viewModel?.ToggleReactionAsync(chip.CommentId, chip.Kind);
    }

    // The picker lists the reactions of the service for the comment the button belongs to.
    private void OnAddReactionClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || sender is not FrameworkElement anchor) return;
        var flyout = new MenuFlyout();
        foreach (var kind in ReactionChip.All)
        {
            var item = new MenuFlyoutItem { Text = $"{ReactionChip.EmojiOf(kind)} {ReactionChip.LabelOf(kind)}" };
            item.Click += (_, _) => _ = _viewModel?.ToggleReactionAsync(row.Entry.Id, kind);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private void OnEditCommentClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _viewModel?.BeginEditCommand.Execute(row);
    }

    private void OnSaveEditClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _viewModel?.SaveEditCommand.Execute(row);
    }

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _viewModel?.CancelEditCommand.Execute(row);
    }

    private void OnDeleteCommentClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _viewModel?.DeleteCommentCommand.Execute(row);
    }

    private void Close() => _viewModel?.CloseCommand.Execute(null);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
