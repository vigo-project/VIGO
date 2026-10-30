using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class CommitTests
{
    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Message(TestRepoBuilder repo, string revision = "HEAD") =>
        repo.Git("log", "-1", "--format=%B", revision).ReplaceLineEndings("\n");

    private async Task<GitSha> CommitAsync(TestRepoBuilder repo, CommitRequest request)
    {
        var result = await _git.CommitAsync(repo.Path, request, Ct);
        return Assert.IsType<Success<GitSha>>(result.Value, exactMatch: true).Value;
    }

    /// <summary>Installs a hook script (sh, run by Git for Windows) and makes sure the repository uses it.</summary>
    private static void InstallHook(TestRepoBuilder repo, string name, string script)
    {
        var hooks = Path.Combine(repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        TestRepoBuilder.WriteHook(Path.Combine(hooks, name), script);
        repo.Git("config", "core.hooksPath", hooks.Replace('\\', '/'));
    }

    [Fact]
    public async Task First_commit_records_the_staged_files_and_the_message()
    {
        using var repo = TestRepoBuilder.Create();
        repo.WriteFile("a.txt", "a\n").WriteFile("b.txt", "b\n").Stage("a.txt");

        var sha = await CommitAsync(repo, new CommitRequest(CommitMessage.Compose("- Initial « commit »", "Body line 1\n\nBody line 2")));

        Assert.Equal(repo.Head(), sha.ToString());
        Assert.Equal("- Initial « commit »\n\nBody line 1\n\nBody line 2\n\n", Message(repo));
        Assert.Equal("a.txt\n", repo.Git("ls-tree", "--name-only", "HEAD").ReplaceLineEndings("\n"));
        Assert.Contains("?? b.txt", repo.Git("status", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Co_authors_become_trailers()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");

        await CommitAsync(repo, new CommitRequest("Pair work\n", CoAuthors:
            [new GitIdentity("Ada Lovelace", "ada@example.com"), new GitIdentity(" ", "ignored@example.com")]));

        Assert.Equal("Pair work\n\nCo-authored-by: Ada Lovelace <ada@example.com>\n\n", Message(repo));
    }

    [Fact]
    public async Task Nothing_staged_is_a_failure_and_HEAD_does_not_move()
    {
        using var repo = TestRepoBuilder.Create();
        var head = repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "unstaged\n");

        var result = await _git.CommitAsync(repo.Path, new CommitRequest("Nothing\n"), Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.Equal(head, repo.Head());
    }

    [Fact]
    public async Task Amend_replaces_HEAD_keeping_its_parent()
    {
        using var repo = TestRepoBuilder.Create();
        var parent = repo.Commit("init", ("a.txt", "a\n"));
        var old = repo.Commit("Typo in mesage", ("b.txt", "b\n"));

        // Message only: nothing staged.
        var reworded = await CommitAsync(repo, new CommitRequest("Fixed message\n", Amend: true));
        Assert.NotEqual(old, reworded.ToString());
        Assert.Equal(parent, repo.Git("rev-parse", "HEAD^").Trim());
        Assert.Equal("Fixed message\n\n", Message(repo));

        // With a newly staged file.
        repo.WriteFile("c.txt", "c\n").Stage("c.txt");
        await CommitAsync(repo, new CommitRequest("Fixed message\n", Amend: true));
        Assert.Equal(parent, repo.Git("rev-parse", "HEAD^").Trim());
        Assert.Equal("a.txt\nb.txt\nc.txt\n", repo.Git("ls-tree", "--name-only", "HEAD").ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Failing_pre_commit_hook_reports_its_output_unless_hooks_are_skipped()
    {
        using var repo = TestRepoBuilder.Create();
        var head = repo.Commit("init", ("a.txt", "a\n"));
        InstallHook(repo, "pre-commit", "#!/bin/sh\necho 'lint: 2 problems' >&2\necho 'src/a.cs: missing semicolon' >&2\nexit 1\n");
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");

        var result = await _git.CommitAsync(repo.Path, new CommitRequest("Blocked\n"), Ct);

        var failure = Assert.IsType<Failure>(result.Value);
        Assert.Equal("lint: 2 problems", failure.Message);
        Assert.Equal("lint: 2 problems\nsrc/a.cs: missing semicolon", failure.Details);
        Assert.Equal(head, repo.Head());

        await CommitAsync(repo, new CommitRequest("Forced\n", SkipHooks: true));
        Assert.Equal("Forced\n\n", Message(repo));
    }

    [Fact]
    public async Task Commit_msg_hook_can_rewrite_the_message()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        InstallHook(repo, "commit-msg", "#!/bin/sh\nprintf '\\nRefs: #42\\n' >> \"$1\"\n");
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");

        await CommitAsync(repo, new CommitRequest("With hook\n"));

        Assert.Equal("With hook\n\nRefs: #42\n\n", Message(repo));
    }

    [Fact]
    public async Task Config_value_is_read_with_the_repository_scope()
    {
        using var repo = TestRepoBuilder.Create();

        var signing = await _git.GetConfigValueAsync(repo.Path, "commit.gpgsign", Ct);
        var missing = await _git.GetConfigValueAsync(repo.Path, "vigo.missing-key", Ct);

        Assert.Equal("false", Assert.IsType<Success<string?>>(signing.Value).Value);
        Assert.Null(Assert.IsType<Success<string?>>(missing.Value).Value);
    }
}
