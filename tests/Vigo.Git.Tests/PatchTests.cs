using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Patch files (J7.12): format-patch / diff written to a file, applied with git am / git apply.</summary>
public sealed class PatchTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _source = TestRepoBuilder.Create();
    private readonly List<TestRepoBuilder> _others = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vigo-patch-" + Guid.NewGuid().ToString("N"));

    public PatchTests()
    {
        Directory.CreateDirectory(_directory);
        _source.Commit("init", ("a.txt", "1\n2\n3\n"), ("b.txt", "b\n"));
    }

    public void Dispose()
    {
        _source.Dispose();
        foreach (var other in _others) other.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Left in the temporary folder.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PatchFile(string name = "x.patch") => Path.Combine(_directory, name);

    /// <summary>A second repository with the history of the source as it is now.</summary>
    private TestRepoBuilder Copy()
    {
        var copy = TestRepoBuilder.Clone(_source.Path, firstTick: 50_000);
        _others.Add(copy);
        return copy;
    }

    private static GitSha Sha(string hex) => GitSha.Parse(hex.Trim());

    private async Task<GitResult<PatchApplyOutcome>> ApplyAsync(TestRepoBuilder repo, string patch) =>
        await _git.ApplyPatchAsync(repo.Path, patch, Ct);

    [Fact]
    public async Task A_commit_patch_replays_as_a_commit_with_the_same_author_message_and_date()
    {
        var copy = Copy();
        _source.Commit("Add c", ("c.txt", "c\n"));
        var patch = PatchFile();

        Assert.IsType<Done>((await _git.CreatePatchFromCommitsAsync(_source.Path, [Sha(_source.Head())], patch, Ct)).Value);
        Assert.StartsWith("From ", await File.ReadAllTextAsync(patch, Ct), StringComparison.Ordinal);
        var outcome = Assert.IsType<Success<PatchApplyOutcome>>((await ApplyAsync(copy, patch)).Value);

        Assert.Equal(PatchApplyOutcome.Committed, outcome.Value);
        Assert.Equal("Add c", copy.Git("log", "-1", "--format=%s").Trim());
        Assert.Equal(_source.Git("log", "-1", "--format=%an|%ae|%aI").Trim(), copy.Git("log", "-1", "--format=%an|%ae|%aI").Trim());
        Assert.Equal("c\n", File.ReadAllText(Path.Combine(copy.Path, "c.txt")));
        Assert.Equal(string.Empty, copy.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task Consecutive_commits_go_in_one_file_whatever_the_order_given()
    {
        var copy = Copy();
        var first = _source.Commit("first", ("c.txt", "1\n"));
        var second = _source.Commit("second", ("d.txt", "2\n"));
        var patch = PatchFile();

        Assert.IsType<Done>((await _git.CreatePatchFromCommitsAsync(_source.Path, [Sha(second), Sha(first)], patch, Ct)).Value);
        Assert.IsType<Success<PatchApplyOutcome>>((await ApplyAsync(copy, patch)).Value);

        Assert.Equal(["second", "first", "init"], copy.Git("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task The_root_commit_has_a_patch()
    {
        var patch = PatchFile();

        Assert.IsType<Done>((await _git.CreatePatchFromCommitsAsync(_source.Path, [Sha(_source.Head())], patch, Ct)).Value);

        using var empty = TestRepoBuilder.Create();
        _others.Add(empty);
        Assert.IsType<Success<PatchApplyOutcome>>((await ApplyAsync(empty, patch)).Value);
        Assert.Equal("init", empty.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Commits_that_do_not_follow_each_other_are_refused_and_nothing_is_written()
    {
        var first = _source.Commit("first", ("c.txt", "1\n"));
        _source.Commit("second", ("d.txt", "2\n"));
        var third = _source.Commit("third", ("e.txt", "3\n"));
        var patch = PatchFile();

        var result = await _git.CreatePatchFromCommitsAsync(_source.Path, [Sha(first), Sha(third)], patch, Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.False(File.Exists(patch));
    }

    [Fact]
    public async Task A_merge_commit_has_no_patch()
    {
        _source.Branch("side");
        _source.Commit("on side", ("s.txt", "s\n"));
        _source.Checkout("main");
        _source.Commit("on main", ("m.txt", "m\n"));
        var merge = _source.Merge("side");
        var patch = PatchFile();

        var result = await _git.CreatePatchFromCommitsAsync(_source.Path, [Sha(merge)], patch, Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.False(File.Exists(patch));
    }

    [Fact]
    public async Task Working_tree_changes_apply_to_another_copy_and_stay_staged()
    {
        var copy = Copy();
        _source.WriteFile("a.txt", "1\nTWO\n3\n");
        var patch = PatchFile("changes.patch");

        Assert.IsType<Done>((await _git.CreatePatchFromWorkingTreeAsync(_source.Path, PatchScope.Unstaged, null, patch, Ct)).Value);
        var outcome = Assert.IsType<Success<PatchApplyOutcome>>((await ApplyAsync(copy, patch)).Value);

        Assert.Equal(PatchApplyOutcome.Applied, outcome.Value);
        Assert.Equal("1\nTWO\n3\n", File.ReadAllText(Path.Combine(copy.Path, "a.txt")));
        // --3way implies --index: the change is in the index too.
        Assert.Contains("M  a.txt", copy.Git("status", "--porcelain"), StringComparison.Ordinal);
        Assert.Equal(_source.Head(), copy.Head());
    }

    [Fact]
    public async Task The_scope_chooses_between_staged_and_unstaged_changes()
    {
        _source.WriteFile("a.txt", "1\nSTAGED\n3\n").Stage("a.txt");
        _source.WriteFile("b.txt", "unstaged\n");

        var staged = PatchFile("staged.patch");
        var unstaged = PatchFile("unstaged.patch");
        var all = PatchFile("all.patch");
        Assert.IsType<Done>((await _git.CreatePatchFromWorkingTreeAsync(_source.Path, PatchScope.Staged, null, staged, Ct)).Value);
        Assert.IsType<Done>((await _git.CreatePatchFromWorkingTreeAsync(_source.Path, PatchScope.Unstaged, null, unstaged, Ct)).Value);
        Assert.IsType<Done>((await _git.CreatePatchFromWorkingTreeAsync(_source.Path, PatchScope.All, null, all, Ct)).Value);

        var stagedText = await File.ReadAllTextAsync(staged, Ct);
        var unstagedText = await File.ReadAllTextAsync(unstaged, Ct);
        var allText = await File.ReadAllTextAsync(all, Ct);
        Assert.Contains("a.txt", stagedText, StringComparison.Ordinal);
        Assert.DoesNotContain("b.txt", stagedText, StringComparison.Ordinal);
        Assert.Contains("b.txt", unstagedText, StringComparison.Ordinal);
        Assert.DoesNotContain("a.txt", unstagedText, StringComparison.Ordinal);
        Assert.Contains("a.txt", allText, StringComparison.Ordinal);
        Assert.Contains("b.txt", allText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_given_paths_go_in_the_patch_even_with_odd_names()
    {
        _source.WriteFile("a.txt", "changed\n").WriteFile("dir with space/é.txt", "x\n");
        _source.Commit("add odd", ("dir with space/é.txt", "x\n"));
        _source.WriteFile("a.txt", "changed again\n").WriteFile("dir with space/é.txt", "y\n");
        var patch = PatchFile();

        Assert.IsType<Done>((await _git.CreatePatchFromWorkingTreeAsync(_source.Path, PatchScope.All, ["dir with space/é.txt"], patch, Ct)).Value);

        var text = await File.ReadAllTextAsync(patch, Ct);
        Assert.Contains("+y", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a.txt", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_binary_file_survives_the_round_trip()
    {
        var copy = Copy();
        var bytes = Enumerable.Range(0, 2_000).Select(i => (byte)(i * 31 % 256)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_source.Path, "data.bin"), bytes, Ct);
        _source.Git("add", "data.bin");
        var patch = PatchFile();

        Assert.IsType<Done>((await _git.CreatePatchFromWorkingTreeAsync(_source.Path, PatchScope.Staged, null, patch, Ct)).Value);
        Assert.IsType<Success<PatchApplyOutcome>>((await ApplyAsync(copy, patch)).Value);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(copy.Path, "data.bin"), Ct));
    }

    [Fact]
    public async Task Without_changes_there_is_no_patch_and_no_file()
    {
        var patch = PatchFile();

        var result = await _git.CreatePatchFromWorkingTreeAsync(_source.Path, PatchScope.All, null, patch, Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.False(File.Exists(patch));
        Assert.False(File.Exists(patch + ".vigo-tmp"));
    }

    [Fact]
    public async Task A_mailbox_that_conflicts_stays_in_progress_until_aborted()
    {
        var copy = Copy();
        _source.Commit("source side", ("a.txt", "1\nSOURCE\n3\n"));
        var patch = PatchFile();
        Assert.IsType<Done>((await _git.CreatePatchFromCommitsAsync(_source.Path, [Sha(_source.Head())], patch, Ct)).Value);
        var before = copy.Head();
        copy.Commit("copy side", ("a.txt", "1\nCOPY\n3\n"));
        var copyHead = copy.Head();

        var result = await ApplyAsync(copy, patch);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Contains("a.txt", conflict.Paths);
        var status = Assert.IsType<Success<WorkingTreeStatus>>((await _git.GetStatusAsync(copy.Path, Ct)).Value).Value;
        Assert.Equal(RepositoryOperationKind.ApplyMailbox, status.Operation?.Kind);
        Assert.Equal(1, status.Operation?.Step);
        Assert.Equal("source side", status.Operation?.Message);

        Assert.IsType<Done>((await _git.AbortOperationAsync(copy.Path, RepositoryOperationKind.ApplyMailbox, Ct)).Value);
        var after = Assert.IsType<Success<WorkingTreeStatus>>((await _git.GetStatusAsync(copy.Path, Ct)).Value).Value;
        Assert.Null(after.Operation);
        Assert.Equal(copyHead, copy.Head());
        Assert.NotEqual(before, copyHead);
    }

    [Fact]
    public async Task A_resolved_mailbox_continues_into_a_commit()
    {
        var copy = Copy();
        _source.Commit("source side", ("a.txt", "1\nSOURCE\n3\n"));
        var patch = PatchFile();
        Assert.IsType<Done>((await _git.CreatePatchFromCommitsAsync(_source.Path, [Sha(_source.Head())], patch, Ct)).Value);
        copy.Commit("copy side", ("a.txt", "1\nCOPY\n3\n"));
        Assert.IsType<Conflict>((await ApplyAsync(copy, patch)).Value);

        copy.WriteFile("a.txt", "1\nBOTH\n3\n").Stage("a.txt");
        var result = await _git.ContinueOperationAsync(copy.Path, RepositoryOperationKind.ApplyMailbox, Ct);

        Assert.IsType<Done>(result.Value);
        Assert.Equal("source side", copy.Git("log", "-1", "--format=%s").Trim());
        Assert.Equal("1\nBOTH\n3\n", File.ReadAllText(Path.Combine(copy.Path, "a.txt")));
    }

    [Fact]
    public async Task A_diff_that_conflicts_leaves_the_conflicts_to_resolve()
    {
        var copy = Copy();
        _source.WriteFile("a.txt", "1\nSOURCE\n3\n");
        var patch = PatchFile();
        Assert.IsType<Done>((await _git.CreatePatchFromWorkingTreeAsync(_source.Path, PatchScope.All, null, patch, Ct)).Value);
        copy.Commit("copy side", ("a.txt", "1\nCOPY\n3\n"));

        var result = await ApplyAsync(copy, patch);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Equal(["a.txt"], conflict.Paths);
        Assert.Contains("<<<<<<<", File.ReadAllText(Path.Combine(copy.Path, "a.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_that_does_not_apply_fails_without_leaving_anything_behind()
    {
        var copy = Copy();
        _source.Commit("source side", ("a.txt", "1\nSOURCE\n3\n"));
        var patch = PatchFile();
        Assert.IsType<Done>((await _git.CreatePatchFromCommitsAsync(_source.Path, [Sha(_source.Head())], patch, Ct)).Value);
        // The file the patch changes is gone, and the change is not mergeable.
        copy.Git("rm", "--quiet", "a.txt");
        copy.Git("commit", "--quiet", "-m", "remove a");
        var head = copy.Head();

        var result = await ApplyAsync(copy, patch);

        Assert.True(result.Value is Failure or Conflict);
        var status = Assert.IsType<Success<WorkingTreeStatus>>((await _git.GetStatusAsync(copy.Path, Ct)).Value).Value;
        if (result.Value is Failure) Assert.Null(status.Operation);
        Assert.Equal(head, copy.Head());
    }

    [Fact]
    public async Task A_file_that_is_not_a_patch_is_refused()
    {
        var notPatch = PatchFile("note.patch");
        await File.WriteAllTextAsync(notPatch, "just some text\n", Ct);

        var result = await ApplyAsync(_source, notPatch);

        Assert.IsType<Failure>(result.Value);
        Assert.Equal(string.Empty, _source.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task A_missing_file_is_refused()
    {
        var result = await ApplyAsync(_source, PatchFile("nope.patch"));

        Assert.IsType<Failure>(result.Value);
    }

    [Fact]
    public void The_chain_of_commits_is_found_in_order_or_refused()
    {
        var a = Sha("1111111111111111111111111111111111111111");
        var b = Sha("2222222222222222222222222222222222222222");
        var c = Sha("3333333333333333333333333333333333333333");
        var d = Sha("4444444444444444444444444444444444444444");
        var root = Sha("0000000000000000000000000000000000000001");
        var parents = new Dictionary<GitSha, GitSha[]> { [a] = [root], [b] = [a], [c] = [b], [d] = [b] };

        Assert.Equal([a, b, c], GitService.OrderChain([c, a, b], parents));
        Assert.Equal([b], GitService.OrderChain([b], parents));
        // A gap, a fork (c and d both children of b) and an unknown commit.
        Assert.Null(GitService.OrderChain([a, c], parents));
        Assert.Null(GitService.OrderChain([b, c, d], parents));
        Assert.Null(GitService.OrderChain([root], parents));
    }
}
