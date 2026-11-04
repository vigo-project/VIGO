using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Vigo.App.Controls;

/// <summary>Stage / discard buttons of a block of the side-by-side diff (see the XAML).</summary>
public sealed partial class DiffBlockBar : UserControl
{
    public DiffBlockBar() => InitializeComponent();

    /// <summary>A button was clicked: discard (else stage / unstage).</summary>
    public event EventHandler<bool>? Invoked;

    /// <summary>Sets the labels; no discard button when <paramref name="discardLabel"/> is null.</summary>
    public void SetLabels(string applyLabel, string? discardLabel)
    {
        ApplyText.Text = applyLabel;
        AutomationProperties.SetName(ApplyButton, applyLabel);
        DiscardButton.Visibility = discardLabel is null ? Visibility.Collapsed : Visibility.Visible;
        DiscardText.Text = discardLabel ?? string.Empty;
        if (discardLabel is not null) AutomationProperties.SetName(DiscardButton, discardLabel);
    }

    private void OnApplyClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, false);

    private void OnDiscardClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, true);
}
