using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Vigo.App.Controls;

/// <summary>Header of a pane of the native conflict editor (see the XAML).</summary>
public sealed partial class ConflictPaneHeader : UserControl
{
    public ConflictPaneHeader() => InitializeComponent();

    /// <summary>"Tout prendre" was clicked.</summary>
    public event EventHandler? TakeAll;

    /// <summary>Header of the ours or theirs pane.</summary>
    public void ShowSide(Color swatch, string title, string detail, string takeAll)
    {
        Swatch.Background = new SolidColorBrush(swatch);
        Swatch.Visibility = Visibility.Visible;
        TitleText.Text = title;
        DetailText.Text = detail;
        TakeAllButton.Content = takeAll;
        TakeAllButton.Visibility = Visibility.Visible;
        Legend.Visibility = Visibility.Collapsed;
    }

    /// <summary>Header of the result pane: no swatch, the legend of the two colors instead of "Tout prendre".</summary>
    public void ShowResult(string title, string detail, Color ours, string legendOurs, Color theirs, string legendTheirs)
    {
        Swatch.Visibility = Visibility.Collapsed;
        TitleText.Text = title;
        DetailText.Text = detail;
        TakeAllButton.Visibility = Visibility.Collapsed;
        LegendOursSwatch.Background = new SolidColorBrush(ours);
        LegendOursText.Text = legendOurs;
        LegendTheirsSwatch.Background = new SolidColorBrush(theirs);
        LegendTheirsText.Text = legendTheirs;
        Legend.Visibility = Visibility.Visible;
    }

    private void OnTakeAllClick(object sender, RoutedEventArgs e) => TakeAll?.Invoke(this, EventArgs.Empty);
}
