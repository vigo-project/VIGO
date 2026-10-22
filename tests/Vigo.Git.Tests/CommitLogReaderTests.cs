using System.IO.Pipelines;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.Git.Log;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class CommitLogReaderTests
{
    private readonly GitService _sut = new(new GitCli());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(GitResult Result, List<IReadOnlyList<CommitInfo>> Pages)> ReadAllAsync(
        string path, CommitLogOptions? options = null, CancellationToken? token = null)
    {
        var pages = new List<IReadOnlyList<CommitInfo>>();
        var result = await _sut.ReadLogAsync(path, options ?? CommitLogOptions.Default,
            (page, _) => { pages.Add(page); return ValueTask.CompletedTask; }, token ?? Ct);
        return (result, pages);
    }

    [Fact]
    public async Task Linear_history_is_read_newest_first_with_parents()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.txt", "1"));
        var second = repo.Commit("second", ("a.txt", "2"));

        var (result, pages) = await ReadAllAsync(repo.Path);

        Assert.IsType<Done>(result.Value);
        var commits = Assert.Single(pages);
        Assert.Equal([second, first], commits.Select(c => c.Sha.ToString()));
        Assert.Equal([GitSha.Parse(first)], commits[0].Parents);
        Assert.Empty(commits[1].Parents);
        Assert.Equal("second", commits[0].Subject);
        Assert.Equal("Vigo Test", commits[0].AuthorName);
        Assert.Equal("test@vigo.dev", commits[0].AuthorEmail);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_060), commits[0].AuthorDate);
    }

    [Fact]
    public async Task Parallel_branches_and_merge_are_in_topological_order()
    {
        using var repo = TestRepoBuilder.Create();
        var root = repo.Commit("root");
        repo.Branch("feature");
        var f1 = repo.Commit("feature 1");
        repo.Checkout("main");
        var m1 = repo.Commit("main 1");
        var merge = repo.Merge("feature");
        repo.Branch("unmerged");
        var u1 = repo.Commit("unmerged 1");
        repo.Checkout("main");

        var (_, pages) = await ReadAllAsync(repo.Path);

        var commits = pages.SelectMany(p => p).ToList();
        Assert.Equal(5, commits.Count);
        var index = commits.Select((c, i) => (c.Sha.ToString(), i)).ToDictionary();
        // Every child is listed before its parents.
        foreach (var commit in commits)
            foreach (var parent in commit.Parents)
                Assert.True(index[commit.Sha.ToString()] < index[parent.ToString()]);
        Assert.Contains(u1, index.Keys);
        var mergeCommit = commits[index[merge]];
        Assert.True(mergeCommit.IsMerge);
        Assert.Equal([GitSha.Parse(m1), GitSha.Parse(f1)], mergeCommit.Parents);
        Assert.Equal(commits.Count - 1, index[root]);
    }

    [Fact]
    public async Task Empty_repository_yields_no_page()
    {
        using var repo = TestRepoBuilder.Create();

        var (result, pages) = await ReadAllAsync(repo.Path);

        Assert.IsType<Done>(result.Value);
        Assert.Empty(pages);
    }

    [Fact]
    public async Task Commits_are_delivered_by_pages()
    {
        using var repo = TestRepoBuilder.Create();
        for (var i = 0; i < 5; i++) repo.Commit($"c{i}");

        var (_, pages) = await ReadAllAsync(repo.Path, new CommitLogOptions(PageSize: 2));

        Assert.Equal([2, 2, 1], pages.Select(p => p.Count));
        Assert.Equal("c4", pages[0][0].Subject);
        Assert.Equal("c0", pages[2][0].Subject);
    }

    [Fact]
    public async Task MaxCount_limits_the_number_of_commits()
    {
        using var repo = TestRepoBuilder.Create();
        for (var i = 0; i < 5; i++) repo.Commit($"c{i}");

        var (_, pages) = await ReadAllAsync(repo.Path, new CommitLogOptions(MaxCount: 3));

        Assert.Equal(3, Assert.Single(pages).Count);
    }

    [Fact]
    public async Task Cancellation_while_reading_returns_canceled()
    {
        using var repo = TestRepoBuilder.Create();
        for (var i = 0; i < 4; i++) repo.Commit($"c{i}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pages = 0;

        var result = await _sut.ReadLogAsync(repo.Path, new CommitLogOptions(PageSize: 1), (_, _) =>
        {
            pages++;
            cts.Cancel();
            return ValueTask.CompletedTask;
        }, cts.Token);

        Assert.IsType<Canceled>(result.Value);
        Assert.Equal(1, pages);
    }

    [Fact]
    public async Task Stash_tags_and_unicode_are_handled()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Git("config", "user.name", "Élodie 日本");
        var c1 = repo.Commit("Première version — ✓", ("a.txt", "1"));
        repo.Tag("v1", "release 1");
        repo.WriteFile("a.txt", "dirty");
        repo.Stash("work in progress");

        var (_, pages) = await ReadAllAsync(repo.Path);

        // The stash commits (worktree and index) are not part of the graph history.
        var commit = Assert.Single(Assert.Single(pages));
        Assert.Equal(c1, commit.Sha.ToString());
        Assert.Equal("Première version — ✓", commit.Subject);
        Assert.Equal("Élodie 日本", commit.AuthorName);
    }

    [Fact]
    public async Task Agent_checkpoint_refs_are_not_part_of_the_graph()
    {
        using var repo = TestRepoBuilder.Create();
        var c1 = repo.Commit("Real commit", ("a.txt", "1"));
        // An AI agent keeps its checkpoints as commits under refs/agents/, on top of the current commit.
        var checkpoint = repo.Git("commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "Agent host session - turn 1").Trim();
        repo.Git("update-ref", "refs/agents/session-1/checkpoints/turn/0", checkpoint);

        var (_, pages) = await ReadAllAsync(repo.Path);

        var commit = Assert.Single(Assert.Single(pages));
        Assert.Equal(c1, commit.Sha.ToString());
    }

    [Fact]
    public async Task Missing_directory_fails()
    {
        var (result, _) = await ReadAllAsync(Path.Combine(Path.GetTempPath(), "vigo-tests", "missing-" + Guid.NewGuid().ToString("N")));

        Assert.IsType<Failure>(result.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Parser_handles_fields_split_across_reads(int chunkSize)
    {
        var sha1 = new string('a', 40);
        var sha2 = new string('b', 40);
        // Second record without terminator, with an empty subject: both forms are accepted.
        var raw = $"{sha2}\0{sha1}\0Ann\0ann@x.dev\01700000000\0two\0{sha1}\0\0Ann\0ann@x.dev\01700000060\0";
        var pipe = new Pipe();
        var pages = new List<IReadOnlyList<CommitInfo>>();
        var reader = new CommitLogReader(10, (page, _) => { pages.Add(page); return ValueTask.CompletedTask; });

        var readTask = reader.ReadAsync(pipe.Reader, Ct);
        var bytes = Encoding.UTF8.GetBytes(raw);
        for (var i = 0; i < bytes.Length; i += chunkSize)
            await pipe.Writer.WriteAsync(bytes.AsMemory(i, Math.Min(chunkSize, bytes.Length - i)), Ct);
        await pipe.Writer.CompleteAsync();
        await readTask;

        var commits = Assert.Single(pages);
        Assert.Equal(2, commits.Count);
        Assert.Equal("two", commits[0].Subject);
        Assert.Equal(string.Empty, commits[1].Subject);
        Assert.Same(commits[0].AuthorName, commits[1].AuthorName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_060), commits[1].AuthorDate);
    }

    private static CommitFilter FilterOf(string? word = null, string? author = null, string? sha = null, string? file = null) =>
        new(word is null ? [] : [word], author is null ? [] : [author], sha, file is null ? [] : [file]);

    private async Task<string[]> SearchAsync(string path, CommitFilter filter)
    {
        var (result, pages) = await ReadAllAsync(path, CommitLogOptions.Default with { Filter = filter });
        Assert.IsType<Done>(result.Value);
        return [.. pages.SelectMany(p => p).Select(c => c.Subject)];
    }

    [Fact]
    public async Task Search_by_word_matches_the_message_without_regard_to_case()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Fix crash on startup", ("a.txt", "1"));
        repo.Commit("Add logo", ("a.txt", "2"));

        Assert.Equal(["Fix crash on startup"], await SearchAsync(repo.Path, FilterOf(word: "CRASH")));
        Assert.Empty(await SearchAsync(repo.Path, FilterOf(word: "missing")));
    }

    [Fact]
    public async Task Search_words_are_plain_text_not_regular_expressions()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Handle a.b input", ("a.txt", "1"));
        repo.Commit("Handle axb input", ("a.txt", "2"));

        Assert.Equal(["Handle a.b input"], await SearchAsync(repo.Path, FilterOf(word: "a.b")));
    }

    [Fact]
    public async Task Several_words_must_all_be_in_the_message()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("fix crash", ("a.txt", "1"));
        repo.Commit("fix typo", ("a.txt", "2"));
        var filter = new CommitFilter(["fix", "crash"], [], null, []);

        Assert.Equal(["fix crash"], await SearchAsync(repo.Path, filter));
    }

    [Fact]
    public async Task Search_by_author_matches_name_or_email()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("one", ("a.txt", "1"));

        Assert.Equal(["one"], await SearchAsync(repo.Path, FilterOf(author: "vigo test")));
        Assert.Equal(["one"], await SearchAsync(repo.Path, FilterOf(author: "test@vigo.dev")));
        Assert.Empty(await SearchAsync(repo.Path, FilterOf(author: "nobody")));
    }

    [Fact]
    public async Task Search_by_file_keeps_the_commits_that_touch_a_matching_path()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("docs", ("docs/README.md", "1"));
        repo.Commit("code", ("src/app.cs", "1"));

        Assert.Equal(["docs"], await SearchAsync(repo.Path, FilterOf(file: "readme")));
        Assert.Equal(["code"], await SearchAsync(repo.Path, FilterOf(file: "src\\app")));
        Assert.Equal(["docs"], await SearchAsync(repo.Path, FilterOf(file: "*.md")));
        Assert.Empty(await SearchAsync(repo.Path, FilterOf(file: "missing")));
    }

    [Fact]
    public async Task Search_by_sha_prefix_returns_that_commit_alone()
    {
        using var repo = TestRepoBuilder.Create();
        var first = repo.Commit("first", ("a.txt", "1"));
        repo.Commit("second", ("a.txt", "2"));

        Assert.Equal(["first"], await SearchAsync(repo.Path, FilterOf(sha: first[..8])));
    }

    [Fact]
    public async Task Search_by_unknown_sha_fails_instead_of_listing_everything()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("first", ("a.txt", "1"));

        var (result, pages) = await ReadAllAsync(repo.Path, CommitLogOptions.Default with { Filter = FilterOf(sha: "deadbeef") });

        Assert.IsType<Failure>(result.Value);
        Assert.Empty(pages.SelectMany(p => p));
    }

    [Fact]
    public async Task Parser_rejects_truncated_output()
    {
        var pipe = new Pipe();
        var reader = new CommitLogReader(10, (_, _) => ValueTask.CompletedTask);
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(new string('a', 40) + "\0\0Ann"), Ct);
        await pipe.Writer.CompleteAsync();

        await Assert.ThrowsAsync<FormatException>(() => reader.ReadAsync(pipe.Reader, Ct));
    }
}
