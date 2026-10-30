using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Hooks folder, enabled / disabled hooks and the "skip hooks" switch (J7.31).</summary>
public sealed class HooksTests : IDisposable
{
    private const string Failing = "#!/bin/sh\necho 'lint failed' >&2\nexit 1\n";

    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();

    public HooksTests() => _repo.Commit("init", ("a.txt", "a\n"));

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string HooksDir => Path.Combine(_repo.Path, ".git", "hooks");

    private void Write(string name, string content = "#!/bin/sh\nexit 0\n", string? folder = null)
    {
        folder ??= HooksDir;
        Directory.CreateDirectory(folder);
        TestRepoBuilder.WriteHook(Path.Combine(folder, name), content);
    }

    private async Task<RepositoryHooks> ReadAsync() =>
        Assert.IsType<Success<RepositoryHooks>>((await _git.GetHooksAsync(_repo.Path, Ct)).Value).Value;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    [Fact]
    public async Task Only_the_hooks_of_the_effective_folder_are_listed_without_the_samples_and_the_disabled_ones_are_marked()
    {
        Write("pre-commit");
        Write("pre-push.sample");
        Write("commit-msg.disabled");

        var hooks = await ReadAsync();

        Assert.Null(hooks.HooksPath);
        Assert.Equal([new HookEntry("commit-msg", false), new HookEntry("pre-commit", true)], hooks.Hooks);
        Assert.False(hooks.SkipHooks);
    }

    [Fact]
    public async Task The_hooks_folder_is_set_relative_then_removed()
    {
        Write("pre-commit");
        Write("pre-push", folder: Path.Combine(_repo.Path, "tools", "hooks"));

        AssertDone(await _git.SetHooksPathAsync(_repo.Path, "tools/hooks", Ct));
        var set = await ReadAsync();

        Assert.Equal("tools/hooks", set.HooksPath);
        Assert.Equal([new HookEntry("pre-push", true)], set.Hooks);

        AssertDone(await _git.SetHooksPathAsync(_repo.Path, "", Ct));
        AssertDone(await _git.SetHooksPathAsync(_repo.Path, null, Ct));
        var reset = await ReadAsync();

        Assert.Null(reset.HooksPath);
        Assert.Equal([new HookEntry("pre-commit", true)], reset.Hooks);
    }

    [Fact]
    public async Task A_hook_is_disabled_and_enabled_by_renaming_and_nothing_is_overwritten()
    {
        Write("pre-commit", Failing);

        AssertDone(await _git.SetHookEnabledAsync(_repo.Path, "pre-commit", false, Ct));
        Assert.True(File.Exists(Path.Combine(HooksDir, "pre-commit.disabled")));
        Assert.Equal([new HookEntry("pre-commit", false)], (await ReadAsync()).Hooks);

        AssertDone(await _git.SetHookEnabledAsync(_repo.Path, "pre-commit", true, Ct));
        Assert.True(File.Exists(Path.Combine(HooksDir, "pre-commit")));

        Write("pre-commit.disabled", "other");
        var refused = await _git.SetHookEnabledAsync(_repo.Path, "pre-commit", false, Ct);

        Assert.IsType<Failure>(refused.Value);
        Assert.Equal(Failing, File.ReadAllText(Path.Combine(HooksDir, "pre-commit")).ReplaceLineEndings("\n"));
        Assert.IsType<Failure>((await _git.SetHookEnabledAsync(_repo.Path, "../x", false, Ct)).Value);
    }

    [Fact]
    public async Task Skip_hooks_makes_commits_made_by_the_service_ignore_a_failing_pre_commit()
    {
        Write("pre-commit", Failing);
        _repo.WriteFile("a.txt", "a2\n").Stage("a.txt");

        Assert.IsType<Failure>((await _git.CommitAsync(_repo.Path, new CommitRequest("Blocked\n"), Ct)).Value);

        AssertDone(await _git.SetSkipHooksAsync(_repo.Path, true, Ct));
        Assert.True((await ReadAsync()).SkipHooks);
        Assert.IsType<Success<GitSha>>((await _git.CommitAsync(_repo.Path, new CommitRequest("Forced\n"), Ct)).Value);

        AssertDone(await _git.SetSkipHooksAsync(_repo.Path, false, Ct));
        _repo.WriteFile("a.txt", "a3\n").Stage("a.txt");

        Assert.False((await ReadAsync()).SkipHooks);
        Assert.IsType<Failure>((await _git.CommitAsync(_repo.Path, new CommitRequest("Blocked again\n"), Ct)).Value);
    }

    [Fact]
    public async Task Skip_hooks_makes_pushes_made_by_the_service_ignore_a_failing_pre_push()
    {
        using var remote = TestRepoBuilder.Create(bare: true);
        _repo.Git("remote", "add", "origin", remote.Path);
        Write("pre-push", Failing);

        var blocked = await _git.PushAsync(_repo.Path, new PushRequest(), null, Ct);
        Assert.IsType<Failure>(blocked.Value);

        AssertDone(await _git.SetSkipHooksAsync(_repo.Path, true, Ct));
        AssertDone(await _git.PushAsync(_repo.Path, new PushRequest(), null, Ct));
        Assert.Equal(_repo.Head(), remote.Git("rev-parse", "main").Trim());
    }
}
