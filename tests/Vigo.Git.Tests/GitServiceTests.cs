using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class GitServiceTests
{
    private readonly GitService _sut = new(new GitCli());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetGitVersion_returns_installed_version()
    {
        var result = await _sut.GetGitVersionAsync(Ct);

        var version = Assert.IsType<Success<string>>(result.Value).Value;
        Assert.Matches(@"^\d+\.\d+", version);
    }

    [Fact]
    public async Task Open_repository_with_commits_reports_branch_and_head()
    {
        using var repo = TestRepoBuilder.Create("main");
        var sha = repo.Commit("initial", ("README.md", "# test"));

        var result = await _sut.OpenAsync(repo.Path, Ct);

        var info = Assert.IsType<Success<RepositoryInfo>>(result.Value).Value;
        Assert.Equal("main", info.HeadName);
        Assert.Equal(sha, info.HeadSha);
        Assert.False(info.IsEmpty);
        Assert.False(info.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(repo.Path), info.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public async Task Open_from_subdirectory_discovers_repository_root()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("initial", ("src/app/file.txt", "x"));

        var result = await _sut.OpenAsync(Path.Combine(repo.Path, "src", "app"), Ct);

        var info = Assert.IsType<Success<RepositoryInfo>>(result.Value).Value;
        Assert.Equal(Path.GetFileName(repo.Path), info.Name);
    }

    [Fact]
    public async Task Open_empty_repository_reports_unborn_head()
    {
        using var repo = TestRepoBuilder.Create("trunk");

        var result = await _sut.OpenAsync(repo.Path, Ct);

        var info = Assert.IsType<Success<RepositoryInfo>>(result.Value).Value;
        Assert.True(info.IsEmpty);
        Assert.Null(info.HeadSha);
    }

    [Fact]
    public async Task Open_detached_head_reports_short_sha()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("one");
        repo.Commit("two");
        repo.Checkout(first);

        var result = await _sut.OpenAsync(repo.Path, Ct);

        var info = Assert.IsType<Success<RepositoryInfo>>(result.Value).Value;
        Assert.True(info.IsHeadDetached);
        Assert.Equal(first[..7], info.HeadName);
    }

    [Fact]
    public async Task Open_missing_directory_fails()
    {
        var result = await _sut.OpenAsync(@"Z:\does\not\exist", Ct);

        Assert.IsType<Failure>(result.Value);
    }

    [Fact]
    public async Task Init_creates_repository_on_requested_branch()
    {
        using var holder = TestRepoBuilder.Create();
        var target = Path.Combine(holder.Path, "nested-new");

        var result = await _sut.InitAsync(target, "develop", Ct);

        var info = Assert.IsType<Success<RepositoryInfo>>(result.Value).Value;
        Assert.True(info.IsEmpty);
        Assert.Equal("develop", info.HeadName);
        Assert.True(Directory.Exists(Path.Combine(target, ".git")));
    }

    [Fact]
    public async Task GetIdentity_reads_repository_config()
    {
        using var repo = TestRepoBuilder.Create();

        var result = await _sut.GetIdentityAsync(repo.Path, Ct);

        var identity = Assert.IsType<Success<GitIdentity>>(result.Value).Value;
        Assert.Equal("Vigo Test", identity.Name);
        Assert.Equal("test@vigo.dev", identity.Email);
        Assert.Equal("VT", identity.Initials);
    }
}
