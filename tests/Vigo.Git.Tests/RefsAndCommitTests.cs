using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class RefsAndCommitTests
{
    private readonly GitService _sut = new(new GitCli());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// main tracks origin/main with one commit ahead and one behind; feature has no upstream;
    /// a lightweight tag, an annotated tag and a stash.
    /// </summary>
    private static (TestRepoBuilder Repo, TestRepoBuilder Remote) CreateRichRepository()
    {
        var remote = TestRepoBuilder.Create(bare: true);
        var repo = TestRepoBuilder.Create();
        var a = repo.Commit("a", ("file.txt", "a"));
        repo.Tag("light");
        repo.Commit("b", ("file.txt", "b"));
        repo.Tag("v1.0", "Release 1.0\n\nNotes");
        repo.Git("remote", "add", "origin", remote.Path);
        repo.Git("push", "--quiet", "-u", "origin", "main");
        repo.Git("reset", "--hard", "--quiet", a);
        repo.Commit("c", ("file.txt", "c"));
        repo.Branch("feature", checkout: false);
        repo.WriteFile("file.txt", "dirty");
        repo.Stash("wip");
        return (repo, remote);
    }

    private static RepositoryRefs Success(GitResult<RepositoryRefs> result) =>
        Assert.IsType<Success<RepositoryRefs>>(result.Value).Value;

    [Fact]
    public async Task GetRefs_reports_branches_tracking_tags_and_stashes()
    {
        var (repo, remote) = CreateRichRepository();
        using var _ = repo;
        using var __ = remote;

        var refs = Success(await _sut.GetRefsAsync(repo.Path, Ct));

        Assert.Equal("main", refs.CurrentBranch);
        Assert.Equal(GitSha.Parse(repo.Head()), refs.Head);

        var main = Assert.Single(refs.Refs, r => r.CanonicalName == "refs/heads/main");
        Assert.True(main.IsCurrent);
        Assert.Equal("origin/main", main.Upstream);
        Assert.Equal((1, 1), (main.Ahead, main.Behind));

        var feature = Assert.Single(refs.Refs, r => r.Name == "feature");
        Assert.False(feature.IsCurrent);
        Assert.Null(feature.Upstream);

        var originMain = Assert.Single(refs.OfKind(GitRefKind.RemoteBranch));
        Assert.Equal("origin/main", originMain.Name);
        Assert.Equal("origin", originMain.Remote);

        var tags = refs.OfKind(GitRefKind.Tag).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(["light", "v1.0"], tags.Select(t => t.Name));
        Assert.Null(tags[0].Message);
        Assert.Equal("Release 1.0", tags[1].Message);
        // The annotated tag is peeled to the commit pushed to origin/main.
        Assert.Equal(originMain.Target, tags[1].Target);

        var stash = Assert.Single(refs.OfKind(GitRefKind.Stash));
        Assert.Equal("stash@{0}", stash.Name);
        Assert.Equal("On main: wip", stash.Message);
        // What the graph needs to place it: the commit it was made on and who made it.
        Assert.NotNull(stash.Stash);
        Assert.Equal(refs.OfKind(GitRefKind.LocalBranch).Single(b => b.Name == "feature").Target, stash.Stash.Parent);
        Assert.False(string.IsNullOrEmpty(stash.Stash.AuthorName));
    }

    [Fact]
    public async Task Cli_fallback_matches_libgit2()
    {
        var (repo, remote) = CreateRichRepository();
        using var _ = repo;
        using var __ = remote;

        var fromLibGit2 = Success(await _sut.GetRefsAsync(repo.Path, Ct));
        var fromCli = Success(await _sut.GetRefsWithCliAsync(repo.Path, Ct));

        Assert.Equal(fromLibGit2.Head, fromCli.Head);
        Assert.Equal(fromLibGit2.CurrentBranch, fromCli.CurrentBranch);
        Assert.Equal(
            fromLibGit2.Refs.OrderBy(r => r.CanonicalName, StringComparer.Ordinal),
            fromCli.Refs.OrderBy(r => r.CanonicalName, StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetRefs_on_empty_repository_has_no_head()
    {
        using var repo = TestRepoBuilder.Create("trunk");

        var fromLibGit2 = Success(await _sut.GetRefsAsync(repo.Path, Ct));
        var fromCli = Success(await _sut.GetRefsWithCliAsync(repo.Path, Ct));

        foreach (var refs in new[] { fromLibGit2, fromCli })
        {
            Assert.Empty(refs.Refs);
            Assert.Null(refs.Head);
            Assert.Equal("trunk", refs.CurrentBranch);
        }
    }

    [Fact]
    public async Task GetRefs_with_detached_head_has_no_current_branch()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("one");
        repo.Commit("two");
        repo.Checkout(first);

        foreach (var refs in new[] { Success(await _sut.GetRefsAsync(repo.Path, Ct)), Success(await _sut.GetRefsWithCliAsync(repo.Path, Ct)) })
        {
            Assert.Null(refs.CurrentBranch);
            Assert.Equal(GitSha.Parse(first), refs.Head);
            Assert.DoesNotContain(refs.Refs, r => r.IsCurrent);
        }
    }

    [Fact]
    public async Task GetCommit_lists_changes_against_first_parent()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("keep.txt", "keep"), ("old/name.txt", "same content for rename detection\n"), ("gone.txt", "bye"));
        File.Delete(Path.Combine(repo.Path, "gone.txt"));
        Directory.CreateDirectory(Path.Combine(repo.Path, "new"));
        File.Move(Path.Combine(repo.Path, "old", "name.txt"), Path.Combine(repo.Path, "new", "name.txt"));
        var sha = repo.Commit("Change files\n\nLonger description.", ("keep.txt", "changed"), ("added.txt", "hi"));

        var result = await _sut.GetCommitAsync(repo.Path, GitSha.Parse(sha), Ct);

        var details = Assert.IsType<Success<CommitDetails>>(result.Value).Value;
        Assert.Equal("Change files", details.Summary);
        Assert.Equal("Longer description.", details.Body);
        Assert.Equal("Vigo Test", details.Author.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_060), details.Committer.When);
        Assert.False(details.Signature?.IsSigned);
        Assert.Single(details.Parents);
        var files = details.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                new ChangedFile("added.txt", null, FileChangeKind.Added, 1, 0),
                new ChangedFile("gone.txt", null, FileChangeKind.Deleted, 0, 1),
                new ChangedFile("keep.txt", null, FileChangeKind.Modified, 1, 1),
                new ChangedFile("new/name.txt", "old/name.txt", FileChangeKind.Renamed),
            ],
            files);
    }

    [Fact]
    public async Task GetCommit_of_root_commit_lists_every_file_as_added()
    {
        using var repo = TestRepoBuilder.Create();
        var sha = repo.Commit("root", ("a.txt", "a"), ("dir/b.txt", "b"));

        var result = await _sut.GetCommitAsync(repo.Path, GitSha.Parse(sha), Ct);

        var details = Assert.IsType<Success<CommitDetails>>(result.Value).Value;
        Assert.Empty(details.Parents);
        Assert.Equal(string.Empty, details.Body);
        Assert.All(details.Files, f => Assert.Equal(FileChangeKind.Added, f.Kind));
        Assert.Equal(["a.txt", "dir/b.txt"], details.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetCommit_with_unknown_id_fails()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root");

        var result = await _sut.GetCommitAsync(repo.Path, GitSha.Parse(new string('e', 40)), Ct);

        Assert.IsType<Failure>(result.Value);
    }
}
