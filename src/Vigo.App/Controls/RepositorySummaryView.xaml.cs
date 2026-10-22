using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Controls;

/// <summary>"Résumé" page (J7.13): totals, latest branches, tags and commits, README. Échap goes back to the graph.</summary>
public sealed partial class RepositorySummaryView : UserControl
{
    private const double MaxColumnWidth = 1200;

    /// <summary>Below this width the three cards are stacked.</summary>
    private const double NarrowWidth = 900;

    private RepositorySummaryViewModel? _viewModel;

    public RepositorySummaryView()
    {
        InitializeComponent();
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            _viewModel?.Close();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Page shown by the control; set once by the repository view.</summary>
    public RepositorySummaryViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
        }
    }

    public void Release() => ViewModel = null;

    public static Visibility Inverse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    private void OnCloseClick(object sender, RoutedEventArgs e) => _viewModel?.Close();

    // A row of a card: back to the graph on its commit.
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        var sha = (sender as FrameworkElement)?.DataContext switch
        {
            SummaryBranchRow branch => branch.Sha,
            SummaryTagRow tag => tag.Target,
            SummaryCommitRow commit => commit.Sha,
            _ => (GitSha?)null,
        };
        if (sha is { } target) _viewModel?.SelectCommand.Execute(target);
    }

    // The column is centered by hand (see IssueView); the cards go from three columns to a stack on a narrow page.
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var column = Math.Min(MaxColumnWidth, width);
        Column.Width = column;
        Column.Margin = new Thickness(Math.Max(0, (width - column) / 2), 0, 0, 0);

        var narrow = width < NarrowWidth;
        CardColumn1.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        CardColumn2.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(TagsCard, narrow ? 0 : 1, narrow ? 1 : 0);
        Place(CommitsCard, narrow ? 0 : 2, narrow ? 2 : 0);
    }

    private static void Place(FrameworkElement card, int column, int row)
    {
        Grid.SetColumn(card, column);
        Grid.SetRow(card, row);
    }
}
