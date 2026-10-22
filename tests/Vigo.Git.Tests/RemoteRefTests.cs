using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Pushing and deleting single branches and tags of a remote; adding, renaming and removing remotes.</summary>
public sealed class RemoteRefTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _remote;
    private readonly TestRepoBuilder _mine;

    public RemoteRefTests()
    {
        _remote = TestRepoBuilder.Create(bare: true);
        _mine = TestRepoBuilder.Create();
        _mine.Commit("init", ("a.txt", "1\n"));
        _mine.Git("remote", "add", "origin", _remote.Path);
        _mine.Git("push", "--quiet", "--set-upstream", "origin", "main");
    }

    public void Dispose()
    {
        _mine.Dispose();
        _remote.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private string[] RemoteRefs() =>
        _remote.Git("for-each-ref", "--format=%(refname)").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Pushes_a_branch_that_is_not_checked_out_and_sets_its_upstream()
    {
        _mine.Branch("feature", checkout: false);

        AssertDone(await _git.PushRefAsync(_mine.Path, new RefPush("origin", "refs/heads/feature", "refs/heads/feature", SetUpstream: true), null, Ct));

        Assert.Contains("refs/heads/feature", RemoteRefs());
        Assert.Equal("origin/feature", _mine.Git("rev-parse", "--abbrev-ref", "feature@{upstream}").Trim());
        Assert.Equal("main", _mine.Git("branch", "--show-current").Trim());
    }

    [Fact]
    public async Task Pushes_and_deletes_a_tag()
    {
        _mine.Tag("v1", "Release 1");

        AssertDone(await _git.PushRefAsync(_mine.Path, new RefPush("origin", "refs/tags/v1", "refs/tags/v1"), null, Ct));
        Assert.Contains("refs/tags/v1", RemoteRefs());

        AssertDone(await _git.PushRefAsync(_mine.Path, new RefPush("origin", null, "refs/tags/v1"), null, Ct));
        Assert.DoesNotContain("refs/tags/v1", RemoteRefs());
    }

    [Fact]
    public async Task A_tag_already_on_the_remote_elsewhere_is_explained()
    {
        _mine.Tag("v1");
        _mine.Git("push", "--quiet", "origin", "v1");
        _mine.Commit("two", ("a.txt", "2\n"));
        _mine.Git("tag", "--force", "v1");

        var result = await _git.PushRefAsync(_mine.Path, new RefPush("origin", "refs/tags/v1", "refs/tags/v1"), null, Ct);

        Assert.Equal("Le tag v1 existe déjà sur origin, sur un autre commit.", Assert.IsType<Failure>(result.Value).Message);
    }

    [Fact]
    public async Task Deletes_a_remote_branch_and_its_remote_tracking_branch()
    {
        _mine.Branch("feature", checkout: false);
        _mine.Git("push", "--quiet", "origin", "feature");

        AssertDone(await _git.PushRefAsync(_mine.Path, new RefPush("origin", null, "refs/heads/feature"), null, Ct));

        Assert.DoesNotContain("refs/heads/feature", RemoteRefs());
        Assert.DoesNotContain("origin/feature", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_branch_already_gone_on_the_remote_succeeds_and_prunes_it()
    {
        _mine.Branch("gone", checkout: false);
        _mine.Git("push", "--quiet", "origin", "gone");
        _remote.Git("update-ref", "-d", "refs/heads/gone");

        AssertDone(await _git.PushRefAsync(_mine.Path, new RefPush("origin", null, "refs/heads/gone"), null, Ct));

        Assert.DoesNotContain("origin/gone", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Option_like_remotes_and_partial_names_are_refused()
    {
        Assert.IsType<Failure>((await _git.PushRefAsync(_mine.Path, new RefPush("--mirror", "refs/heads/main", "refs/heads/main"), null, Ct)).Value);
        Assert.IsType<Failure>((await _git.PushRefAsync(_mine.Path, new RefPush("origin", "main", "refs/heads/main"), null, Ct)).Value);
        Assert.IsType<Failure>((await _git.AddRemoteAsync(_mine.Path, "-x", "https://example.com/a.git", Ct)).Value);
        Assert.IsType<Failure>((await _git.AddRemoteAsync(_mine.Path, "x", "--upload-pack=evil", Ct)).Value);
    }

    [Fact]
    public async Task Adds_renames_and_removes_a_remote()
    {
        AssertDone(await _git.AddRemoteAsync(_mine.Path, "upstream", "https://example.com/atlas/api.git", Ct));
        var again = await _git.AddRemoteAsync(_mine.Path, "upstream", "https://example.com/other.git", Ct);
        Assert.Equal("Le distant upstream existe déjà.", Assert.IsType<Failure>(again.Value).Message);

        AssertDone(await _git.RenameRemoteAsync(_mine.Path, "origin", "github", Ct));
        Assert.Equal("github/main", _mine.Git("rev-parse", "--abbrev-ref", "main@{upstream}").Trim());

        AssertDone(await _git.RemoveRemoteAsync(_mine.Path, "upstream", Ct));
        var remotes = Assert.IsType<Success<IReadOnlyList<GitRemote>>>((await _git.GetRemotesAsync(_mine.Path, Ct)).Value).Value;
        Assert.Equal(["github"], remotes.Select(r => r.Name));
    }
}
