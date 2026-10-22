using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class FileTextTests
{
    private readonly GitService _sut = new(new GitCli());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string?> Read(TestRepoBuilder repo, FileVersion version) =>
        Assert.IsType<Success<string?>>((await _sut.ReadFileTextAsync(repo.Path, version, Ct)).Value).Value;

    [Fact]
    public async Task Reads_a_file_from_a_commit_the_index_and_the_disk()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("dir/a.txt", "one\n"));
        repo.Commit("second", ("dir/a.txt", "two\n"));
        repo.WriteFile("dir/a.txt", "staged\n").Stage("dir/a.txt").WriteFile("dir/a.txt", "on disk\n");

        Assert.Equal("one\n", await Read(repo, FileVersion.AtCommit(first, "dir/a.txt")));
        Assert.Equal("two\n", await Read(repo, FileVersion.AtCommit("HEAD", "dir/a.txt")));
        Assert.Equal("staged\n", await Read(repo, FileVersion.InIndex("dir/a.txt")));
        Assert.Equal("on disk\n", await Read(repo, FileVersion.OnDisk("dir/a.txt")));
    }

    [Fact]
    public async Task Missing_versions_give_null()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.txt", "a\n"));
        repo.WriteFile("new.txt", "untracked\n");

        Assert.Null(await Read(repo, FileVersion.AtCommit(first, "new.txt")));
        Assert.Null(await Read(repo, FileVersion.InIndex("new.txt")));
        Assert.Null(await Read(repo, FileVersion.OnDisk("gone.txt")));
        Assert.Equal("untracked\n", await Read(repo, FileVersion.OnDisk("new.txt")));
    }

    [Fact]
    public async Task Text_without_final_newline_and_with_crlf_is_kept_as_is()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Git("config", "core.autocrlf", "false");
        repo.Commit("first", ("a.txt", "x\r\ny"), ("empty.txt", ""));

        Assert.Equal("x\r\ny", await Read(repo, FileVersion.AtCommit("HEAD", "a.txt")));
        Assert.Equal("", await Read(repo, FileVersion.InIndex("empty.txt")));
    }

    [Fact]
    public async Task A_blob_over_the_limit_is_refused()
    {
        using var repo = TestRepoBuilder.Create();
        var sha = repo.Commit("big", ("big.txt", new string('x', (int)IGitService.MaxFileTextBytes + 1)));

        var result = await _sut.ReadFileTextAsync(repo.Path, FileVersion.AtCommit(sha, "big.txt"), Ct);

        Assert.IsType<Failure>(result.Value);
    }

    [Fact]
    public async Task Reads_from_a_subdirectory_with_top_level_paths()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("first", ("sub/b.txt", "b\n"));

        var result = await _sut.ReadFileTextAsync(Path.Combine(repo.Path, "sub"), FileVersion.InIndex("sub/b.txt"), Ct);

        Assert.Equal("b\n", Assert.IsType<Success<string?>>(result.Value).Value);
    }
}
