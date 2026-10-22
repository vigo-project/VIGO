using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Columns of the contributors table, to sort by.</summary>
public enum ContributorColumn
{
    Name,
    Email,
    Commits,
    First,
    Last,
}

/// <summary>A line of the contributors table.</summary>
public sealed record ContributorRow(Contributor Contributor, string PercentText, string FirstText, string LastText)
{
    public string Name => Contributor.Name;

    public string Email => Contributor.Email;

    public string CommitsText => Contributor.Commits.ToString(CultureInfo.CurrentCulture);
}

/// <summary>
/// "Statistiques" page of the left panel (J7.14, ADR-080), shown in place of the graph and of the right panel: commits per
/// month, top contributors, the table of all of them, and when people work (weekday, hour). The history is read and aggregated
/// off the UI thread, with a counter of the commits read; what was computed is kept for the commit HEAD points to, so that
/// opening the page again is instant until HEAD moves. Closing the page cancels the read.
/// </summary>
public sealed partial class RepositoryStatsViewModel : ObservableObject, IDisposable
{
    /// <summary>The pie chart shows this many contributors, the others are gathered in "Autres".</summary>
    public const int PieSize = 8;

    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly WorkingTreeViewModel _tree;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _computing;
    private RepositoryStats? _stats;
    private GitSha? _statsHead;

