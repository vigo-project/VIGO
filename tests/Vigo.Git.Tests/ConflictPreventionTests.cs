using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Conflict prevention (J7.11): the default remote branch, the dry-run merge and the files changed on the target.</summary>
public sealed class ConflictPreventionTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _remote = TestRepoBuilder.Create(bare: true);
    private readonly TestRepoBuilder _seed = TestRepoBuilder.Create();
    private readonly List<TestRepoBuilder> _others = [];

    public ConflictPreventionTests()
    {
        _seed.Commit("init", ("a.txt", "1\n2\n3\n"), ("b.txt", "b\n"));
        _seed.Git("remote", "add", "origin", _remote.Path);
        _seed.Git("push", "--quiet", "origin", "main");
    }

    public void Dispose()
    {
        _seed.Dispose();
        _remote.Dispose();
        foreach (var other in _others) other.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A clone of the remote (so refs/remotes/origin/HEAD exists), on a feature branch.</summary>
    private TestRepoBuilder CloneOnFeature()
    {
        var mine = TestRepoBuilder.Clone(_remote.Path, firstTick: 50_000);
        _others.Add(mine);
        mine.Branch("feature");
        return mine;
    }

    private void RemoteAdvances(params (string Path, string Content)[] files)
    {
        _seed.Commit("on main", files);
        _seed.Git("push", "--quiet", "origin", "main");
    }

    private static ConflictTarget Target(GitResult<ConflictTarget> result) => Assert.IsType<Success<ConflictTarget>>(result.Value).Value;

    private static IReadOnlyList<string> Files(GitResult<IReadOnlyList<string>> result) => Assert.IsType<Success<IReadOnlyList<string>>>(result.Value).Value;

    private static GitSha Sha(string hex) => GitSha.Parse(hex.Trim());

    [Fact]
    public async Task The_target_is_the_default_branch_the_remote_head_points_to()
    {
        var mine = CloneOnFeature();

        var target = Target(await _git.GetConflictTargetAsync(mine.Path, Ct));

        Assert.Equal("origin/main", target.Name);
        Assert.Equal(Sha(mine.Git("rev-parse", "origin/main")), target.Sha);
    }

    [Fact]
    public async Task Without_a_remote_head_the_target_falls_back_on_main()
    {
        using var mine = TestRepoBuilder.Create();
        mine.Commit("init", ("a.txt", "1\n"));
        mine.Git("remote", "add", "origin", _remote.Path);
        mine.Git("fetch", "--quiet", "origin");
        mine.Branch("feature");

        Assert.Equal("origin/main", Target(await _git.GetConflictTargetAsync(mine.Path, Ct)).Name);
    }

    [Fact]
    public async Task Without_main_the_target_falls_back_on_master()
    {
        using var old = TestRepoBuilder.Create(bare: true);
        using var source = TestRepoBuilder.Create("master");
        source.Commit("init", ("a.txt", "1\n"));
        source.Git("push", "--quiet", old.Path, "master");
        using var mine = TestRepoBuilder.Create();
        mine.Commit("init", ("a.txt", "1\n"));
        mine.Git("remote", "add", "origin", old.Path);
        mine.Git("fetch", "--quiet", "origin");

        Assert.Equal("origin/master", Target(await _git.GetConflictTargetAsync(mine.Path, Ct)).Name);
    }

    [Fact]
    public async Task The_remote_is_the_one_of_the_upstream()
    {
        var mine = CloneOnFeature();
        mine.Git("remote", "add", "upstream", _remote.Path);
        mine.Git("fetch", "--quiet", "upstream");
        mine.Git("config", "branch.feature.remote", "upstream");

        Assert.Equal("upstream/main", Target(await _git.GetConflictTargetAsync(mine.Path, Ct)).Name);
    }

    [Fact]
    public async Task There_is_no_target_on_a_detached_head_or_without_a_remote_branch()
    {
        var mine = CloneOnFeature();
        mine.Git("checkout", "--quiet", "--detach");
        Assert.IsType<Failure>((await _git.GetConflictTargetAsync(mine.Path, Ct)).Value);

        using var alone = TestRepoBuilder.Create();
        alone.Commit("init", ("a.txt", "1\n"));
        Assert.IsType<Failure>((await _git.GetConflictTargetAsync(alone.Path, Ct)).Value);
    }

    [Fact]
    public async Task A_merge_that_goes_through_has_no_conflict_and_touches_nothing()
    {
        var mine = CloneOnFeature();
        mine.Commit("mine", ("c.txt", "c\n"));
        RemoteAdvances(("d.txt", "d\n"));
        mine.Git("fetch", "--quiet");
        var head = Sha(mine.Head());
        var target = Sha(mine.Git("rev-parse", "origin/main"));

        var files = Files(await _git.PredictConflictsAsync(mine.Path, head, target, Ct));

        Assert.Empty(files);
        Assert.Equal(head, Sha(mine.Head()));
        Assert.Equal(string.Empty, mine.Git("status", "--porcelain").Trim());
        Assert.False(File.Exists(Path.Combine(mine.Path, "d.txt")));
    }

    [Fact]
    public async Task Two_edits_of_the_same_lines_are_a_conflict_listing_the_file()
    {
        var mine = CloneOnFeature();
        mine.Commit("mine", ("a.txt", "1\nmine\n3\n"), ("b.txt", "mine b\n"));
        RemoteAdvances(("a.txt", "1\ntheirs\n3\n"), ("b.txt", "their b\n"), ("other.txt", "x\n"));
        mine.Git("fetch", "--quiet");

        var files = Files(await _git.PredictConflictsAsync(mine.Path, Sha(mine.Head()), Sha(mine.Git("rev-parse", "origin/main")), Ct));

        Assert.Equal(["a.txt", "b.txt"], files.Order(StringComparer.Ordinal));
        Assert.Equal(string.Empty, mine.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task A_rename_on_one_side_and_an_edit_on_the_other_merge_cleanly()
    {
        var mine = CloneOnFeature();
        mine.Git("mv", "b.txt", "renamed.txt");
        mine.Commit("rename");
        RemoteAdvances(("b.txt", "edited\n"));
        mine.Git("fetch", "--quiet");

        var files = Files(await _git.PredictConflictsAsync(mine.Path, Sha(mine.Head()), Sha(mine.Git("rev-parse", "origin/main")), Ct));

        Assert.Empty(files);
    }

    [Fact]
    public async Task The_files_changed_on_the_target_since_the_merge_base_are_listed()
    {
        var mine = CloneOnFeature();
        mine.Commit("mine", ("mine.txt", "m\n"));
        RemoteAdvances(("a.txt", "1\ntheirs\n3\n"), ("new dir/new.txt", "n\n"));
        mine.Git("fetch", "--quiet");
        var head = Sha(mine.Head());

        var files = Files(await _git.GetChangedFilesSinceMergeBaseAsync(mine.Path, head, Sha(mine.Git("rev-parse", "origin/main")), Ct));

        Assert.Equal(["a.txt", "new dir/new.txt"], files.Order(StringComparer.Ordinal));
        Assert.Empty(Files(await _git.GetChangedFilesSinceMergeBaseAsync(mine.Path, head, Sha(mine.Git("rev-parse", "origin/main~1")), Ct)));
    }

    [Theory]
    [InlineData("2.50.1.windows.1", 2, 50)]
    [InlineData("2.38.0", 2, 38)]
    [InlineData("2.9", 2, 9)]
    public void The_git_version_is_read_from_its_first_numbers(string text, int major, int minor)
    {
        Assert.True(GitService.TryParseVersion(text, out var version));
        Assert.Equal((major, minor), (version.Major, version.Minor));
    }

    [Fact]
    public void Output_without_conflict_gives_no_file_and_the_tree_id_is_skipped()
    {
        Assert.Empty(GitService.ParseMergeTree(new CliOutput(0, "abc123\0", string.Empty)));
        Assert.Equal(["a.txt", "dir/b c.txt"], GitService.ParseMergeTree(new CliOutput(1, "abc123\0a.txt\0dir/b c.txt\0a.txt\0", string.Empty)));
    }
}
