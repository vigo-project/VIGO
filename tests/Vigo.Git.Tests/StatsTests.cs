using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.Git.Stats;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Statistics of the history for the "Statistiques" page (J7.14): months, contributors, weekday and hour of the authors.</summary>
public sealed class StatsTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A commit by a given author at a given moment (ISO date with its offset), whatever the clock says.
    private void CommitAt(string isoDate, string name = "Ada", string email = "ada@example.com")
    {
        Dictionary<string, string> env = new()
        {
            ["GIT_AUTHOR_DATE"] = isoDate,
            ["GIT_COMMITTER_DATE"] = isoDate,
            ["GIT_AUTHOR_NAME"] = name,
            ["GIT_AUTHOR_EMAIL"] = email,
        };
        _repo.Git(env, "commit", "--allow-empty", "--quiet", "-m", $"at {isoDate}");
    }

    private async Task<RepositoryStats> StatsAsync(IProgress<int>? progress = null) =>
        Assert.IsType<Success<RepositoryStats>>((await _git.ReadCommitStatsAsync(_repo.Path, progress, Ct)).Value).Value;

    [Fact]
    public async Task An_empty_repository_has_no_statistics()
    {
        var stats = await StatsAsync();

        Assert.Equal(0, stats.TotalCommits);
        Assert.Empty(stats.CommitsPerMonth);
        Assert.Empty(stats.Contributors);
    }

    [Fact]
    public async Task Months_without_commits_are_counted_as_zero()
    {
        CommitAt("2025-01-10T12:00:00+00:00");
        CommitAt("2025-01-20T12:00:00+00:00");
        CommitAt("2025-04-05T12:00:00+00:00");

        var stats = await StatsAsync();

        Assert.Equal(3, stats.TotalCommits);
        Assert.Equal([(2025, 1, 2), (2025, 2, 0), (2025, 3, 0), (2025, 4, 1)], stats.CommitsPerMonth.Select(m => (m.Year, m.Month, m.Count)));
    }

    [Fact]
    public async Task The_month_is_utc_but_the_weekday_and_hour_are_the_authors_local_ones()
    {
        // Saturday 1 March 2025, 00:30 at +02:00: still 28 February (a Friday) in UTC.
        CommitAt("2025-03-01T00:30:00+02:00");

        var stats = await StatsAsync();

        Assert.Equal((2025, 2), (stats.CommitsPerMonth[0].Year, stats.CommitsPerMonth[0].Month));
        Assert.Equal(1, stats.ByWeekday[(int)DayOfWeek.Saturday]);
        Assert.Equal(0, stats.ByWeekday[(int)DayOfWeek.Friday]);
        Assert.Equal(1, stats.ByHour[0]);
        Assert.Equal(1, stats.ByHour.Sum());
    }

    [Fact]
    public async Task The_mailmap_merges_addresses_and_names()
    {
        _repo.WriteFile(".mailmap", "Ada Lovelace <ada@example.com> <old@example.com>\n");
        CommitAt("2025-02-01T10:00:00+00:00", "Ada Lovelace", "ada@example.com");
        CommitAt("2025-02-02T10:00:00+00:00", "ada", "old@example.com");

        var stats = await StatsAsync();

        var ada = Assert.Single(stats.Contributors);
        Assert.Equal("Ada Lovelace", ada.Name);
        Assert.Equal("ada@example.com", ada.Email);
        Assert.Equal(2, ada.Commits);
    }

    [Fact]
    public async Task Contributors_are_ordered_by_commits_with_their_first_and_last_commit()
    {
        CommitAt("2025-02-01T10:00:00+00:00", "Bob", "bob@example.com");
        CommitAt("2025-02-02T10:00:00+00:00", "Ada", "ada@example.com");
        CommitAt("2025-02-03T10:00:00+00:00", "Ada", "ADA@example.com");

        var stats = await StatsAsync();

        Assert.Equal(["Ada", "Bob"], stats.Contributors.Select(c => c.Name));
        Assert.Equal(2, stats.Contributors[0].Commits);
        Assert.Equal(new DateTimeOffset(2025, 2, 2, 10, 0, 0, TimeSpan.Zero), stats.Contributors[0].First);
        Assert.Equal(new DateTimeOffset(2025, 2, 3, 10, 0, 0, TimeSpan.Zero), stats.Contributors[0].Last);
    }

    [Fact]
    public async Task Progress_ends_with_the_number_of_commits_read()
    {
        for (var i = 0; i < 3; i++) _repo.Commit($"c{i}", ($"f{i}.txt", "x\n"));
        var counts = new List<int>();

        await StatsAsync(new SyncProgress(counts.Add));

        Assert.Equal(3, counts[^1]);
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    [Theory]
    [InlineData("2025-03-01 00:30:00 +0200", DayOfWeek.Saturday, 0, true)]
    [InlineData("2024-02-29 23:59:59 -0800", DayOfWeek.Thursday, 23, true)]
    [InlineData("2025-13-01 10:00:00 +0000", DayOfWeek.Sunday, 0, false)]
    [InlineData("2025-02-30 10:00:00 +0000", DayOfWeek.Sunday, 0, false)]
    [InlineData("2025-02-01", DayOfWeek.Sunday, 0, false)]
    public void The_local_weekday_and_hour_come_from_the_iso_date(string date, DayOfWeek weekday, int hour, bool valid)
    {
        Assert.Equal(valid, CommitStatsReader.TryParseLocal(date, out var parsedDay, out var parsedHour));
        if (!valid) return;
        Assert.Equal((weekday, hour), (parsedDay, parsedHour));
    }
}
