using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class WorktreeTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo;
    private readonly List<string> _folders = [];

    public WorktreeTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Commit("init", ("a.txt", "a\n"));
        _repo.Branch("topic", checkout: false);
    }

    public void Dispose()
    {
        _repo.Dispose();
        foreach (var folder in _folders)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { /* temp folder, cleaned by the system later */ }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string NewFolder(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "vigo-tests", "wt-" + Guid.NewGuid().ToString("N")[..8], name);
        _folders.Add(Path.GetDirectoryName(folder)!);
        return folder;
    }

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private async Task<IReadOnlyList<Worktree>> ListAsync() =>
        Assert.IsType<Success<IReadOnlyList<Worktree>>>(
            (await _git.GetWorktreesAsync(_repo.Path, Ct)).Value, exactMatch: false).Value;

    [Fact]
    public async Task A_repository_has_only_its_main_worktree()
    {
        var worktrees = await ListAsync();

        var main = Assert.Single(worktrees);
        Assert.True(main.IsMain);
        Assert.Equal("main", main.Branch);
        Assert.Equal(_repo.Head(), main.Head!.Value.ToString());
        Assert.False(main.IsLocked);
        Assert.False(main.IsPrunable);
    }

    [Fact]
    public async Task Adding_a_worktree_on_an_existing_branch_lists_it_after_the_main_one()
    {
        var folder = NewFolder("topic-wt");

        AssertDone(await _git.AddWorktreeAsync(_repo.Path, new AddWorktreeRequest(folder, Branch: "topic"), Ct));

        var worktrees = await ListAsync();
        Assert.Equal(2, worktrees.Count);
        var added = worktrees[1];
        Assert.False(added.IsMain);
        Assert.Equal("topic-wt", added.Name);
        Assert.Equal("topic", added.Branch);
        Assert.True(File.Exists(Path.Combine(folder, "a.txt")));
    }

    [Fact]
    public async Task Adding_a_worktree_with_a_new_branch_creates_the_branch_at_the_commit()
    {
        _repo.Commit("second", ("b.txt", "b\n"));
        var first = _repo.Git("rev-parse", "HEAD~1").Trim();
        var folder = NewFolder("feature-wt");

        AssertDone(await _git.AddWorktreeAsync(_repo.Path,
            new AddWorktreeRequest(folder, NewBranch: "feature/x", Commit: GitSha.Parse(first)), Ct));

        Assert.Equal(first, _repo.Git("rev-parse", "feature/x").Trim());
        var added = (await ListAsync())[1];
        Assert.Equal("feature/x", added.Branch);
        Assert.False(File.Exists(Path.Combine(folder, "b.txt")));
    }

    [Fact]
    public async Task Adding_a_detached_worktree_checks_out_the_commit()
    {
        var folder = NewFolder("detached-wt");

        AssertDone(await _git.AddWorktreeAsync(_repo.Path, new AddWorktreeRequest(folder, Commit: GitSha.Parse(_repo.Head())), Ct));

        var added = (await ListAsync())[1];
        Assert.True(added.IsDetached);
        Assert.Null(added.Branch);
        Assert.Equal(_repo.Head(), added.Head!.Value.ToString());
    }

    [Fact]
    public async Task A_branch_checked_out_elsewhere_is_refused_with_an_explanation()
    {
        var result = await _git.AddWorktreeAsync(_repo.Path, new AddWorktreeRequest(NewFolder("again"), Branch: "main"), Ct);

        var failure = Assert.IsType<Failure>(result.Value);
        Assert.Contains("déjà extraite", failure.Message, StringComparison.Ordinal);
        Assert.Single(await ListAsync());
    }

    [Fact]
    public async Task An_invalid_new_branch_name_is_refused_before_git_runs()
    {
        var result = await _git.AddWorktreeAsync(_repo.Path, new AddWorktreeRequest(NewFolder("bad"), NewBranch: "a..b", Commit: GitSha.Parse(_repo.Head())), Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.Single(await ListAsync());
    }

    [Fact]
    public async Task Removing_a_clean_worktree_deletes_its_folder_and_keeps_the_branch()
    {
        var folder = NewFolder("clean-wt");
        AssertDone(await _git.AddWorktreeAsync(_repo.Path, new AddWorktreeRequest(folder, Branch: "topic"), Ct));

        AssertDone(await _git.RemoveWorktreeAsync(_repo.Path, folder, force: false, Ct));

        Assert.Single(await ListAsync());
        Assert.False(Directory.Exists(folder));
        Assert.Contains("topic", _repo.Git("branch", "--list", "topic"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worktree_with_untracked_files_needs_force()
    {
        var folder = NewFolder("dirty-wt");
        AssertDone(await _git.AddWorktreeAsync(_repo.Path, new AddWorktreeRequest(folder, Branch: "topic"), Ct));
        File.WriteAllText(Path.Combine(folder, "scratch.txt"), "x");

        var refused = await _git.RemoveWorktreeAsync(_repo.Path, folder, force: false, Ct);

        var failure = Assert.IsType<Failure>(refused.Value);
        Assert.Equal(FailureReason.WorktreeNeedsForce, failure.Reason);
        Assert.True(Directory.Exists(folder));

        AssertDone(await _git.RemoveWorktreeAsync(_repo.Path, folder, force: true, Ct));
        Assert.False(Directory.Exists(folder));
        Assert.Single(await ListAsync());
    }

    [Fact]
    public async Task A_worktree_whose_folder_was_deleted_is_prunable_until_pruned()
    {
        var folder = NewFolder("gone-wt");
        AssertDone(await _git.AddWorktreeAsync(_repo.Path, new AddWorktreeRequest(folder, Branch: "topic"), Ct));
        Directory.Delete(folder, recursive: true);

        var listed = (await ListAsync())[1];
        Assert.True(listed.IsPrunable);

        AssertDone(await _git.PruneWorktreesAsync(_repo.Path, Ct));
        Assert.Single(await ListAsync());
    }

    [Fact]
    public async Task A_locked_worktree_reports_its_reason_and_needs_force_to_be_removed()
    {
        var folder = NewFolder("locked-wt");
        AssertDone(await _git.AddWorktreeAsync(_repo.Path, new AddWorktreeRequest(folder, Branch: "topic"), Ct));
        _repo.Git("worktree", "lock", "--reason", "on a usb drive", folder);

        var listed = (await ListAsync())[1];
        Assert.True(listed.IsLocked);
        Assert.Equal("on a usb drive", listed.LockReason);

        var refused = await _git.RemoveWorktreeAsync(_repo.Path, folder, force: false, Ct);
        Assert.Equal(FailureReason.WorktreeNeedsForce, Assert.IsType<Failure>(refused.Value).Reason);
        AssertDone(await _git.RemoveWorktreeAsync(_repo.Path, folder, force: true, Ct));
    }

    [Fact]
    public void The_porcelain_output_is_parsed_field_by_field()
    {
        var sha = new string('a', 40);
        var output = string.Join('\0',
            "worktree C:/work/repo", $"HEAD {sha}", "branch refs/heads/main", "",
            "worktree C:/work/repo-wt", $"HEAD {sha}", "detached", "locked", "",
            "worktree C:/work/gone", $"HEAD {sha}", "branch refs/heads/old", "prunable gitdir file points to non-existent location", "", "");

        var parsed = GitService.ParseWorktrees(output);

        Assert.Equal(3, parsed.Count);
        Assert.True(parsed[0].IsMain);
        Assert.Equal("main", parsed[0].Branch);
        Assert.True(parsed[1].IsDetached);
        Assert.Equal(string.Empty, parsed[1].LockReason);
        Assert.True(parsed[1].IsLocked);
        Assert.Equal("repo-wt", parsed[1].Name);
        Assert.StartsWith("gitdir file", parsed[2].PruneReason, StringComparison.Ordinal);
    }
}
