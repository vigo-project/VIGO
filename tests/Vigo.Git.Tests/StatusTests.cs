using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.Git.Status;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class StatusTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<WorkingTreeStatus> StatusAsync(TestRepoBuilder repo)
    {
        var result = await _git.GetStatusAsync(repo.Path, Ct);
        return Assert.IsType<Success<WorkingTreeStatus>>(result.Value).Value;
    }

    [Fact]
    public async Task Clean_repository_has_no_entry_and_reports_the_branch()
    {
        using var repo = TestRepoBuilder.Create();
        var head = repo.Commit("init", ("a.txt", "a\n"));

        var status = await StatusAsync(repo);

        Assert.True(status.IsClean);
        Assert.Equal("main", status.Branch);
        Assert.Equal(head, status.Head?.ToString());
        Assert.Null(status.Upstream);
    }

    [Fact]
    public async Task Staged_unstaged_untracked_renamed_and_deleted_files_are_classified()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"), ("b.txt", "b\n"), ("c.txt", "c\n"), ("d.txt", "d\n"));

        repo.WriteFile("a.txt", "a\na2\n").Stage("a.txt").WriteFile("a.txt", "a\na2\na3\n"); // partially staged
        repo.Git("mv", "b.txt", "renamed.txt");                                                 // staged rename
        repo.DeleteFile("c.txt");                                                               // unstaged deletion
        repo.WriteFile("d.txt", "d2\n");                                                        // unstaged change
        repo.WriteFile("dir/sub/new file é.txt", "n\n");                                        // untracked, nested

        var status = await StatusAsync(repo);

        Assert.Equal(["a.txt", "c.txt", "d.txt", "dir/sub/new file é.txt", "renamed.txt"], status.Entries.Select(e => e.Path));
        var a = status.Entries[0];
        Assert.Equal((FileChangeKind.Modified, FileChangeKind.Modified), (a.IndexChange, a.WorkTreeChange));
        Assert.True(a.IsPartiallyStaged);
        Assert.Equal((null, FileChangeKind.Deleted), (status.Entries[1].IndexChange, status.Entries[1].WorkTreeChange));
        Assert.Equal((null, FileChangeKind.Modified), (status.Entries[2].IndexChange, status.Entries[2].WorkTreeChange));
        var untracked = status.Entries[3];
        Assert.True(untracked.IsUntracked);
        Assert.Equal(FileChangeKind.Added, untracked.WorkTreeChange);
        var renamed = status.Entries[4];
        Assert.Equal(FileChangeKind.Renamed, renamed.IndexChange);
        Assert.Equal("b.txt", renamed.OldPath);
        Assert.Null(renamed.WorkTreeChange);
    }

    [Fact]
    public async Task Ignored_files_are_left_out()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", (".gitignore", "bin/\n"));
        repo.WriteFile("bin/out.dll", "x");

        Assert.True((await StatusAsync(repo)).IsClean);
    }

    [Fact]
    public async Task Repository_without_commits_reports_no_head()
    {
        using var repo = TestRepoBuilder.Create();
        repo.WriteFile("a.txt", "a\n").Stage("a.txt");

        var status = await StatusAsync(repo);

        Assert.Null(status.Head);
        Assert.Equal("main", status.Branch);
        Assert.Equal(FileChangeKind.Added, Assert.Single(status.Entries).IndexChange);
    }

    [Fact]
    public async Task Detached_head_and_upstream_tracking_are_reported()
    {
        using var origin = TestRepoBuilder.Create();
        origin.Commit("one", ("a.txt", "1\n"));
        using var repo = TestRepoBuilder.Create();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("fetch", "--quiet", "origin");
        repo.Git("checkout", "--quiet", "-B", "main", "--track", "origin/main");
        repo.Commit("two", ("a.txt", "2\n"));

        var tracking = await StatusAsync(repo);
        Assert.Equal("origin/main", tracking.Upstream);
        Assert.Equal((1, 0), (tracking.Ahead, tracking.Behind));

        repo.Git("checkout", "--quiet", "--detach");
        var detached = await StatusAsync(repo);
        Assert.Null(detached.Branch);
        Assert.NotNull(detached.Head);
    }

    [Fact]
    public async Task Merge_conflicts_are_reported_as_unmerged()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("a.txt", "base\n"), ("gone.txt", "g\n"));
        repo.Branch("other").Commit("other", ("a.txt", "other\n"), ("gone.txt", "changed\n"));
        repo.Checkout("main");
        repo.DeleteFile("gone.txt");
        repo.Commit("main", ("a.txt", "main\n"));
        Assert.ThrowsAny<Exception>(() => repo.Git("merge", "--quiet", "other"));

        var status = await StatusAsync(repo);

        Assert.Equal(ConflictKind.BothModified, Assert.Single(status.Entries, e => e.Path == "a.txt").Conflict);
        Assert.Equal(ConflictKind.DeletedByUs, Assert.Single(status.Entries, e => e.Path == "gone.txt").Conflict);
        Assert.All(status.Entries, e => Assert.True(e.IsConflicted));
    }

    [Fact]
    public void Parser_reads_headers_and_every_entry_kind()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var output = string.Join('\0',
            $"# branch.oid {sha}", "# branch.head feature/x", "# branch.upstream origin/feature/x", "# branch.ab +3 -2",
            $"1 .T N... 100644 100644 120000 {sha} {sha} link",
            $"2 RM N... 100644 100644 100644 {sha} {sha} R87 new name.txt", "old name.txt",
            $"u AA N... 000000 100644 100644 100644 {sha} {sha} {sha} both.txt",
            "? untracked.txt", "");

        var status = StatusReader.Parse(output);

        Assert.Equal(("feature/x", "origin/feature/x", 3, 2), (status.Branch, status.Upstream, status.Ahead, status.Behind));
        Assert.Equal(sha, status.Head?.ToString());
        Assert.Equal(["both.txt", "link", "new name.txt", "untracked.txt"], status.Entries.Select(e => e.Path));
        Assert.Equal(ConflictKind.BothAdded, status.Entries[0].Conflict);
        Assert.Equal(FileChangeKind.TypeChanged, status.Entries[1].WorkTreeChange);
        Assert.Equal(("old name.txt", FileChangeKind.Renamed, FileChangeKind.Modified),
            (status.Entries[2].OldPath, status.Entries[2].IndexChange, status.Entries[2].WorkTreeChange));
        Assert.True(status.Entries[3].IsUntracked);
    }

    [Fact]
    public async Task Working_diffs_show_the_staged_and_unstaged_parts_separately()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "1\n2\n3\n"));
        repo.WriteFile("a.txt", "1\nTWO\n3\n").Stage("a.txt").WriteFile("a.txt", "1\nTWO\n3\n4\n");
        var entry = Assert.Single((await StatusAsync(repo)).Entries);

        var staged = await DiffAsync(repo, entry, WorkingDiffSide.Staged);
        var unstaged = await DiffAsync(repo, entry, WorkingDiffSide.Unstaged);

        Assert.Equal((1, 1), (staged.Additions, staged.Deletions));
        Assert.Contains(staged.Hunks[0].Lines, l => l is { Kind: DiffLineKind.Added, Text: "TWO" });
        Assert.Equal((1, 0), (unstaged.Additions, unstaged.Deletions));
        Assert.Contains(unstaged.Hunks[0].Lines, l => l is { Kind: DiffLineKind.Added, Text: "4", NewNumber: 4 });
    }

    [Fact]
    public async Task Untracked_file_diff_is_the_whole_file()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("dir/new file.txt", "x\ny\n");
        var entry = Assert.Single((await StatusAsync(repo)).Entries);

        var diff = await DiffAsync(repo, entry, WorkingDiffSide.Unstaged);

        Assert.Equal("dir/new file.txt", diff.Path);
        Assert.Equal((2, 0), (diff.Additions, diff.Deletions));
    }

    [Fact]
    public async Task Staged_rename_diff_follows_the_old_path()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("old.txt", "1\n2\n3\n4\n5\n"));
        repo.Git("mv", "old.txt", "new.txt");
        repo.WriteFile("new.txt", "1\n2\n3\n4\nFIVE\n").Stage("new.txt");
        var entry = Assert.Single((await StatusAsync(repo)).Entries);

        var diff = await DiffAsync(repo, entry, WorkingDiffSide.Staged);

        Assert.Equal("old.txt", diff.OldPath);
        Assert.Equal((1, 1), (diff.Additions, diff.Deletions));
    }

    private async Task<FileDiff> DiffAsync(TestRepoBuilder repo, StatusEntry entry, WorkingDiffSide side)
    {
        var result = await _git.GetWorkingFileDiffAsync(repo.Path, entry, side, Ct);
        return Assert.IsType<Success<FileDiff>>(result.Value).Value;
    }
}
