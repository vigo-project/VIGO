using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class FileHistoryTests
{
    private readonly GitService _sut = new(new GitCli());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<List<FileHistoryEntry>> History(TestRepoBuilder repo, string path, bool follow = true, int pageSize = 100)
    {
        var entries = new List<FileHistoryEntry>();
        var result = await _sut.ReadFileHistoryAsync(repo.Path, path, follow, pageSize, (page, _) =>
        {
            entries.AddRange(page);
            return ValueTask.CompletedTask;
        }, Ct);
        Assert.IsType<Done>(result.Value);
        return entries;
    }

    private async Task<List<BlameHunk>> Blame(TestRepoBuilder repo, string path, string? revision)
    {
        var hunks = new List<BlameHunk>();
        var result = await _sut.BlameAsync(repo.Path, path, revision, (batch, _) =>
        {
            hunks.AddRange(batch);
            return ValueTask.CompletedTask;
        }, Ct);
        Assert.IsType<Done>(result.Value);
        return [.. hunks.OrderBy(h => h.Line)];
    }

    [Fact]
    public async Task History_lists_the_commits_of_the_file_newest_first_with_their_changes()
    {
        using var repo = TestRepoBuilder.Create();
        var added = repo.Commit("add", ("src/a.txt", "1\n2\n3\n"), ("other.txt", "x\n"));
        repo.Commit("other only", ("other.txt", "y\n"));
        var changed = repo.Commit("change", ("src/a.txt", "1\ntwo\n3\n4\n"));

        var entries = await History(repo, "src/a.txt");

        Assert.Equal([changed, added], entries.Select(e => e.Commit.Sha.ToString()));
        Assert.Equal("change", entries[0].Commit.Subject);
        Assert.Equal(new ChangedFile("src/a.txt", null, FileChangeKind.Modified, 2, 1), entries[0].File);
        Assert.Equal(new ChangedFile("src/a.txt", null, FileChangeKind.Added, 3, 0), entries[1].File);
        Assert.Single(entries[0].Commit.Parents);
    }

    [Fact]
    public async Task Blame_reports_non_ascii_paths_unquoted()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("add", ("café.txt", "1\n2\n"));
        repo.Commit("change", ("café.txt", "1\ntwo\n"));

        var hunks = await Blame(repo, "café.txt", "HEAD");

        Assert.All(hunks, h => Assert.Equal("café.txt", h.Path));
    }

    [Fact]
    public async Task History_follows_a_rename_when_asked()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("add", ("old name.txt", "a\nb\nc\nd\ne\nf\n"));
        Directory.CreateDirectory(Path.Combine(repo.Path, "dir"));
        repo.Git("mv", "old name.txt", "dir/new name.txt");
        var renamed = repo.Commit("rename", ("dir/new name.txt", "a\nb\nc\nd\ne\nF\n"));

        var followed = await History(repo, "dir/new name.txt");
        var plain = await History(repo, "dir/new name.txt", follow: false);

        Assert.Equal([renamed, first], followed.Select(e => e.Commit.Sha.ToString()));
        Assert.Equal(FileChangeKind.Renamed, followed[0].File.Kind);
        Assert.Equal("dir/new name.txt", followed[0].File.Path);
        Assert.Equal("old name.txt", followed[0].File.OldPath);
        Assert.Equal((1, 1), (followed[0].File.Additions, followed[0].File.Deletions));
        Assert.Equal("old name.txt", followed[1].File.Path);
        Assert.Equal([renamed], plain.Select(e => e.Commit.Sha.ToString()));
        Assert.Equal(FileChangeKind.Added, plain[0].File.Kind);
    }

    [Fact]
    public async Task History_includes_the_deletion_and_is_delivered_by_pages()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("add", ("a.txt", "1\n"));
        repo.Commit("edit", ("a.txt", "2\n"));
        repo.DeleteFile("a.txt");
        repo.Commit("delete");

        var pages = new List<int>();
        var result = await _sut.ReadFileHistoryAsync(repo.Path, "a.txt", true, 2, (page, _) =>
        {
            pages.Add(page.Count);
            return ValueTask.CompletedTask;
        }, Ct);

        Assert.IsType<Done>(result.Value);
        Assert.Equal([2, 1], pages);
        var entries = await History(repo, "a.txt");
        Assert.Equal(FileChangeKind.Deleted, entries[0].File.Kind);
        Assert.Equal((0, 1), (entries[0].File.Additions, entries[0].File.Deletions));
    }

    [Fact]
    public async Task History_of_an_unknown_file_is_empty()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("add", ("a.txt", "1\n"));

        Assert.Empty(await History(repo, "missing.txt"));
    }

    [Fact]
    public async Task Blame_gives_the_commit_of_each_line_and_where_it_came_from()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.txt", "1\n2\n3\n"));
        var second = repo.Commit("second", ("a.txt", "1\nTWO\n3\n4\n"));

        var hunks = await Blame(repo, "a.txt", "HEAD");

        Assert.Equal([(1, 1, first), (2, 1, second), (3, 1, first), (4, 1, second)],
            hunks.Select(h => (h.Line, h.LineCount, h.Commit.Sha.ToString())));
        var newer = hunks[1].Commit;
        Assert.Equal("second", newer.Summary);
        Assert.Equal("Vigo Test", newer.AuthorName);
        Assert.False(newer.IsBoundary);
        Assert.Equal(new BlameOrigin(GitSha.Parse(first), "a.txt"), newer.Previous);
        Assert.Same(hunks[0].Commit, hunks[2].Commit);
        Assert.True(hunks[0].Commit.IsBoundary);
        Assert.Null(hunks[0].Commit.Previous);

        // At an older revision, the lines of that version.
        var older = await Blame(repo, "a.txt", first);
        Assert.Equal([(1, 3, first)], older.Select(h => (h.Line, h.LineCount, h.Commit.Sha.ToString())));
    }

    [Fact]
    public async Task Blame_of_the_file_on_disk_marks_the_uncommitted_lines()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.txt", "1\n2\n"));
        repo.WriteFile("a.txt", "1\nchanged\n");

        var hunks = await Blame(repo, "a.txt", null);

        Assert.False(hunks[0].Commit.IsUncommitted);
        Assert.Equal(first, hunks[0].Commit.Sha.ToString());
        Assert.True(hunks[1].Commit.IsUncommitted);
    }

    [Fact]
    public async Task Blame_follows_the_file_through_a_rename()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("old.txt", "a\nb\nc\nd\n"));
        repo.Git("mv", "old.txt", "new.txt");
        repo.Commit("rename");

        var hunks = await Blame(repo, "new.txt", "HEAD");

        Assert.Equal(first, hunks.Single().Commit.Sha.ToString());
        Assert.Equal("old.txt", hunks.Single().Path);
    }

    [Fact]
    public async Task Blame_of_a_path_missing_in_that_version_fails()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("first", ("a.txt", "1\n"));

        var result = await _sut.BlameAsync(repo.Path, "missing.txt", "HEAD", (_, _) => ValueTask.CompletedTask, Ct);

        Assert.Contains("n'existe pas", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lists_the_files_of_the_index_from_a_subdirectory()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("first", ("a.txt", "1\n"), ("sub/b c.txt", "2\n"));
        repo.WriteFile("untracked.txt", "x\n");

        var result = await _sut.ListFilesAsync(Path.Combine(repo.Path, "sub"), Ct);

        Assert.Equal(["a.txt", "sub/b c.txt"], Assert.IsType<Success<IReadOnlyList<string>>>(result.Value).Value);
    }

    [Fact]
    public async Task Restore_writes_an_older_version_over_the_file_and_leaves_the_index()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("old.txt", "one\n"));
        Directory.CreateDirectory(Path.Combine(repo.Path, "dir"));
        repo.Git("mv", "old.txt", "dir/new.txt");
        repo.Commit("rename", ("dir/new.txt", "two\n"));
        repo.WriteFile("dir/new.txt", "local\n");

        var result = await _sut.RestoreFileAsync(repo.Path, GitSha.Parse(first), "old.txt", "dir/new.txt", Ct);

        Assert.IsType<Done>(result.Value);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(repo.Path, "dir", "new.txt")));
        Assert.Equal(" M dir/new.txt", repo.Git("status", "--porcelain").TrimEnd());
        Assert.False(File.Exists(Path.Combine(repo.Path, "dir", "new.txt.vigo-restore")));
    }

    [Fact]
    public async Task Restore_of_a_path_missing_in_the_commit_fails_and_keeps_the_file()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.txt", "one\n"));
        repo.WriteFile("a.txt", "local\n");

        var result = await _sut.RestoreFileAsync(repo.Path, GitSha.Parse(first), "missing.txt", "a.txt", Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.Equal("local\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }
}
