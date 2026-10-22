using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class InteractiveRebaseTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T AssertSuccess<T>(GitResult<T> result)
    {
        Assert.True(result.Value is Success<T>, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());
        return ((Success<T>)result.Value!).Value;
    }

    /// <summary>Subjects of HEAD's history, newest first, down to (not including) <paramref name="since"/>.</summary>
    private static string[] Subjects(TestRepoBuilder repo, string since) =>
        repo.Git("log", "--format=%s", $"{since}..HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>main: base, then one, two, three (each adding its own file).</summary>
    private static TestRepoBuilder Linear(out string baseSha, out GitSha[] commits)
    {
        var repo = TestRepoBuilder.Create();
        repo.Git("config", "rebase.autoStash", "false");
        repo.Git("config", "commit.gpgSign", "false");
        baseSha = repo.Commit("base", ("base.txt", "base\n"));
        commits =
        [
            GitSha.Parse(repo.Commit("one", ("one.txt", "1\n"))),
            GitSha.Parse(repo.Commit("two", ("two.txt", "2\n"))),
            GitSha.Parse(repo.Commit("three", ("three.txt", "3\n"))),
        ];
        return repo;
    }

    [Fact]
    public async Task Lists_the_commits_to_replay_oldest_first()
    {
        using var repo = Linear(out var baseSha, out var commits);
        repo.Commit("four\n\nwith a body", ("four.txt", "4\n"));

        var listed = AssertSuccess(await _git.GetRebaseCommitsAsync(repo.Path, baseSha, Ct));

        Assert.Equal(["one", "two", "three", "four"], listed.Select(c => c.Subject));
        Assert.Equal(commits[0], listed[0].Sha);
        Assert.Equal("four\n\nwith a body", listed[3].Message);
        Assert.All(listed, c => Assert.False(c.IsMerge));
        Assert.All(listed, c => Assert.NotEqual(DateTimeOffset.MinValue, c.AuthorDate));
    }

    [Fact]
    public async Task Flags_merge_commits()
    {
        using var repo = Linear(out var baseSha, out _);
        repo.Branch("side");
        repo.Commit("side", ("side.txt", "s\n"));
        repo.Checkout("main");
        repo.Git("merge", "--no-ff", "--no-edit", "--quiet", "side");

        var listed = AssertSuccess(await _git.GetRebaseCommitsAsync(repo.Path, baseSha, Ct));

        Assert.Single(listed, c => c.IsMerge);
    }

    [Fact]
    public async Task Reorders_rewords_squashes_and_drops()
    {
        using var repo = Linear(out var baseSha, out var c);

        var outcome = AssertSuccess(await _git.InteractiveRebaseAsync(repo.Path, baseSha,
        [
            new RebaseTodoItem(c[2], RebaseAction.Reword, "three, reworded\n\nbody # kept"),
            new RebaseTodoItem(c[0], RebaseAction.Pick),
            new RebaseTodoItem(c[1], RebaseAction.Fixup),
        ], Ct));

        Assert.Equal(RebaseOutcome.Rebased, outcome);
        Assert.Equal(["one", "three, reworded"], Subjects(repo, baseSha));
        Assert.Equal("three, reworded\n\nbody # kept", repo.Git("log", "-1", "--format=%B", "HEAD~1").Trim());
        // The fixup melded two into one: both files are in HEAD.
        Assert.Contains("two.txt", repo.Git("show", "--name-only", "--format=", "HEAD"), StringComparison.Ordinal);
        Assert.Null(AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct)).Operation);

        using var other = Linear(out var otherBase, out var d);
        Assert.Equal(RebaseOutcome.Rebased, AssertSuccess(await _git.InteractiveRebaseAsync(other.Path, otherBase,
        [
            new RebaseTodoItem(d[0], RebaseAction.Pick),
            new RebaseTodoItem(d[1], RebaseAction.Drop),
            new RebaseTodoItem(d[2], RebaseAction.Squash),
        ], Ct)));
        Assert.Equal(["one"], Subjects(other, otherBase));
        Assert.DoesNotContain("two.txt", other.Git("ls-files"), StringComparison.Ordinal);
        Assert.Contains("three", other.Git("log", "-1", "--format=%B"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stops_on_an_edit_step_until_continued()
    {
        using var repo = Linear(out var baseSha, out var c);

        var outcome = AssertSuccess(await _git.InteractiveRebaseAsync(repo.Path, baseSha,
        [
            new RebaseTodoItem(c[0], RebaseAction.Pick),
            new RebaseTodoItem(c[1], RebaseAction.Edit),
            new RebaseTodoItem(c[2], RebaseAction.Pick),
        ], Ct));

        Assert.Equal(RebaseOutcome.Stopped, outcome);
        var operation = AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct)).Operation;
        Assert.Equal(RepositoryOperationKind.Rebase, operation?.Kind);
        Assert.True(operation?.IsEditStop);
        Assert.Equal(c[1].ToString(), repo.Head());

        Assert.IsType<Done>((await _git.ContinueOperationAsync(repo.Path, RepositoryOperationKind.Rebase, Ct)).Value);
        Assert.Null(AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct)).Operation);
        Assert.Equal(["three", "two", "one"], Subjects(repo, baseSha));
    }

    [Fact]
    public async Task Stops_on_conflicts()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Git("config", "commit.gpgSign", "false");
        var baseSha = repo.Commit("base", ("a.txt", "0\n"));
        var one = GitSha.Parse(repo.Commit("one", ("a.txt", "1\n")));
        var two = GitSha.Parse(repo.Commit("two", ("a.txt", "2\n")));

        var result = await _git.InteractiveRebaseAsync(repo.Path, baseSha,
            [new RebaseTodoItem(two, RebaseAction.Pick), new RebaseTodoItem(one, RebaseAction.Pick)], Ct);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Equal(["a.txt"], conflict.Paths);
        var operation = AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct)).Operation;
        Assert.False(operation?.IsEditStop);
        Assert.IsType<Done>((await _git.AbortOperationAsync(repo.Path, RepositoryOperationKind.Rebase, Ct)).Value);
        Assert.Equal(two.ToString(), repo.Head());
    }

    [Fact]
    public async Task Refuses_a_list_that_cannot_be_applied()
    {
        using var repo = Linear(out var baseSha, out var c);
        var head = repo.Head();

        Assert.IsType<Failure>((await _git.InteractiveRebaseAsync(repo.Path, baseSha,
            [new RebaseTodoItem(c[0], RebaseAction.Fixup), new RebaseTodoItem(c[1], RebaseAction.Pick), new RebaseTodoItem(c[2], RebaseAction.Pick)], Ct)).Value);
        Assert.IsType<Failure>((await _git.InteractiveRebaseAsync(repo.Path, baseSha,
            [.. c.Select(sha => new RebaseTodoItem(sha, RebaseAction.Drop))], Ct)).Value);
        Assert.Equal(head, repo.Head());
    }
}
