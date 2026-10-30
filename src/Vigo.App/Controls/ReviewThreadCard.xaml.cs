using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.ViewModels;

namespace Vigo.App.Controls;

/// <summary>Read-only card of a discussion thread in the native diff (see the XAML).</summary>
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
    }

    private void OnToggleClick(object sender, RoutedEventArgs e) => Toggled?.Invoke(this, EventArgs.Empty);
}