    public RepositoryStatsViewModel(IGitService git, string repositoryPath, WorkingTreeViewModel tree, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _tree = tree;
        _lifetime = lifetime;
        _tree.RefsChanged += OnRefsChanged;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The history is being read.</summary>
    [ObservableProperty]
    public partial bool IsComputing { get; private set; }

    /// <summary>"120 000 commits lus…" while computing.</summary>
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>At least one commit was read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoData))]
    public partial bool HasData { get; private set; }

    /// <summary>Nothing to draw and nothing running: no commit, or a read error.</summary>
    public bool NoData => !HasData && !IsComputing;

    partial void OnIsComputingChanged(bool value) => OnPropertyChanged(nameof(NoData));

    [ObservableProperty]
    public partial ChartSeries CommitsPerMonth { get; private set; } = ChartSeries.Empty;

    [ObservableProperty]
    public partial ChartSeries ByWeekday { get; private set; } = ChartSeries.Empty;

    [ObservableProperty]
    public partial ChartSeries ByHour { get; private set; } = ChartSeries.Empty;

    /// <summary>Top contributors for the pie chart.</summary>
    public ObservableCollection<PieSlice> Slices { get; } = [];

    /// <summary>Alternative text of the pie chart.</summary>
    [ObservableProperty]
    public partial string ContributorsSummary { get; private set; } = string.Empty;

    /// <summary>Every contributor, in the order of <see cref="SortColumn"/>.</summary>
    public ObservableCollection<ContributorRow> Contributors { get; } = [];

    [ObservableProperty]
    public partial ContributorColumn SortColumn { get; private set; } = ContributorColumn.Commits;

    [ObservableProperty]
    public partial bool SortDescending { get; private set; } = true;

    /// <summary>How many times git was asked (the cache answers the others); tests count them.</summary>
    internal int Reads { get; private set; }

    /// <summary>Shows the page and computes the statistics, unless they are known for the current HEAD.</summary>
    public async Task OpenAsync()
    {
        IsOpen = true;
        await ComputeAsync();
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Cancel();
    }

    /// <summary>Reads the history again, even when HEAD has not moved.</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        _stats = null;
        _statsHead = null;
        return ComputeAsync();
    }

    /// <summary>Sorts the table by a column; the same column again reverses the order.</summary>
    [RelayCommand]
    private void SortBy(ContributorColumn column)
    {
        if (column == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            // Numbers and dates start with the largest, names with A.
            SortDescending = column is ContributorColumn.Commits or ContributorColumn.First or ContributorColumn.Last;
        }
        ShowContributors();
    }

    // HEAD moved (commit, checkout, fetch of the checked-out branch…): what is shown is out of date.
    private void OnRefsChanged(object? sender, EventArgs e)
    {
        if (IsOpen && _tree.Status?.Head != _statsHead) _ = ComputeAsync();
    }

    private void Cancel()
    {
        _computing?.Cancel();
        _computing?.Dispose();
        _computing = null;
    }

    private async Task ComputeAsync()
    {
        Cancel();
        var head = _tree.Status?.Head;
        if (_stats is { } known && head is not null && head == _statsHead)
        {
            Message = null;
            Apply(known);
            return;
        }

        var source = _computing = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var token = source.Token;
        IsComputing = true;
        ProgressText = string.Empty;
        Message = null;
        try
        {
            Reads++;
            // Created here, on the UI thread: Progress<T> posts its reports back to it.
            var progress = new Progress<int>(count =>
            {
                if (!token.IsCancellationRequested) ProgressText = Loc.Format("Stats.Progress", count.ToString("N0", CultureInfo.CurrentCulture));
            });
            var result = await Task.Run(() => _git.ReadCommitStatsAsync(_repositoryPath, progress, token), CancellationToken.None);
            if (token.IsCancellationRequested) return;

            switch (result.Value)
            {
                case Success<RepositoryStats> { Value: var stats }:
                    _stats = stats;
                    _statsHead = head;
                    Apply(stats);
                    break;
                case Failure failure:
                    Message = failure.Message;
                    Apply(RepositoryStats.Empty);
                    break;
                case Conflict or AuthenticationFailed:
                    Message = Loc.Get("Common.UnexpectedResult");
                    Apply(RepositoryStats.Empty);
                    break;
                case Canceled:
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_computing == source)
            {
                IsComputing = false;
                ProgressText = string.Empty;
            }
        }
    }

    private void Apply(RepositoryStats stats)
    {
        HasData = stats.TotalCommits > 0;
        var culture = CultureInfo.CurrentCulture;

        CommitsPerMonth = MonthSeries(stats, culture);
        ByWeekday = WeekdaySeries(stats, culture);
        ByHour = HourSeries(stats);
        ShowPie(stats);
        ShowContributors();
    }

    private static string CommitsText(int count) =>
        Loc.Format(count == 1 ? "Stats.Commits.One" : "Stats.Commits.Other", count.ToString("N0", CultureInfo.CurrentCulture));

    private static string Tooltip(string label, int count) => Loc.Format("Stats.Tooltip", label, CommitsText(count));

    internal static ChartSeries MonthSeries(RepositoryStats stats, CultureInfo culture)
    {
        if (stats.CommitsPerMonth.Count == 0) return ChartSeries.Empty;
        var points = new List<ChartPoint>(stats.CommitsPerMonth.Count);
        foreach (var month in stats.CommitsPerMonth)
        {
            var date = new DateTime(month.Year, month.Month, 1);
            // Years label the axis (at January); a series of less than a year labels its first month.
            var label = month.Month == 1 ? month.Year.ToString(culture) : points.Count == 0 ? date.ToString("MMM yyyy", culture) : string.Empty;
            points.Add(new ChartPoint(label, month.Count, Tooltip(date.ToString("MMMM yyyy", culture), month.Count)));
        }

        var peak = stats.CommitsPerMonth.MaxBy(m => m.Count)!;
        var first = stats.CommitsPerMonth[0];
        var last = stats.CommitsPerMonth[^1];
        string Name(MonthCount m) => new DateTime(m.Year, m.Month, 1).ToString("MMM yyyy", culture);
        var summary = Loc.Format("Stats.Summary.Month", Name(first), Name(last), peak.Count.ToString("N0", culture), Name(peak));
        return new ChartSeries(points, summary);
    }

    /// <summary>The week in the order of the culture (Monday first in France, Sunday first in the United States).</summary>
    internal static ChartSeries WeekdaySeries(RepositoryStats stats, CultureInfo culture)
    {
        if (stats.TotalCommits == 0) return ChartSeries.Empty;
        var first = (int)culture.DateTimeFormat.FirstDayOfWeek;
        var points = new List<ChartPoint>(7);
        for (var i = 0; i < 7; i++)
        {
            var day = (first + i) % 7;
            points.Add(new ChartPoint(culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)day), stats.ByWeekday[day],
                Tooltip(culture.DateTimeFormat.GetDayName((DayOfWeek)day), stats.ByWeekday[day])));
        }
        var peak = points.MaxBy(p => p.Value)!;
        var peakDay = culture.DateTimeFormat.GetDayName((DayOfWeek)((first + points.IndexOf(peak)) % 7));
        return new ChartSeries(points, Loc.Format("Stats.Summary.Weekday", peak.Value.ToString("N0", culture), peakDay));
    }

    internal static ChartSeries HourSeries(RepositoryStats stats)
    {
        if (stats.TotalCommits == 0) return ChartSeries.Empty;
        var points = new List<ChartPoint>(24);
        for (var hour = 0; hour < 24; hour++)
            points.Add(new ChartPoint(hour.ToString(CultureInfo.CurrentCulture), stats.ByHour[hour], Tooltip(Loc.Format("Stats.Hour", hour), stats.ByHour[hour])));
        var peak = points.MaxBy(p => p.Value)!;
        return new ChartSeries(points, Loc.Format("Stats.Summary.Hour", peak.Value.ToString("N0", CultureInfo.CurrentCulture), points.IndexOf(peak)));
    }

    private void ShowPie(RepositoryStats stats)
    {
        var slices = new List<PieSlice>();
        var top = stats.Contributors.Take(PieSize).ToList();
        foreach (var person in top) slices.Add(new PieSlice(person.Name, person.Commits, Tooltip(person.Name, person.Commits)));
        var others = stats.Contributors.Skip(PieSize).Sum(c => c.Commits);
        if (others > 0)
        {
            var label = Loc.Get("Stats.Others");
            slices.Add(new PieSlice(label, others, Tooltip(label, others), IsOther: true));
        }

        Slices.Clear();
        foreach (var slice in slices) Slices.Add(slice);
        ContributorsSummary = top.Count == 0
            ? string.Empty
            : Loc.Format("Stats.Summary.Contributors", string.Join(", ", top.Take(5).Select(c => $"{c.Name} ({c.Commits})")));
    }

    private void ShowContributors()
    {
        var culture = CultureInfo.CurrentCulture;
        IEnumerable<Contributor> all = _stats?.Contributors ?? [];
        var total = Math.Max(1, _stats?.TotalCommits ?? 1);

        IOrderedEnumerable<Contributor> ordered = SortColumn switch
        {
            ContributorColumn.Name => Order(all, c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            ContributorColumn.Email => Order(all, c => c.Email, StringComparer.CurrentCultureIgnoreCase),
            ContributorColumn.First => Order(all, c => c.First, Comparer<DateTimeOffset>.Default),
            ContributorColumn.Last => Order(all, c => c.Last, Comparer<DateTimeOffset>.Default),
            _ => Order(all, c => c.Commits, Comparer<int>.Default),
        };

        var rows = ordered.ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ContributorRow(c, ((double)c.Commits / total).ToString("P1", culture), c.First.ToLocalTime().ToString("d", culture), c.Last.ToLocalTime().ToString("d", culture)))
            .ToList();
        Contributors.Clear();
        foreach (var row in rows) Contributors.Add(row);
    }

    private IOrderedEnumerable<Contributor> Order<TKey>(IEnumerable<Contributor> source, Func<Contributor, TKey> key, IComparer<TKey> comparer) =>
        SortDescending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

    public void Dispose()
    {
        _tree.RefsChanged -= OnRefsChanged;
        Cancel();
    }
}
