using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class FileBytesTests
{
    private readonly GitService _sut = new(new GitCli());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // NUL and high-bit-free characters only: written as UTF-8 by the builder, they stay the same bytes.
    private const string Blob = "BIN\0\u0001\u0002data\0";

    private async Task<byte[]?> Read(TestRepoBuilder repo, FileVersion version) =>
        Assert.IsType<Success<byte[]?>>((await _sut.ReadFileBytesAsync(repo.Path, version, Ct)).Value).Value;

    [Fact]
    public async Task Reads_raw_bytes_from_a_commit_the_index_and_the_disk()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("img/a.png", Blob));
        repo.WriteFile("img/a.png", Blob + "staged").Stage("img/a.png").WriteFile("img/a.png", Blob + "disk");

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Blob), await Read(repo, FileVersion.AtCommit(first, "img/a.png")));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Blob + "staged"), await Read(repo, FileVersion.InIndex("img/a.png")));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Blob + "disk"), await Read(repo, FileVersion.OnDisk("img/a.png")));
    }

    [Fact]
    public async Task Missing_versions_give_null()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.png", Blob));

        Assert.Null(await Read(repo, FileVersion.AtCommit(first, "new.png")));
        Assert.Null(await Read(repo, FileVersion.InIndex("new.png")));
        Assert.Null(await Read(repo, FileVersion.OnDisk("gone.png")));
    }

    [Fact]
    public async Task An_empty_file_gives_empty_bytes()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("first", ("empty.png", ""));

        Assert.Empty(await Read(repo, FileVersion.AtCommit("HEAD", "empty.png")) ?? throw new InvalidOperationException("missing"));
    }
}
