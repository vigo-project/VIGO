using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class HiddenRefsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vigo-hidden-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string FilePath => Path.Combine(_directory, "hidden-refs.json");

    private JsonHiddenRefsStore Store() => new(FilePath, NullLogger<JsonHiddenRefsStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Missing_file_hides_nothing()
    {
        using var store = Store();

        Assert.Empty(store.Load(@"C:\repo\.git"));
    }

    [Fact]
    public async Task Saved_keys_are_read_back_per_repository()
    {
        using var store = Store();
        await store.SaveAsync(@"C:\a\.git", ["refs/heads/x", "stash:abc"], Ct);
        await store.SaveAsync(@"C:\b\.git", ["refs/tags/v1"], Ct);

        using var again = Store();
        Assert.Equal(["refs/heads/x", "stash:abc"], again.Load(@"C:\a\.git").Order());
        Assert.Equal(["refs/tags/v1"], again.Load(@"C:\B\.git"));

        await store.SaveAsync(@"C:\a\.git", [], Ct);
        Assert.Empty(again.Load(@"C:\a\.git"));
        Assert.Single(again.Load(@"C:\b\.git"));
    }

    [Fact]
    public async Task Corrupt_file_is_ignored()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{ not json", Ct);
        using var store = Store();

        Assert.Empty(store.Load("repo"));
    }

    [Fact]
    public async Task Toggle_is_saved_and_restored_by_the_next_instance()
    {
        using var store = Store();
        var first = new HiddenRefs(store, "repo");
        var raised = 0;
        first.Changed += (_, _) => raised++;

        first.Toggle("refs/heads/x");
        first.Toggle("refs/tags/v1");
        first.Toggle("refs/tags/v1");
        await first.WhenSavedAsync();

        Assert.Equal(3, raised);
        var second = new HiddenRefs(store, "repo");
        Assert.True(second.IsHidden("refs/heads/x"));
        Assert.False(second.IsHidden("refs/tags/v1"));
        Assert.Equal(["refs/heads/x"], second.ExcludedRefs());
    }

    [Fact]
    public void Stashes_are_keyed_by_commit_and_left_out_of_the_excluded_refs()
    {
        var sha = GitSha.Parse(new string('a', 40));
        var stash = new GitRef("stash@{0}", "stash@{0}", GitRefKind.Stash, sha);
        var hidden = new HiddenRefs(null, "repo");

        hidden.Toggle(HiddenRefs.KeyOf(stash));

        Assert.True(hidden.IsHidden(stash));
        Assert.Empty(hidden.ExcludedRefs());
    }

    [Fact]
    public async Task Hidden_branch_loses_its_badge_and_the_commits_only_it_reaches()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "1"));
        repo.Branch("feature");
        var feature = repo.Commit("feature work", ("b.txt", "1"));
        repo.Checkout("main");
        repo.Commit("main work", ("a.txt", "2"));
        repo.Tag("v1");
        using var sut = new GraphViewModel(_git, repo.Path, new FakeTimeProvider(Now), NullLogger.Instance, new HiddenRefs(null, repo.Path));
        await sut.LoadAsync(Ct);
        Assert.Equal(3, sut.RowCount);

        sut.Hidden.Toggle("refs/heads/feature");
        await sut.WhenReloadedAsync();

        Assert.Equal(2, sut.RowCount);
        Assert.Null(sut.FindCommit(GitSha.Parse(feature)));
        Assert.Contains(sut.GetBadges(sut.Head!.Value), b => b.Label == "v1");

        sut.Hidden.Toggle("refs/tags/v1");
        await sut.WhenReloadedAsync();
        Assert.DoesNotContain(sut.GetBadges(sut.Head!.Value), b => b.Kind == RefBadgeKind.Tag);

        sut.Hidden.Toggle("refs/heads/feature");
        await sut.WhenReloadedAsync();
        Assert.Equal(3, sut.RowCount);
        Assert.Contains(sut.GetBadges(GitSha.Parse(feature)), b => b.Label == "feature");
    }

    [Fact]
    public async Task Hidden_stash_is_not_drawn()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "1"));
        repo.WriteFile("a.txt", "2");
        repo.Stash("wip");
        using var sut = new GraphViewModel(_git, repo.Path, new FakeTimeProvider(Now), NullLogger.Instance, new HiddenRefs(null, repo.Path));
        await sut.LoadAsync(Ct);
        Assert.Equal(2, sut.RowCount);
        var stash = Assert.Single(sut.Refs!.OfKind(GitRefKind.Stash));

        sut.Hidden.Toggle(HiddenRefs.KeyOf(stash));
        await sut.WhenReloadedAsync();

        Assert.Equal(1, sut.RowCount);
    }

    [Fact]
    public async Task Rows_of_hidden_refs_are_marked_and_the_checked_out_branch_has_no_eye()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "1"));
        repo.Branch("feature", checkout: false);
        repo.Tag("v1");
        using var sut = new GraphViewModel(_git, repo.Path, new FakeTimeProvider(Now), NullLogger.Instance, new HiddenRefs(null, repo.Path));
        await sut.LoadAsync(Ct);
        sut.Hidden.Toggle("refs/heads/feature");

        var rows = RefRowBuilder.BuildRows(sut.Refs, [], string.Empty, new HashSet<string>(), _ => null, hidden: sut.Hidden);

        var main = rows.Single(r => r.Key == "local:refs/heads/main");
        Assert.False(main.CanHide);
        var feature = rows.Single(r => r.Key == "local:refs/heads/feature");
        Assert.True(feature.CanHide);
        Assert.True(feature.IsHidden);
        // Tags section is collapsed by default in the panel, but this call expands everything.
        var tag = rows.Single(r => r.Key == "tag:refs/tags/v1");
        Assert.True(tag.CanHide);
        Assert.False(tag.IsHidden);
    }
}
