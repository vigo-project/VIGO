using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vigo.App.Controls;

/// <summary>Header of a hunk in the native unified diff, with its staging buttons (see the XAML).</summary>
public sealed partial class DiffHunkHeader : UserControl
{
    public DiffHunkHeader() => InitializeComponent();

    /// <summary>Index of the hunk shown (in the diff document).</summary>
    public int Hunk { get; private set; } = -1;

    /// <summary>A button was clicked: stage / unstage (false), or discard (true).</summary>
    public event EventHandler<bool>? Invoked;

    /// <summary>Shows a hunk; <paramref name="staging"/> null for a read-only diff (no buttons).</summary>
    public void Show(int hunk, string header, EditorStaging? staging)
    {
        Hunk = hunk;
        HeaderText.Text = header;
        ApplyButton.Content = staging?.ApplyLabel;
        ApplyButton.Visibility = staging is null ? Visibility.Collapsed : Visibility.Visible;
        DiscardButton.Content = staging?.DiscardLabel;
        DiscardButton.Visibility = staging?.DiscardLabel is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, false);

    private void OnDiscardClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, true);
}
