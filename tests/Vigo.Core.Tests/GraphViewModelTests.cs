using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class GraphViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GraphViewModel CreateSut(string path) => new(_git, path, new FakeTimeProvider(Now), NullLogger.Instance);

    private static GitSha Sha(string hex) => GitSha.Parse(hex);

    [Fact]
    public async Task Loads_the_whole_history_with_lanes_and_badges()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "1"));
        repo.Branch("feature");
        var feature = repo.Commit("feature work", ("b.txt", "1"));
        repo.Checkout("main");
        repo.Commit("main work", ("a.txt", "2"));
        var merge = repo.Merge("feature", "Merge feature");
        repo.Tag("v1.0", "Release");
        using var sut = CreateSut(repo.Path);
        var changes = 0;
        sut.Changed += (_, _) => changes++;

        await sut.LoadAsync(Ct);

        Assert.False(sut.IsLoading);
        Assert.Null(sut.ErrorMessage);
        Assert.False(sut.IsEmpty);
        Assert.Equal(4, sut.RowCount);
        Assert.Equal(4, sut.Lanes.RowCount);
        Assert.True(changes >= 1);
        Assert.Equal(Sha(merge), sut.Head);
        Assert.Equal("Merge feature", sut.GetCommit(0).Subject);
        Assert.Equal(2, sut.Lanes.GetOutgoingLanes(0).Length);

        var badges = sut.GetBadges(Sha(merge));
        Assert.Equal([("main", RefBadgeKind.Head), ("v1.0", RefBadgeKind.Tag)], badges.Select(b => (b.Label, b.Kind)));
        Assert.Equal(RefBadgeKind.Branch, Assert.Single(sut.GetBadges(Sha(feature))).Kind);
        Assert.Empty(sut.GetBadges(sut.GetCommit(3).Sha));
    }

    [Fact]
    public async Task Empty_repository_is_reported_as_empty()
    {
        using var repo = TestRepoBuilder.Create();
        using var sut = CreateSut(repo.Path);

        await sut.LoadAsync(Ct);

        Assert.True(sut.IsEmpty);
        Assert.Null(sut.Head);
    }

    [Fact]
    public async Task Unreadable_repository_sets_an_error_message()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vigo-missing-" + Guid.NewGuid().ToString("N"));
        using var sut = CreateSut(missing);

        await sut.LoadAsync(Ct);

        Assert.NotNull(sut.ErrorMessage);
        Assert.False(sut.IsEmpty);
        Assert.False(sut.IsLoading);
    }

    [Fact]
    public async Task Selection_moves_within_loaded_rows()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("a", ("a.txt", "1"));
        repo.Commit("b", ("a.txt", "2"));
        using var sut = CreateSut(repo.Path);
        await sut.LoadAsync(Ct);

        sut.MoveSelection(1);
        Assert.Equal(0, sut.SelectedRow);
        sut.MoveSelection(10);
        Assert.Equal(1, sut.SelectedRow);
        sut.MoveSelection(-10);
        Assert.Equal(0, sut.SelectedRow);
    }

    [Fact]
    public async Task Refresh_keeps_the_history_when_refs_only_point_at_loaded_commits()
    {
        using var repo = TestRepoBuilder.Create();
        var root = repo.Commit("root", ("a.txt", "1"));
        repo.Branch("feature");
        var feature = repo.Commit("feature", ("b.txt", "1"));
        repo.Checkout("main");
        using var sut = CreateSut(repo.Path);
        await sut.LoadAsync(Ct);
        var lanes = sut.Lanes;
        var refsChanged = 0;
        sut.RefsChanged += (_, _) => refsChanged++;

        // Checkout of an existing branch, tag and branch on known commits: no new commit, no tip dropped.
        repo.Checkout("feature");
        repo.Tag("v1.0", target: root);
        repo.Git("branch", "other", root);
        await sut.RefreshAsync();

        Assert.Same(lanes, sut.Lanes);
        Assert.Equal(1, refsChanged);
        Assert.Equal(Sha(feature), sut.Head);
        Assert.Equal("feature", sut.HeadBranch);
        Assert.Equal(["main", "other", "v1.0"], sut.GetBadges(Sha(root)).Select(b => b.Label).Order());
        Assert.Contains(sut.Refs!.Refs, r => r.Name == "other");
    }

    [Fact]
    public async Task Refresh_rereads_the_history_for_a_new_commit_or_a_dropped_tip()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "1"));
        repo.Branch("feature");
        repo.Commit("feature", ("b.txt", "1"));
        repo.Checkout("main");
        using var sut = CreateSut(repo.Path);
        await sut.LoadAsync(Ct);
        var lanes = sut.Lanes;

        var added = repo.Commit("main work", ("a.txt", "2"));
        await sut.RefreshAsync();
        Assert.NotSame(lanes, sut.Lanes);
        Assert.Equal(3, sut.RowCount);
        Assert.Equal(Sha(added), sut.Head);

        // The only ref on "feature" is deleted: its commit is no longer reachable.
        lanes = sut.Lanes;
        repo.Git("branch", "-D", "feature");
        await sut.RefreshAsync();
        Assert.NotSame(lanes, sut.Lanes);
        Assert.Equal(2, sut.RowCount);
    }

    [Fact]
    public void Badges_fold_a_synced_upstream_and_keep_kind_order()
    {
        var a = Sha(new string('a', 40));
        var b = Sha(new string('b', 40));
        var refs = new RepositoryRefs(
        [
            new GitRef("refs/tags/v1", "v1", GitRefKind.Tag, a),
            new GitRef("refs/remotes/origin/main", "origin/main", GitRefKind.RemoteBranch, a) { Remote = "origin" },
            new GitRef("refs/remotes/origin/dev", "origin/dev", GitRefKind.RemoteBranch, a) { Remote = "origin" },
            new GitRef("refs/heads/main", "main", GitRefKind.LocalBranch, a) { IsCurrent = true, Upstream = "origin/main" },
            new GitRef("refs/heads/dev", "dev", GitRefKind.LocalBranch, b) { Upstream = "origin/dev" },
        ], a, "main");

        var badges = RefBadge.FromRefs(refs);

        Assert.Equal(
            [new RefBadge("main", RefBadgeKind.Head, IsOnRemote: true), new RefBadge("origin/dev", RefBadgeKind.Remote), new RefBadge("v1", RefBadgeKind.Tag)],
            badges[a]);
        Assert.Equal([new RefBadge("dev", RefBadgeKind.Branch)], badges[b]);
    }

    [Fact]
    public void Detached_head_gets_its_own_badge()
    {
        var a = Sha(new string('a', 40));

        var badges = RefBadge.FromRefs(new RepositoryRefs([], a, null));

        Assert.Equal([new RefBadge("HEAD", RefBadgeKind.Head)], badges[a]);
    }

    [Theory]
    [InlineData(90, "il y a 1 h")]
    [InlineData(2 * 24 * 60, "il y a 2 j")]
    [InlineData(7 * 24 * 60, "lun. 28 sept.")]
    [InlineData(300 * 24 * 60, "9 déc. 2025")]
    public void Commit_dates_are_relative_for_a_week_then_absolute(int minutesAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.FormatCommitDate(Now.AddMinutes(-minutesAgo), Now, Zone));
}
