using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class GitFlowTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo;
    private static readonly GitFlowConfig Config = GitFlowConfig.Defaults() with { VersionTagPrefix = "v" };

    public GitFlowTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Commit("init", ("a.txt", "a\n"));
    }

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private static GitFlowFinished AssertFinished(GitResult<GitFlowFinished> result) =>
        Assert.IsType<Success<GitFlowFinished>>(result.Value, exactMatch: false).Value;

    private async Task InitAsync() => AssertDone(await _git.GitFlowInitAsync(_repo.Path, Config, Ct));

    private int ParentCount(string revision) => _repo.Git("rev-list", "--parents", "-n", "1", revision).Trim().Split(' ').Length - 1;

    private bool Exists(string reference) => _repo.Git("for-each-ref", reference).Trim().Length > 0;

    // ---- Settings --------------------------------------------------------------------------------------------------

    [Fact]
    public void Settings_are_read_from_the_gitflow_keys_with_the_extension_defaults_for_the_missing_ones()
    {
        var config = GitService.ParseGitFlowConfig(
            "gitflow.branch.master master\ngitflow.branch.develop dev\ngitflow.prefix.feature feat/\ngitflow.prefix.versiontag v\n");

        Assert.Equal(new GitFlowConfig("master", "dev", "feat/", "release/", "hotfix/", "v"), config);
    }

    [Fact]
    public void An_empty_version_tag_prefix_is_kept_empty()
    {
        var config = GitService.ParseGitFlowConfig("gitflow.branch.master main\ngitflow.branch.develop develop\ngitflow.prefix.versiontag \n");

        Assert.Equal(string.Empty, config!.VersionTagPrefix);
    }

    [Fact]
    public async Task Saving_the_settings_round_trips_the_six_keys_and_never_creates_a_branch()
    {
        await InitAsync();
        _repo.Git("branch", "trunk");
        var edited = new GitFlowConfig("trunk", "develop", "feat/", "rel/", "fix/", "ver-");

        AssertDone(await _git.SetGitFlowAsync(_repo.Path, edited, Ct));

        Assert.Equal(edited, Assert.IsType<Success<GitFlowConfig?>>(( await _git.GetGitFlowAsync(_repo.Path, Ct)).Value, exactMatch: false).Value);

        var missing = edited with { Develop = "integration" };
        Assert.NotNull(await _git.GitFlowProblemAsync(_repo.Path, missing, Ct));
        Assert.IsType<Failure>((await _git.SetGitFlowAsync(_repo.Path, missing, Ct)).Value);
        Assert.False(Exists("refs/heads/integration"));
        Assert.Equal("develop", _repo.Git("config", "gitflow.branch.develop").Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("gitflow.branch.master main\n")]
    [InlineData("gitflow.prefix.feature feature/\n")]
    public void Settings_without_both_main_branches_mean_git_flow_is_not_used(string output) =>
        Assert.Null(GitService.ParseGitFlowConfig(output));

    [Fact]
    public async Task A_repository_that_was_not_initialized_has_no_settings()
    {
        Assert.Null(Assert.IsType<Success<GitFlowConfig?>>((await _git.GetGitFlowAsync(_repo.Path, Ct)).Value, exactMatch: false).Value);
    }

    [Fact]
    public void Settings_are_validated()
    {
        Assert.Null(Config.Validate());
        Assert.NotNull((Config with { Develop = "main" }).Validate());
        Assert.NotNull((Config with { FeaturePrefix = "feature" }).Validate());
        Assert.NotNull((Config with { ReleasePrefix = "feature/" }).Validate());
        Assert.NotNull((Config with { Main = "bad name" }).Validate());
        Assert.NotNull((Config with { VersionTagPrefix = "v ?" }).Validate());
    }

    [Fact]
    public void Branches_are_told_apart_by_their_prefix()
    {
        Assert.Equal((GitFlowKind.Feature, "login"), Config.Parse("feature/login"));
        Assert.Equal((GitFlowKind.Release, "1.2.0"), Config.Parse("release/1.2.0"));
        Assert.Equal((GitFlowKind.Hotfix, "x/y"), Config.Parse("hotfix/x/y"));
        Assert.Null(Config.Parse("feature/"));
        Assert.Null(Config.Parse("main"));
    }

    // ---- Init ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Init_creates_the_development_branch_on_main_and_writes_the_settings()
    {
        await InitAsync();

        Assert.Equal(_repo.Head(), _repo.Git("rev-parse", "develop").Trim());
        var read = Assert.IsType<Success<GitFlowConfig?>>((await _git.GetGitFlowAsync(_repo.Path, Ct)).Value, exactMatch: false).Value;
        Assert.Equal(Config, read);
        Assert.Equal("v", _repo.Git("config", "gitflow.prefix.versiontag").Trim());
    }

    [Fact]
    public async Task Init_keeps_an_existing_development_branch_and_can_run_twice()
    {
        _repo.Branch("develop", checkout: true);
        _repo.Commit("on develop", ("d.txt", "d\n"));
        var develop = _repo.Head();
        _repo.Checkout("main");

        await InitAsync();
        await InitAsync();

        Assert.Equal(develop, _repo.Git("rev-parse", "develop").Trim());
    }

    [Fact]
    public async Task Init_refuses_a_missing_production_branch_and_invalid_settings()
    {
        var missing = await _git.GitFlowInitAsync(_repo.Path, Config with { Main = "trunk" }, Ct);
        Assert.Contains("trunk", Assert.IsType<Failure>(missing.Value).Message);

        var same = await _git.GitFlowInitAsync(_repo.Path, Config with { Develop = "main" }, Ct);
        Assert.IsType<Failure>(same.Value);
        Assert.False(Exists("refs/heads/trunk"));
    }

    // ---- Start -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_a_feature_branches_from_develop_and_checks_it_out()
    {
        await InitAsync();
        _repo.Checkout("develop");
        _repo.Commit("on develop", ("d.txt", "d\n"));
        var develop = _repo.Head();
        _repo.Checkout("main");

        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Feature, "login"), Ct));

        Assert.Equal("feature/login", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal(develop, _repo.Head());
    }

    [Fact]
    public async Task Starting_a_hotfix_branches_from_main()
    {
        await InitAsync();
        _repo.Checkout("develop");
        _repo.Commit("on develop", ("d.txt", "d\n"));
        var main = _repo.Git("rev-parse", "main").Trim();

        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Hotfix, "1.0.1"), Ct));

        Assert.Equal("hotfix/1.0.1", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal(main, _repo.Head());
    }

    [Fact]
    public async Task Starting_refuses_an_existing_branch_an_existing_version_tag_and_an_invalid_name()
    {
        await InitAsync();
        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Feature, "login"), Ct));
        _repo.Checkout("develop");
        _repo.Tag("v1.0.0");

        Assert.IsType<Failure>((await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Feature, "login"), Ct)).Value);
        Assert.IsType<Failure>((await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Release, "1.0.0"), Ct)).Value);
        Assert.IsType<Failure>((await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Feature, "bad name"), Ct)).Value);
        Assert.Equal("develop", _repo.Git("branch", "--show-current").Trim());
    }

    // ---- Finish ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Finishing_a_feature_merges_it_into_develop_with_a_merge_commit_and_deletes_it()
    {
        await InitAsync();
        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Feature, "login"), Ct));
        _repo.Commit("login", ("login.txt", "x\n"));

        var finished = AssertFinished(await _git.GitFlowFinishAsync(_repo.Path, Config, new GitFlowFinishRequest(GitFlowKind.Feature, "login", KeepBranch: false), Ct));

        Assert.Equal(["develop"], finished.MergedInto);
        Assert.Null(finished.Tag);
        Assert.True(finished.BranchDeleted);
        Assert.Equal("develop", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal(2, ParentCount("develop"));
        Assert.False(Exists("refs/heads/feature/login"));
        Assert.True(File.Exists(Path.Combine(_repo.Path, "login.txt")));
        Assert.Equal(0, ParentCount("main"));
    }

    [Fact]
    public async Task Finishing_can_keep_the_branch()
    {
        await InitAsync();
        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Feature, "login"), Ct));
        _repo.Commit("login", ("login.txt", "x\n"));

        var finished = AssertFinished(await _git.GitFlowFinishAsync(_repo.Path, Config, new GitFlowFinishRequest(GitFlowKind.Feature, "login", KeepBranch: true), Ct));

        Assert.False(finished.BranchDeleted);
        Assert.True(Exists("refs/heads/feature/login"));
    }

    [Fact]
    public async Task Finishing_a_release_merges_into_main_tags_it_and_merges_the_tag_into_develop()
    {
        await InitAsync();
        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Release, "1.0.0"), Ct));
        _repo.Commit("bump", ("version.txt", "1.0.0\n"));

        var finished = AssertFinished(await _git.GitFlowFinishAsync(_repo.Path, Config, new GitFlowFinishRequest(GitFlowKind.Release, "1.0.0", KeepBranch: false), Ct));

        Assert.Equal(["main", "develop"], finished.MergedInto);
        Assert.Equal("v1.0.0", finished.Tag);
        Assert.Equal("develop", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal(2, ParentCount("main"));
        Assert.Equal("tag", _repo.Git("cat-file", "-t", "v1.0.0").Trim());
        Assert.Equal(_repo.Git("rev-parse", "main").Trim(), _repo.Git("rev-parse", "v1.0.0^{commit}").Trim());
        Assert.Equal("Release 1.0.0", _repo.Git("tag", "-l", "--format=%(contents:subject)", "v1.0.0").Trim());
        Assert.Equal(2, ParentCount("develop"));
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(_repo.Path, "version.txt")).Trim());
        Assert.False(Exists("refs/heads/release/1.0.0"));
    }

    [Fact]
    public async Task Finishing_a_hotfix_reaches_main_and_develop()
    {
        await InitAsync();
        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Hotfix, "1.0.1"), Ct));
        _repo.Commit("fix", ("fix.txt", "fix\n"));

        var finished = AssertFinished(await _git.GitFlowFinishAsync(_repo.Path, Config,
            new GitFlowFinishRequest(GitFlowKind.Hotfix, "1.0.1", KeepBranch: false, TagMessage: "Urgent fix"), Ct));

        Assert.Equal("v1.0.1", finished.Tag);
        Assert.Equal("Urgent fix", _repo.Git("tag", "-l", "--format=%(contents:subject)", "v1.0.1").Trim());
        Assert.True(Exists("refs/tags/v1.0.1"));
        Assert.Equal("fix\n", File.ReadAllText(Path.Combine(_repo.Path, "fix.txt")).ReplaceLineEndings("\n"));
        Assert.Contains("fix.txt", _repo.Git("ls-tree", "--name-only", "main"));
    }

    [Fact]
    public async Task A_conflict_stops_the_finish_with_the_merge_in_progress_and_the_branch_kept()
    {
        await InitAsync();
        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Feature, "login"), Ct));
        _repo.Commit("feature edit", ("a.txt", "feature\n"));
        _repo.Checkout("develop");
        _repo.Commit("develop edit", ("a.txt", "develop\n"));
        _repo.Checkout("feature/login");

        var result = await _git.GitFlowFinishAsync(_repo.Path, Config, new GitFlowFinishRequest(GitFlowKind.Feature, "login", KeepBranch: false), Ct);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Equal(["a.txt"], conflict.Paths);
        Assert.True(File.Exists(Path.Combine(_repo.Path, ".git", "MERGE_HEAD")));
        Assert.True(Exists("refs/heads/feature/login"));
    }

    [Fact]
    public async Task Finishing_refuses_a_missing_branch_and_an_existing_tag_before_touching_anything()
    {
        await InitAsync();

        var missing = await _git.GitFlowFinishAsync(_repo.Path, Config, new GitFlowFinishRequest(GitFlowKind.Feature, "nope", KeepBranch: false), Ct);
        Assert.Contains("feature/nope", Assert.IsType<Failure>(missing.Value).Message);

        AssertDone(await _git.GitFlowStartAsync(_repo.Path, Config, new GitFlowStartRequest(GitFlowKind.Release, "2.0.0"), Ct));
        _repo.Tag("v2.0.0");
        var head = _repo.Head();
        var tagged = await _git.GitFlowFinishAsync(_repo.Path, Config, new GitFlowFinishRequest(GitFlowKind.Release, "2.0.0", KeepBranch: false), Ct);

        Assert.Contains("v2.0.0", Assert.IsType<Failure>(tagged.Value).Message);
        Assert.Equal("release/2.0.0", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal(head, _repo.Head());
    }
}
