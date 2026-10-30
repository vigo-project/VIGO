using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vigo.Core.ViewModels;

namespace Vigo.App.Controls;

/// <summary>View zone of the native conflict editor: header of a conflict in a pane, or card of an unresolved one (see the XAML).</summary>
public sealed partial class ConflictZone : UserControl
{
    private ConflictTake _blockTake;

    public ConflictZone() => InitializeComponent();

    /// <summary>A choice was clicked: the pane's block, or ours / theirs / both on a card.</summary>
    public event EventHandler<ConflictTake>? Taken;

    /// <summary>Header of a conflict in the pane of <paramref name="take"/>'s side.</summary>
    public void ShowBlock(string label, string takeLabel, ConflictTake take, Brush background, Brush border)
    {
        _blockTake = take;
        BlockLabel.Text = label;
        TakeButton.Content = takeLabel;
        BlockBorder.Background = background;
        BlockBorder.BorderBrush = border;
        BlockBorder.Visibility = Visibility.Visible;
    }

    /// <summary>Card of an unresolved conflict in the result; <paramref name="currentBorder"/> outlines the current one.</summary>
    public void ShowCard(string label, string ours, string theirs, string both, Brush? currentBorder)
    {
        CardLabel.Text = label;
        OursButton.Content = ours;
        TheirsButton.Content = theirs;
        BothButton.Content = both;
        if (currentBorder is not null) CardBorder.BorderBrush = currentBorder;
        CardBorder.Visibility = Visibility.Visible;
    }

    private void OnTakeClick(object sender, RoutedEventArgs e) => Taken?.Invoke(this, _blockTake);

    private void OnOursClick(object sender, RoutedEventArgs e) => Taken?.Invoke(this, ConflictTake.Ours);

    private void OnTheirsClick(object sender, RoutedEventArgs e) => Taken?.Invoke(this, ConflictTake.Theirs);

    private void OnBothClick(object sender, RoutedEventArgs e) => Taken?.Invoke(this, ConflictTake.Both);
}
