using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.Markdown;

namespace Vigo.App.Controls;

/// <summary>A kept comment or the box of a new one in the native review (see the XAML).</summary>
public sealed partial class ReviewNoteCard : UserControl
{
    private ReviewNoteCard(string where)
    {
        InitializeComponent();
        WhereText.Text = where;
    }

    /// <summary>"Retirer" clicked on a kept comment.</summary>
    public event EventHandler? RemoveClicked;

    /// <summary>A comment kept for the review: its place, "En attente", its text and "Retirer".</summary>
    public static ReviewNoteCard Draft(string where, string body, MarkdownReferences? references)
    {
        var labels = EditorReviewLabels.Current;
        var card = new ReviewNoteCard(where);
        card.Frame.Visibility = Visibility.Collapsed;
        card.DashedFrame.Visibility = Visibility.Visible;
        card.PendingText.Text = labels.Pending;
        card.PendingBadge.Visibility = Visibility.Visible;
        card.RemoveButton.Content = labels.RemoveDraft;
        card.RemoveButton.Visibility = Visibility.Visible;
        card.BodyView.Blocks = MarkdownParser.Parse(body, references);
        card.BodyView.Visibility = Visibility.Visible;
        return card;
    }

    /// <summary>The box of a new comment, under "Commentaire sur …".</summary>
    public static ReviewNoteCard Composer(string where, ReviewMarkdownBox box)
    {
        var card = new ReviewNoteCard(where);
        card.BoxHost.Child = box;
        return card;
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e) => RemoveClicked?.Invoke(this, EventArgs.Empty);
}
