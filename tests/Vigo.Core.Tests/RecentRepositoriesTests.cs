using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class RecentRepositoriesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryRecentRepositoryStore _store = new();
    private readonly FakeTimeProvider _time = new(Start);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RepositoryInfo Repo(string name, string? branch = "main") =>
        new($@"C:\src\{name}", $@"C:\src\{name}\.git", name, branch, null, false, false, false);

    private static RecentRepository Entry(string name, int minutesAgo, bool pinned = false) =>
        new($@"C:\src\{name}", name, "main", Start.AddMinutes(-minutesAgo), pinned);

    private static string[] Names(RecentRepositories recent) => [.. recent.Items.Select(r => r.Name)];

    [Fact]
    public async Task Opened_repositories_come_first_and_are_saved()
    {
        var recent = new RecentRepositories(_store, _time);

        await recent.RecordOpenedAsync(Repo("atlas"), Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        await recent.RecordOpenedAsync(Repo("docs", branch: "content/v2"), Ct);

        Assert.Equal(["docs", "atlas"], Names(recent));
        Assert.Equal(["docs", "atlas"], _store.Saved.Select(r => r.Name));
        var docs = recent.Items[0];
        Assert.Equal(@"C:\src\docs", docs.Path);
        Assert.Equal("content/v2", docs.Branch);
        Assert.Equal(Start.AddMinutes(5), docs.LastOpened);
    }

    [Fact]
    public async Task Reopening_moves_the_entry_up_without_duplicating_it()
    {
        _store.Saved = [Entry("atlas", 10), Entry("docs", 20)];
        var recent = new RecentRepositories(_store, _time);

        await recent.RecordOpenedAsync(Repo("DOCS") with { WorkingDirectory = @"C:\SRC\docs\" }, Ct);

        Assert.Equal(["DOCS", "atlas"], Names(recent));
    }

    [Fact]
    public async Task Bare_repositories_are_recorded_by_git_directory()
    {
        var recent = new RecentRepositories(_store, _time);

        await recent.RecordOpenedAsync(new RepositoryInfo(null, @"C:\srv\atlas.git", "atlas.git", null, null, true, false, true), Ct);

        Assert.Equal(@"C:\srv\atlas.git", Assert.Single(recent.Items).Path);
    }

    [Fact]
    public async Task Pinned_entries_stay_first_and_keep_their_pin_when_reopened()
    {
        _store.Saved = [Entry("atlas", 10), Entry("docs", 20)];
        var recent = new RecentRepositories(_store, _time);

        await recent.SetPinnedAsync(@"C:\src\docs", true, Ct);
        await recent.RecordOpenedAsync(Repo("atlas"), Ct);
        await recent.RecordOpenedAsync(Repo("docs"), Ct);

        Assert.Equal(["docs", "atlas"], Names(recent));
        Assert.True(recent.Items[0].IsPinned);
        Assert.True(_store.Saved[0].IsPinned);
    }

    [Fact]
    public async Task Only_the_most_recent_unpinned_entries_are_kept()
    {
        _store.Saved = [Entry("pinned", 10_000, pinned: true), .. Enumerable.Range(0, RecentRepositories.MaxUnpinned).Select(i => Entry($"r{i}", i + 1))];
        var recent = new RecentRepositories(_store, _time);

        await recent.RecordOpenedAsync(Repo("new"), Ct);

        Assert.Equal(RecentRepositories.MaxUnpinned + 1, recent.Items.Count);
        Assert.Equal("pinned", recent.Items[0].Name);
        Assert.Equal("new", recent.Items[1].Name);
        Assert.DoesNotContain(recent.Items, r => r.Name == $"r{RecentRepositories.MaxUnpinned - 1}");
    }

    [Fact]
    public async Task Remove_drops_the_entry()
    {
        _store.Saved = [Entry("atlas", 10), Entry("docs", 20)];
        var recent = new RecentRepositories(_store, _time);

        await recent.RemoveAsync(@"c:\src\atlas", Ct);

        Assert.Equal(["docs"], Names(recent));
        Assert.Equal(["docs"], _store.Saved.Select(r => r.Name));
    }

    [Fact]
    public async Task Load_raises_changed_once_and_is_shared()
    {
        _store.Saved = [Entry("atlas", 10)];
        var recent = new RecentRepositories(_store, _time);
        var changes = 0;
        recent.Changed += (_, _) => changes++;

        await Task.WhenAll(recent.LoadAsync(Ct), recent.LoadAsync(Ct));
        await recent.LoadAsync(Ct);

        Assert.Equal(1, changes);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Json_store_round_trips_and_ignores_corrupted_files()
    {
        var directory = Directory.CreateTempSubdirectory("vigo-recent-");
        try
        {
            var path = Path.Combine(directory.FullName, "sub", "recent.json");
            var store = new JsonRecentRepositoryStore(path, NullLogger<JsonRecentRepositoryStore>.Instance);
            Assert.Empty(await store.LoadAsync(Ct));

            RecentRepository[] entries = [Entry("atlas", 10, pinned: true), Entry("docs", 20) with { Branch = null }];
            await store.SaveAsync(entries, Ct);
            Assert.Equal(entries, await store.LoadAsync(Ct));
            Assert.Contains("\"lastOpened\"", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);

            await File.WriteAllTextAsync(path, "{ not json", Ct);
            Assert.Empty(await store.LoadAsync(Ct));

            await File.WriteAllTextAsync(path, """{ "version": 99, "repositories": [] }""", Ct);
            Assert.Empty(await store.LoadAsync(Ct));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0, "à l'instant")]
    [InlineData(5, "il y a 5 min")]
    [InlineData(3 * 60, "il y a 3 h")]
    [InlineData(13 * 60, "hier")]
    [InlineData(3 * 24 * 60, "il y a 3 j")]
    [InlineData(15 * 24 * 60, "il y a 2 sem.")]
    [InlineData(70 * 24 * 60, "il y a 2 mois")]
    [InlineData(400 * 24 * 60, "31 août 2025")]
    public void Relative_time_uses_calendar_days_in_the_local_zone(int minutesAgo, string expected)
    {
        // Start is 14:00 local; for "hier", now is 11:00 local and 13 h earlier is 22:00 the day before.
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        var now = minutesAgo == 13 * 60 ? Start.AddHours(-3) : Start;

        Assert.Equal(expected, RelativeTime.Format(now.AddMinutes(-minutesAgo), now, zone));
    }

    [Fact]
    public void Item_badge_is_stable_and_initials_follow_the_mockup()
    {
        var item = new RecentRepositoryItem(Entry("atlas-api", 5), _time);

        Assert.Equal("AT", item.Initials);
        Assert.Equal(item.ColorIndex, new RecentRepositoryItem(Entry("atlas-api", 50), _time).ColorIndex);
        Assert.InRange(item.ColorIndex, 0, 9);
        Assert.True(item.Matches("ATLAS"));
        Assert.True(item.Matches(@"src\atl"));
        Assert.False(item.Matches("docs"));
    }
}
