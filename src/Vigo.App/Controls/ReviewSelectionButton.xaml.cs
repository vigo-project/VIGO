using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vigo.App.Controls;

/// <summary>"Commenter" under a selection of the native diff (see the XAML).</summary>
public sealed partial class ReviewSelectionButton : UserControl
{
    public ReviewSelectionButton()
    {
        InitializeComponent();
        var label = EditorReviewLabels.Current.CommentOnSelection;
        LabelText.Text = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ActionButton, label);
    }

    public event EventHandler? Invoked;

    private void OnClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, EventArgs.Empty);
}
