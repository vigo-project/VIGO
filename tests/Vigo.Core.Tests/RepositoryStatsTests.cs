using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

/// <summary>"Statistiques" page (J7.14): aggregation, chart data, scales, cache, cancellation.</summary>
public sealed class RepositoryStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeGit _git = new();

    private static CultureInfo Fr => CultureInfo.GetCultureInfo("fr-FR");

    private static CultureInfo En => CultureInfo.GetCultureInfo("en-US");

    private static DateTimeOffset At(int year, int month, int day, int hour = 12) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private RepositoryStatsViewModel Create()
    {
        var info = new RepositoryInfo("C:/repo", "C:/repo/.git", "repo", "main", null, false, false, false);
        var tree = new WorkingTreeViewModel(_git, info, null, NullLogger.Instance, null, null, null, null);
        return new RepositoryStatsViewModel(_git, "C:/repo", tree, CancellationToken.None);
    }

    private static RepositoryStats StatsOf(params (string Name, string Email, int Commits)[] people)
    {
        var builder = new CommitStatsBuilder();
        foreach (var (name, email, commits) in people)
        {
            for (var i = 0; i < commits; i++) builder.Add(At(2025, 1 + i % 3, 1 + i % 20), name, email, DayOfWeek.Monday, 9);
        }
        return builder.Build();
    }

    [Fact]
    public void The_builder_fills_the_empty_months_and_counts_the_utc_month()
    {
        var builder = new CommitStatsBuilder();
        builder.Add(At(2024, 11, 5), "Ada", "ada@x.dev", DayOfWeek.Tuesday, 10);
        // 31 December 23:30 at -02:00 is 1 January 01:30 in UTC.
        builder.Add(new DateTimeOffset(2024, 12, 31, 23, 30, 0, TimeSpan.FromHours(-2)), "Ada", "ada@x.dev", DayOfWeek.Tuesday, 23);
        builder.Add(At(2025, 2, 1), "Ada", "ada@x.dev", DayOfWeek.Saturday, 0);

        var stats = builder.Build();

        Assert.Equal([(2024, 11, 1), (2024, 12, 0), (2025, 1, 1), (2025, 2, 1)], stats.CommitsPerMonth.Select(m => (m.Year, m.Month, m.Count)));
        Assert.Equal(2, stats.ByWeekday[(int)DayOfWeek.Tuesday]);
        Assert.Equal(1, stats.ByHour[23]);
    }

    [Fact]
    public void The_builder_groups_by_address_without_case_then_by_name()
    {
        var builder = new CommitStatsBuilder();
        builder.Add(At(2025, 1, 1), "Ada", "ada@x.dev", DayOfWeek.Monday, 9);
        builder.Add(At(2025, 1, 2), "Ada", "ADA@x.dev", DayOfWeek.Monday, 9);
        builder.Add(At(2025, 1, 3), "ada", "ada@home.dev", DayOfWeek.Monday, 9);
        builder.Add(At(2025, 1, 4), "Bob", "bob@x.dev", DayOfWeek.Monday, 9);

        var stats = builder.Build();

        Assert.Equal(["Ada", "Bob"], stats.Contributors.Select(c => c.Name));
        Assert.Equal(3, stats.Contributors[0].Commits);
        Assert.Equal("ada@x.dev", stats.Contributors[0].Email);
        Assert.Equal(At(2025, 1, 1), stats.Contributors[0].First);
        Assert.Equal(At(2025, 1, 3), stats.Contributors[0].Last);
    }

    [Fact]
    public void An_empty_builder_gives_empty_statistics()
    {
        Assert.Same(RepositoryStats.Empty, new CommitStatsBuilder().Build());
    }

    [Theory]
    [InlineData(0, new double[] { 0, 1 })]
    [InlineData(1, new double[] { 0, 1 })]
    [InlineData(3, new double[] { 0, 1, 2, 3 })]
    [InlineData(7, new double[] { 0, 2, 4, 6, 8 })]
    [InlineData(310, new double[] { 0, 100, 200, 300, 400 })]
    [InlineData(40, new double[] { 0, 10, 20, 30, 40 })]
    public void Graduations_are_round_numbers_covering_the_maximum(double max, double[] expected) =>
        Assert.Equal(expected, ChartScale.NiceTicks(max));

    [Fact]
    public void The_week_follows_the_first_day_of_the_culture()
    {
        var stats = StatsOf(("Ada", "ada@x.dev", 3));

        var french = RepositoryStatsViewModel.WeekdaySeries(stats, Fr);
        var american = RepositoryStatsViewModel.WeekdaySeries(stats, En);

        Assert.Equal("lun.", french.Points[0].Label);
        Assert.Equal("dim.", french.Points[6].Label);
        Assert.Equal("Sun", american.Points[0].Label);
        Assert.Equal("Sat", american.Points[6].Label);
        Assert.Equal(3, french.Points[0].Value);
    }

    [Fact]
    public void Years_label_the_months_axis()
    {
        var builder = new CommitStatsBuilder();
        builder.Add(At(2024, 11, 5), "Ada", "ada@x.dev", DayOfWeek.Monday, 9);
        builder.Add(At(2025, 2, 5), "Ada", "ada@x.dev", DayOfWeek.Monday, 9);

        var series = RepositoryStatsViewModel.MonthSeries(builder.Build(), En);

        Assert.Equal(["Nov 2024", "", "2025", ""], series.Points.Select(p => p.Label));
        Assert.Equal(4, series.Points.Count);
        Assert.NotEmpty(series.Summary);
    }

    [Fact]
    public async Task Opening_reads_the_history_and_fills_the_charts_and_the_table()
    {
        _git.Stats = StatsOf(("Ada", "ada@x.dev", 5), ("Bob", "bob@x.dev", 2));
        var page = Create();

        await page.OpenAsync();

        Assert.True(page.IsOpen);
        Assert.False(page.IsComputing);
        Assert.True(page.HasData);
        Assert.Equal(["Ada", "Bob"], page.Contributors.Select(c => c.Name));
        Assert.Equal(["Ada", "Bob"], page.Slices.Select(s => s.Label));
        Assert.Equal(7, page.ByWeekday.Points.Count);
        Assert.Equal(24, page.ByHour.Points.Count);
        Assert.NotEmpty(page.CommitsPerMonth.Points);
    }

    [Fact]
    public async Task The_pie_keeps_the_eight_biggest_and_gathers_the_others()
    {
        _git.Stats = StatsOf(Enumerable.Range(1, 11).Select(i => ($"P{i:00}", $"p{i}@x.dev", 20 - i)).ToArray());
        var page = Create();

        await page.OpenAsync();

        Assert.Equal(RepositoryStatsViewModel.PieSize + 1, page.Slices.Count);
        Assert.True(page.Slices[^1].IsOther);
        Assert.Equal((20 - 9) + (20 - 10) + (20 - 11), page.Slices[^1].Value);
        Assert.Equal(11, page.Contributors.Count);
    }

    [Fact]
    public async Task Few_contributors_have_no_others_slice()
    {
        _git.Stats = StatsOf(("Ada", "ada@x.dev", 3));
        var page = Create();

        await page.OpenAsync();

        Assert.DoesNotContain(page.Slices, s => s.IsOther);
    }

    [Fact]
    public async Task Sorting_by_a_column_reverses_when_chosen_again()
    {
        _git.Stats = StatsOf(("Bob", "a@x.dev", 2), ("Ada", "z@x.dev", 5), ("Cyd", "m@x.dev", 3));
        var page = Create();
        await page.OpenAsync();
        Assert.Equal(["Ada", "Cyd", "Bob"], page.Contributors.Select(c => c.Name));

        page.SortByCommand.Execute(ContributorColumn.Name);
        Assert.Equal(["Ada", "Bob", "Cyd"], page.Contributors.Select(c => c.Name));

        page.SortByCommand.Execute(ContributorColumn.Name);
        Assert.Equal(["Cyd", "Bob", "Ada"], page.Contributors.Select(c => c.Name));

        page.SortByCommand.Execute(ContributorColumn.Email);
        Assert.Equal(["Bob", "Cyd", "Ada"], page.Contributors.Select(c => c.Name));
    }

    [Fact]
    public async Task Without_submodules_there_is_no_scope_to_choose()
    {
        var page = Create();

        await page.OpenAsync();

        Assert.False(page.HasScopes);
        Assert.Single(page.Scopes);
    }

    [Fact]
    public async Task A_submodule_scope_reads_that_submodule_and_each_scope_is_cached()
    {
        _git.Submodules.Add(new Submodule("libs/dep", "libs/dep", null, null, SubmoduleState.UpToDate, GitSha.Parse(new string('a', 40))));
        _git.Submodules.Add(new Submodule("libs/off", "libs/off", null, null, SubmoduleState.NotInitialized, null));
        _git.Stats = StatsOf(("Ada", "ada@x.dev", 3));
        var page = Create();
        await page.OpenAsync();
        Assert.True(page.HasScopes);
        // The repository, the initialized submodule only (not the other one), and everything.
        Assert.Equal(3, page.Scopes.Count);

        page.SelectedScope = page.Scopes[1];
        while (_git.StatsCalls < 2) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(["C:/repo", Path.Combine("C:/repo", "libs/dep")], _git.StatsPaths);
        Assert.False(_git.StatsIncludedSubmodules);

        page.SelectedScope = page.Scopes[2];
        while (_git.StatsCalls < 3) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(_git.StatsIncludedSubmodules);

        // Back to a scope already read: answered by the cache.
        page.SelectedScope = page.Scopes[1];
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(3, _git.StatsCalls);
    }

    [Fact]
    public async Task A_repository_without_commit_has_nothing_to_draw()
    {
        var page = Create();

        await page.OpenAsync();

        Assert.False(page.HasData);
        Assert.True(page.NoData);
        Assert.Empty(page.Slices);
        Assert.Empty(page.CommitsPerMonth.Points);
    }

    [Fact]
    public async Task A_failed_read_shows_its_message()
    {
        _git.StatsFailure = "boom";
        var page = Create();

        await page.OpenAsync();

        Assert.True(page.IsOpen);
        Assert.Equal("boom", page.Message);
        Assert.True(page.NoData);
    }

    [Fact]
    public async Task Closing_while_computing_cancels_the_read()
    {
        _git.StatsGate = new TaskCompletionSource();
        var page = Create();

        var opening = page.OpenAsync();
        Assert.True(page.IsOpen);
        while (_git.StatsCalls == 0) await Task.Delay(10, TestContext.Current.CancellationToken);
        page.Close();
        await opening;

        Assert.False(page.IsOpen);
        Assert.False(page.HasData);
    }

    [Fact]
    public async Task Refresh_reads_again()
    {
        _git.Stats = StatsOf(("Ada", "ada@x.dev", 1));
        var page = Create();
        await page.OpenAsync();

        await page.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, page.Reads);
    }

    [Fact]
    public async Task The_page_takes_the_place_of_the_graph_and_the_right_panel_and_leaves_with_the_summary()
    {
        var info = new RepositoryInfo("C:/repo", "C:/repo/.git", "repo", "main", null, false, false, false);
        using var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance);

        await tab.ShowStatsAsync();
        Assert.False(tab.ShowGraph);
        Assert.False(tab.ShowRightPanel);

        await tab.ShowSummaryAsync();
        Assert.False(tab.Stats.IsOpen);
        Assert.True(tab.Summary.IsOpen);
        Assert.False(tab.ShowGraph);

        await tab.ShowStatsAsync();
        Assert.False(tab.Summary.IsOpen);

        tab.Stats.Close();
        Assert.True(tab.ShowGraph);
        Assert.True(tab.ShowRightPanel);
    }
}
