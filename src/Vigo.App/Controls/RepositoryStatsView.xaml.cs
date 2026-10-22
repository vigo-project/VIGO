using System.ComponentModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Windows.System;

namespace Vigo.App.Controls;

/// <summary>"Statistiques" page (J7.14): charts and table of contributors. Échap goes back to the graph.</summary>
public sealed partial class RepositoryStatsView : UserControl
{
    private const double MaxColumnWidth = 1200;

    /// <summary>Below this width the cards are stacked.</summary>
    private const double NarrowWidth = 900;

    private RepositoryStatsViewModel? _viewModel;

    public RepositoryStatsView()
    {
        InitializeComponent();
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            _viewModel?.Close();
            e.Handled = true;
        };
        KeyboardAccelerators.Add(escape);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Page shown by the control; set once by the repository view.</summary>
    public RepositoryStatsViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.Slices.CollectionChanged -= OnSlicesChanged;
            }
            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                _viewModel.Slices.CollectionChanged += OnSlicesChanged;
            }
            Bindings.Update();
            UpdateCharts();
        }
    }

    /// <summary>Releases the DirectX resources of the charts when the tab is closed.</summary>
    public void Release()
    {
        ViewModel = null;
        MonthChart.Release();
        ContributorsChart.Release();
        WeekdayChart.Release();
        HourChart.Release();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _viewModel?.Close();

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<ContributorColumn>(tag, out var column))
            _viewModel?.SortByCommand.Execute(column);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RepositoryStatsViewModel.CommitsPerMonth) or nameof(RepositoryStatsViewModel.ByWeekday) or nameof(RepositoryStatsViewModel.ByHour)
                or nameof(RepositoryStatsViewModel.ContributorsSummary):
                UpdateCharts();
                break;
            case nameof(RepositoryStatsViewModel.SortColumn) or nameof(RepositoryStatsViewModel.SortDescending):
                UpdateSortMarks();
                break;
        }
    }

    private void OnSlicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateCharts();

    private void UpdateCharts()
    {
        if (_viewModel is null) return;
        MonthChart.Series = _viewModel.CommitsPerMonth;
        WeekdayChart.Series = _viewModel.ByWeekday;
        HourChart.Series = _viewModel.ByHour;
        ContributorsChart.Slices = _viewModel.Slices.ToList();
        AutomationProperties.SetName(MonthChart, _viewModel.CommitsPerMonth.Summary);
        AutomationProperties.SetName(WeekdayChart, _viewModel.ByWeekday.Summary);
        AutomationProperties.SetName(HourChart, _viewModel.ByHour.Summary);
        AutomationProperties.SetName(ContributorsChart, _viewModel.ContributorsSummary);
        UpdateSortMarks();
    }

    // ▲ ascending, ▼ descending, on the sorted column only.
    private void UpdateSortMarks()
    {
        if (_viewModel is not { } vm) return;
        var mark = vm.SortDescending ? "▼" : "▲";
        NameMark.Text = vm.SortColumn == ContributorColumn.Name ? mark : string.Empty;
        EmailMark.Text = vm.SortColumn == ContributorColumn.Email ? mark : string.Empty;
        CommitsMark.Text = vm.SortColumn == ContributorColumn.Commits ? mark : string.Empty;
        FirstMark.Text = vm.SortColumn == ContributorColumn.First ? mark : string.Empty;
        LastMark.Text = vm.SortColumn == ContributorColumn.Last ? mark : string.Empty;
    }

    // The column is centered by hand (see RepositorySummaryView); cards go from side by side to a stack on a narrow page.
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var column = Math.Min(MaxColumnWidth, width);
        Column.Width = column;
        Column.Margin = new Thickness(Math.Max(0, (width - column) / 2), 0, 0, 0);

        var narrow = width < NarrowWidth;
        MiddleColumn0.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(380);
        MiddleColumn1.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(TableCard, narrow ? 0 : 1, narrow ? 1 : 0);

        BarsColumn1.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Place(HourCard, narrow ? 0 : 1, narrow ? 1 : 0);
    }

    private static void Place(FrameworkElement card, int column, int row)
    {
        Grid.SetColumn(card, column);
        Grid.SetRow(card, row);
    }
}
