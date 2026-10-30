using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;

namespace Vigo.App.Controls;

/// <summary>Card of a discussion thread in the native diff (see the XAML).</summary>
public sealed partial class ReviewThreadCard : UserControl
{
    public ReviewThreadCard(ThreadRow row)
    {
        InitializeComponent();
        Row = row;
        CommentList.ItemsSource = row.Comments;
    }

    public ThreadRow Row { get; }

    /// <summary>The line of the thread is not in the diff (as last shown).</summary>
    public bool OutsideDiff { get; private set; }

    /// <summary>"Afficher" / "Masquer" clicked on a resolved thread.</summary>
    public event EventHandler? Toggled;

    /// <summary>A reaction chosen on a comment: the account's own is added, or removed when already there.</summary>
    public event EventHandler<(long CommentId, ReactionKind Kind)>? ReactionToggled;

    /// <summary>"Résoudre" / "Rouvrir" clicked.</summary>
    public event EventHandler? ResolveClicked;

    /// <summary>"Répondre…" clicked: the editor opens the reply box.</summary>
    public event EventHandler? ReplyOpened;

    /// <summary>
    /// The actions of an account that may write (<see cref="ThreadRow.CanReview"/>): the resolve button, locked while its write
    /// runs, and the reply box (null: the "Répondre…" field that opens it).
    /// </summary>
    public void ShowActions(bool resolving, ReviewMarkdownBox? reply)
    {
        if (!Row.CanReview) return;
        var labels = EditorReviewLabels.Current;
        ResolveButton.Content = Row.IsResolved ? labels.Reopen : labels.Resolve;
        ResolveButton.IsEnabled = !resolving;
        ResolveButton.Visibility = Visibility.Visible;
        ReplyPlaceholder.Content = labels.ReplyPlaceholder;
        ReplyPlaceholder.Visibility = reply is null ? Visibility.Visible : Visibility.Collapsed;
        ReplyHost.Child = reply;
    }

    /// <summary>Draws the thread; <paramref name="outsideDiff"/>: its line is not in the diff (the card is after the last line).</summary>
    public void Show(bool outsideDiff, bool expanded)
    {
        OutsideDiff = outsideDiff;
        var labels = EditorReviewLabels.Current;
        var thread = Row.Thread;
        WhereText.Text = thread.Line is not { } line ? Row.Location
            : thread.StartLine is { } start && start != line ? $"{labels.Lines} {start}–{line}"
            : $"{labels.Line} {line}";
        OutsideText.Text = labels.OutsideDiff;
        OutsideBadge.Visibility = outsideDiff ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = Row.StatusText;
        StatusBadge.Visibility = Row.StatusText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var collapsed = Row.IsResolved && !expanded;
        ToggleButton.Content = collapsed ? labels.Show : labels.Hide;
        ToggleButton.Visibility = Row.IsResolved ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = $"{Row.Comments.Count} {labels.Comments}";
        SummaryText.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        CommentList.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ReplyArea.Visibility = !collapsed && Row.CanReview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleClick(object sender, RoutedEventArgs e) => Toggled?.Invoke(this, EventArgs.Empty);

    private void OnReactionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReactionChip chip) ReactionToggled?.Invoke(this, (chip.CommentId, chip.Kind));
    }

    /// <summary>The picker of the conversation: the reactions in their order, with their name.</summary>
    private void OnAddReactionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ThreadCommentRow row } anchor) return;
        var flyout = new MenuFlyout();
        foreach (var kind in ReactionChip.All)
        {
            var item = new MenuFlyoutItem { Text = $"{ReactionChip.EmojiOf(kind)} {ReactionChip.LabelOf(kind)}" };
            item.Click += (_, _) => ReactionToggled?.Invoke(this, (row.Comment.Id, kind));
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private void OnResolveClick(object sender, RoutedEventArgs e) => ResolveClicked?.Invoke(this, EventArgs.Empty);

    private void OnReplyClick(object sender, RoutedEventArgs e) => ReplyOpened?.Invoke(this, EventArgs.Empty);
}
