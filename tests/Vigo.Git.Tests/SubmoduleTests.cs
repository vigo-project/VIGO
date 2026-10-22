using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class SubmoduleTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo;
    private readonly TestRepoBuilder _dependency;

    public SubmoduleTests()
    {
        _dependency = TestRepoBuilder.Create();
        _dependency.Commit("dep", ("lib.txt", "lib\n"));
        _repo = TestRepoBuilder.Create();
        _repo.Commit("init", ("a.txt", "a\n"));
        TestRepoBuilder.AllowFileSubmodules();
    }

    public void Dispose()
    {
        _repo.Dispose();
        _dependency.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private async Task<IReadOnlyList<Submodule>> ListAsync() =>
        Assert.IsType<Success<IReadOnlyList<Submodule>>>(
            (await _git.GetSubmodulesAsync(_repo.Path, Ct)).Value, exactMatch: false).Value;

    /// <summary>Adds the dependency; committed by default (git refuses to deinitialize a submodule that is only staged).</summary>
    private async Task AddDependencyAsync(string path = "libs/dep", bool commit = true)
    {
        AssertDone(await _git.AddSubmoduleAsync(_repo.Path, _dependency.Path, path, Ct));
        if (commit) _repo.Commit("add " + path);
    }

    [Fact]
    public async Task A_repository_without_gitmodules_has_no_submodule()
    {
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task An_added_submodule_is_listed_up_to_date_with_its_url_and_commit()
    {
        await AddDependencyAsync(commit: false);

        var submodule = Assert.Single(await ListAsync());
        Assert.Equal("libs/dep", submodule.Path);
        Assert.Equal("libs/dep", submodule.Name);
        Assert.Equal("dep", submodule.Label);
        Assert.Equal(SubmoduleState.UpToDate, submodule.State);
        Assert.True(submodule.IsInitialized);
        Assert.Equal(_dependency.Head(), submodule.Head!.Value.ToString());
        Assert.Equal(_dependency.Path.Replace('\\', '/'), submodule.Url!.Replace('\\', '/'));
        Assert.True(File.Exists(Path.Combine(_repo.Path, "libs", "dep", "lib.txt")));
        // Staged, not committed: the user decides.
        Assert.Contains("libs/dep", _repo.Git("diff", "--cached", "--name-only"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_path_the_submodule_takes_the_name_of_the_repository()
    {
        AssertDone(await _git.AddSubmoduleAsync(_repo.Path, _dependency.Path, null, Ct));

        var submodule = Assert.Single(await ListAsync());
        Assert.Equal(Path.GetFileName(_dependency.Path), submodule.Path);
    }

    [Fact]
    public async Task Deinitializing_empties_the_folder_and_updating_clones_it_again()
    {
        await AddDependencyAsync();
        var folder = Path.Combine(_repo.Path, "libs", "dep");

        AssertDone(await _git.DeinitSubmoduleAsync(_repo.Path, "libs/dep", force: false, Ct));

        var deinitialized = Assert.Single(await ListAsync());
        Assert.Equal(SubmoduleState.NotInitialized, deinitialized.State);
        Assert.False(File.Exists(Path.Combine(folder, "lib.txt")));

        AssertDone(await _git.UpdateSubmodulesAsync(_repo.Path, "libs/dep", Ct));

        Assert.Equal(SubmoduleState.UpToDate, Assert.Single(await ListAsync()).State);
        Assert.True(File.Exists(Path.Combine(folder, "lib.txt")));
    }

    [Fact]
    public async Task Updating_all_the_submodules_initializes_every_one()
    {
        await AddDependencyAsync("one");
        await AddDependencyAsync("two");
        AssertDone(await _git.DeinitSubmoduleAsync(_repo.Path, "one", force: false, Ct));
        AssertDone(await _git.DeinitSubmoduleAsync(_repo.Path, "two", force: false, Ct));

        AssertDone(await _git.UpdateSubmodulesAsync(_repo.Path, null, Ct));

        Assert.All(await ListAsync(), s => Assert.Equal(SubmoduleState.UpToDate, s.State));
    }

    [Fact]
    public async Task A_commit_made_inside_the_submodule_makes_it_out_of_sync_until_it_is_updated()
    {
        await AddDependencyAsync();
        var inner = TestRepoBuilder.Attach(Path.Combine(_repo.Path, "libs", "dep"));
        var moved = inner.Commit("local work", ("lib.txt", "changed\n"));

        var submodule = Assert.Single(await ListAsync());
        Assert.Equal(SubmoduleState.OutOfSync, submodule.State);
        Assert.Equal(moved, submodule.Head!.Value.ToString());

        AssertDone(await _git.UpdateSubmodulesAsync(_repo.Path, "libs/dep", Ct));

        var back = Assert.Single(await ListAsync());
        Assert.Equal(SubmoduleState.UpToDate, back.State);
        Assert.Equal(_dependency.Head(), back.Head!.Value.ToString());
    }

    [Fact]
    public async Task Deinitializing_a_modified_submodule_needs_force()
    {
        await AddDependencyAsync();
        File.WriteAllText(Path.Combine(_repo.Path, "libs", "dep", "lib.txt"), "edited\n");

        var refused = await _git.DeinitSubmoduleAsync(_repo.Path, "libs/dep", force: false, Ct);

        var failure = Assert.IsType<Failure>(refused.Value);
        Assert.Equal(FailureReason.SubmoduleNeedsForce, failure.Reason);
        Assert.Contains("modifications locales", failure.Message, StringComparison.Ordinal);

        AssertDone(await _git.DeinitSubmoduleAsync(_repo.Path, "libs/dep", force: true, Ct));
        Assert.Equal(SubmoduleState.NotInitialized, Assert.Single(await ListAsync()).State);
    }

    [Fact]
    public async Task A_submodule_that_is_only_staged_needs_force_to_be_deinitialized()
    {
        await AddDependencyAsync(commit: false);

        var refused = await _git.DeinitSubmoduleAsync(_repo.Path, "libs/dep", force: false, Ct);

        var failure = Assert.IsType<Failure>(refused.Value);
        Assert.Equal(FailureReason.SubmoduleNeedsForce, failure.Reason);
        Assert.Contains("pas encore validé", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_over_a_tracked_path_is_explained()
    {
        await AddDependencyAsync();

        var again = await _git.AddSubmoduleAsync(_repo.Path, _dependency.Path, "libs/dep", Ct);

        Assert.Equal("Ce chemin est déjà suivi par le dépôt.", Assert.IsType<Failure>(again.Value).Message);
    }

    [Fact]
    public async Task Adding_an_unknown_repository_is_explained()
    {
        var missing = Path.Combine(Path.GetTempPath(), "vigo-tests", "no-such-" + Guid.NewGuid().ToString("N")[..8]);

        var result = await _git.AddSubmoduleAsync(_repo.Path, missing, "libs/missing", Ct);

        Assert.IsType<Failure>(result.Value);
    }

    [Theory]
    [InlineData("", "libs/x")]
    [InlineData("-u", "libs/x")]
    [InlineData("https://example.test/x.git", "-x")]
    [InlineData("https://example.test/x.git", "")]
    public async Task Invalid_arguments_are_refused_before_running_git(string url, string path)
    {
        Assert.IsType<Failure>((await _git.AddSubmoduleAsync(_repo.Path, url, path, Ct)).Value);
    }

    [Fact]
    public async Task A_path_starting_with_a_dash_is_refused_for_update_and_deinit()
    {
        Assert.IsType<Failure>((await _git.UpdateSubmodulesAsync(_repo.Path, "-x", Ct)).Value);
        Assert.IsType<Failure>((await _git.DeinitSubmoduleAsync(_repo.Path, "-x", force: true, Ct)).Value);
    }

    [Fact]
    public void The_status_is_parsed_with_its_four_prefixes()
    {
        var sha = new string('a', 40);
        var zeros = new string('0', 40);
        var config = new Dictionary<string, GitService.SubmoduleConfig>
        {
            ["libs/ok"] = new("libs/ok", "https://example.test/ok.git", "main"),
            ["libs/not cloned"] = new("cloned", "https://example.test/nc.git", null),
        };
        var output =
            $" {sha} libs/ok (v1.2-3-gabcdef0)\n" +
            $"-{sha} libs/not cloned\n" +
            $"+{sha} libs/moved (heads/main)\r\n" +
            $"U{zeros} libs/conflict\n";

        var parsed = GitService.ParseSubmodules(output, config);

        Assert.Equal(4, parsed.Count);
        Assert.Equal(new Submodule("libs/ok", "libs/ok", "https://example.test/ok.git", "main", SubmoduleState.UpToDate, GitSha.Parse(sha)), parsed[0]);
        Assert.Equal(SubmoduleState.NotInitialized, parsed[1].State);
        Assert.Equal("libs/not cloned", parsed[1].Path);
        Assert.Equal("cloned", parsed[1].Name);
        Assert.Equal(SubmoduleState.OutOfSync, parsed[2].State);
        Assert.Equal("libs/moved", parsed[2].Path);
        Assert.Null(parsed[2].Url);
        Assert.Equal(SubmoduleState.Conflict, parsed[3].State);
        Assert.Null(parsed[3].Head);
    }

    [Fact]
    public void A_path_that_looks_like_a_description_is_kept_when_it_is_a_known_path()
    {
        var sha = new string('b', 40);
        var config = new Dictionary<string, GitService.SubmoduleConfig> { ["odd (name)"] = new("odd", null, null) };

        var parsed = Assert.Single(GitService.ParseSubmodules($"-{sha} odd (name)\n", config));

        Assert.Equal("odd (name)", parsed.Path);
    }

    [Fact]
    public void The_gitmodules_config_is_parsed_by_path_even_when_the_name_contains_dots()
    {
        var output =
            "submodule.libs/a.b.path\nlibs/a.b\0" +
            "submodule.libs/a.b.url\nhttps://example.test/ab.git\0" +
            "submodule.libs/a.b.branch\ndevelop\0" +
            "submodule.plain.path\nplain\0" +
            "core.unrelated\nvalue\0";

        var parsed = GitService.ParseSubmoduleConfig(output);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(new GitService.SubmoduleConfig("libs/a.b", "https://example.test/ab.git", "develop"), parsed["libs/a.b"]);
        Assert.Equal(new GitService.SubmoduleConfig("plain", null, null), parsed["plain"]);
    }
}
