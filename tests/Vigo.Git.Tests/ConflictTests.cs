using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.Git.Conflicts;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class ConflictTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private static T AssertSuccess<T>(GitResult<T> result)
    {
        Assert.True(result.Value is Success<T>, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());
        return ((Success<T>)result.Value!).Value;
    }

    /// <summary>
    /// main and feature both change a.txt from the same base; feature is merged into main and stops on the conflict.
    /// LF line endings whatever the user's core.autocrlf.
    /// </summary>
    private async Task<TestRepoBuilder> ConflictedAsync(string @base, string main, string feature, string path = "a.txt")
    {
        var repo = TestRepoBuilder.Create();
        repo.Git("config", "core.autocrlf", "false");
        repo.Git("config", "merge.conflictStyle", "merge");
        repo.Commit("base", (path, @base));
        repo.Branch("feature");
        repo.Commit("feature", (path, feature));
        repo.Checkout("main");
        repo.Commit("main", (path, main));
        Assert.IsType<Conflict>((await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)).Value);
        return repo;
    }

    private async Task<StatusEntry> EntryAsync(TestRepoBuilder repo, string path = "a.txt") =>
        AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct)).Entries.Single(e => e.Path == path);

    [Fact]
    public void Parses_common_lines_and_conflicts_of_merge_file()
    {
        var m = new string('<', ConflictReader.MarkerSize);
        var b = new string('|', ConflictReader.MarkerSize);
        var s = new string('=', ConflictReader.MarkerSize);
        var e = new string('>', ConflictReader.MarkerSize);
        var text = $"a\n{m} ours\nB1\n{b} base\nb\n{s}\nB2\nB3\n{e} theirs\nc\n=======\n";

        var (segments, endsWithNewline, crlf) = ConflictReader.Parse(text);

        Assert.True(endsWithNewline);
        Assert.False(crlf);
        Assert.Equal(3, segments.Count);
        Assert.Equal(["a"], Assert.IsType<CommonLines>(segments[0]).Lines);
        var hunk = Assert.IsType<ConflictHunk>(segments[1]);
        Assert.Equal(["B1"], hunk.Ours);
        Assert.Equal(["b"], hunk.Base);
        Assert.Equal(["B2", "B3"], hunk.Theirs);
        // A 7-character "=======" (Markdown title underline) is content.
        Assert.Equal(["c", "======="], Assert.IsType<CommonLines>(segments[2]).Lines);
    }

    [Fact]
    public void Parses_crlf_text_without_final_newline()
    {
        var (segments, endsWithNewline, crlf) = ConflictReader.Parse("a\r\nb\r\nc");

        Assert.False(endsWithNewline);
        Assert.True(crlf);
        Assert.Equal(["a", "b", "c"], Assert.IsType<CommonLines>(Assert.Single(segments)).Lines);
    }

    [Fact]
    public async Task Reads_the_conflicts_from_the_index_stages()
    {
        using var repo = await ConflictedAsync("1\n2\n3\n4\n5\n", "1\nmain\n3\n4\n5\n", "1\nfeature\n3\n4\nfive\n");
        // The working-tree markers do not matter: the file is merged again from the stages.
        repo.WriteFile("a.txt", "edited by hand\n");

        var file = AssertSuccess(await _git.GetConflictAsync(repo.Path, await EntryAsync(repo), Ct));

        Assert.True(file.IsText);
        Assert.Equal(ConflictKind.BothModified, file.Kind);
        Assert.Equal("\n", file.LineEnding);
        Assert.Collection(file.Segments,
            s => Assert.Equal(["1"], Assert.IsType<CommonLines>(s).Lines),
            s =>
            {
                var hunk = Assert.IsType<ConflictHunk>(s);
                Assert.Equal(["main"], hunk.Ours);
                Assert.Equal(["2"], hunk.Base);
                Assert.Equal(["feature"], hunk.Theirs);
            },
            // Changed on one side only: merged without conflict.
            s => Assert.Equal(["3", "4", "five"], Assert.IsType<CommonLines>(s).Lines));
    }

    [Fact]
    public async Task Merges_a_file_added_on_both_sides_against_an_empty_base()
    {
        var repo = TestRepoBuilder.Create();
        using var _ = repo;
        repo.Git("config", "core.autocrlf", "false");
        repo.Commit("base", ("other.txt", "x\n"));
        repo.Branch("feature");
        repo.Commit("feature", ("new.txt", "same\nfeature\n"));
        repo.Checkout("main");
        repo.Commit("main", ("new.txt", "same\nmain\n"));
        Assert.IsType<Conflict>((await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)).Value);

        var file = AssertSuccess(await _git.GetConflictAsync(repo.Path, await EntryAsync(repo, "new.txt"), Ct));

        Assert.Equal(ConflictKind.BothAdded, file.Kind);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal(["main"], hunk.Ours);
        Assert.Equal(["feature"], hunk.Theirs);
    }

    [Fact]
    public async Task Reports_a_file_deleted_on_one_side_without_content()
    {
        var repo = TestRepoBuilder.Create();
        using var _ = repo;
        repo.Commit("base", ("a.txt", "1\n"));
        repo.Branch("feature");
        repo.Git("rm", "-q", "a.txt");
        repo.Git("commit", "-q", "-m", "delete");
        repo.Checkout("main");
        repo.Commit("main", ("a.txt", "2\n"));
        Assert.IsType<Conflict>((await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)).Value);

        var file = AssertSuccess(await _git.GetConflictAsync(repo.Path, await EntryAsync(repo), Ct));

        Assert.Equal(ConflictKind.DeletedByThem, file.Kind);
        Assert.True(file.HasOurs);
        Assert.False(file.HasTheirs);
        Assert.False(file.IsText);
        Assert.Empty(file.Segments);
    }

    [Fact]
    public async Task Reports_a_binary_file()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("base", ("b.bin", "a\0base"));
        repo.Branch("feature");
        repo.Commit("feature", ("b.bin", "a\0feature"));
        repo.Checkout("main");
        repo.Commit("main", ("b.bin", "a\0main"));
        Assert.IsType<Conflict>((await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)).Value);

        var file = AssertSuccess(await _git.GetConflictAsync(repo.Path, await EntryAsync(repo, "b.bin"), Ct));

        Assert.True(file.IsBinary);
        Assert.False(file.IsText);
    }

    [Fact]
    public async Task Writes_and_stages_the_resolved_content()
    {
        using var repo = await ConflictedAsync("1\n2\n", "1\nmain\n", "1\nfeature\n");

        AssertDone(await _git.ResolveConflictAsync(repo.Path, "a.txt", new ConflictResolution.Content("1\nmain\nfeature\n"), Ct));

        Assert.Equal("1\nmain\nfeature\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
        var entry = await EntryAsync(repo);
        Assert.False(entry.IsConflicted);
        Assert.Equal("1\nmain\nfeature\n", repo.Git("show", ":a.txt"));
    }

    [Fact]
    public async Task Takes_one_side_or_deletes_the_file()
    {
        using var repo = await ConflictedAsync("1\n", "main\n", "feature\n");

        AssertDone(await _git.ResolveConflictAsync(repo.Path, "a.txt", new ConflictResolution.TakeSide(ConflictSide.Theirs), Ct));
        Assert.Equal("feature\n", repo.Git("show", ":a.txt"));
        Assert.False((await EntryAsync(repo)).IsConflicted);
    }

    [Fact]
    public async Task Deletes_a_conflicted_file()
    {
        using var repo = await ConflictedAsync("1\n", "main\n", "feature\n");

        AssertDone(await _git.ResolveConflictAsync(repo.Path, "a.txt", new ConflictResolution.Delete(), Ct));

        Assert.False(File.Exists(Path.Combine(repo.Path, "a.txt")));
        var status = AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct));
        var entry = Assert.Single(status.Entries);
        Assert.False(entry.IsConflicted);
        Assert.Equal(FileChangeKind.Deleted, entry.IndexChange);
    }

    [Fact]
    public async Task Taking_the_side_that_deleted_the_file_deletes_it()
    {
        var repo = TestRepoBuilder.Create();
        using var _ = repo;
        repo.Commit("base", ("a.txt", "1\n"));
        repo.Branch("feature");
        repo.Git("rm", "-q", "a.txt");
        repo.Git("commit", "-q", "-m", "delete");
        repo.Checkout("main");
        repo.Commit("main", ("a.txt", "2\n"));
        Assert.IsType<Conflict>((await _git.MergeAsync(repo.Path, "refs/heads/feature", Ct)).Value);

        AssertDone(await _git.ResolveConflictAsync(repo.Path, "a.txt", new ConflictResolution.TakeSide(ConflictSide.Theirs), Ct));

        Assert.False(File.Exists(Path.Combine(repo.Path, "a.txt")));
        Assert.DoesNotContain(AssertSuccess(await _git.GetStatusAsync(repo.Path, Ct)).Entries, e => e.IsConflicted);
    }

    [Fact]
    public async Task Refuses_to_start_a_merge_tool_when_none_is_configured()
    {
        using var repo = await ConflictedAsync("1\n", "main\n", "feature\n");
        repo.Git("config", "merge.tool", "");

        var result = await _git.RunMergeToolAsync(repo.Path, "a.txt", null, Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.True((await EntryAsync(repo)).IsConflicted);
    }
}
