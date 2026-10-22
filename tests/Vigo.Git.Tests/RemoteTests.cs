using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Fetch, pull and push against a local bare repository ("origin") shared by two clones.</summary>
public sealed class RemoteTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _remote;
    private readonly TestRepoBuilder _mine;
    private readonly TestRepoBuilder _theirs;

    public RemoteTests()
    {
        _remote = TestRepoBuilder.Create(bare: true);
        _mine = TestRepoBuilder.Create();
        _mine.Commit("init", ("a.txt", "1\n2\n3\n"));
        _mine.Git("remote", "add", "origin", _remote.Path);
        _mine.Git("push", "--quiet", "--set-upstream", "origin", "main");
        _theirs = TestRepoBuilder.Clone(_remote.Path);
    }

    public void Dispose()
    {
        _theirs.Dispose();
        _mine.Dispose();
        _remote.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private string RemoteHead(string branch = "main") => _remote.Git("rev-parse", $"refs/heads/{branch}").Trim();

    /// <summary>Records reports synchronously (Progress&lt;T&gt; would post them to the thread pool).</summary>
    private sealed class Recorder : IProgress<GitProgress>
    {
        public List<GitProgress> Items { get; } = [];

        public void Report(GitProgress value)
        {
            lock (Items) Items.Add(value);
        }
    }

    /// <summary>Their commit on origin/main, plus ours not pushed yet (diverged branches).</summary>
    private void Diverge(string theirsContent = "1\n2\n3\nthem\n", string mineContent = "mine\n1\n2\n3\n")
    {
        _theirs.Commit("theirs", ("a.txt", theirsContent));
        _theirs.Git("push", "--quiet");
        _mine.Commit("mine", ("a.txt", mineContent));
    }

    [Fact]
    public async Task Fetch_updates_remote_tracking_branches_and_reports_progress()
    {
        var theirs = _theirs.Commit("theirs", ("b.txt", "b\n"));
        _theirs.Git("push", "--quiet");
        var progress = new Recorder();

        AssertDone(await _git.FetchAsync(_mine.Path, progress, true, Ct));

        Assert.Equal(theirs, _mine.Git("rev-parse", "origin/main").Trim());
        Assert.NotEqual(theirs, _mine.Head());
        Assert.NotEmpty(progress.Items);
    }

    [Fact]
    public async Task Fetch_prunes_remote_branches_deleted_elsewhere()
    {
        _theirs.Git("push", "--quiet", "origin", "main:refs/heads/gone", "main:refs/heads/gone2");
        AssertDone(await _git.FetchAsync(_mine.Path, null, true, Ct));
        Assert.Contains("origin/gone2", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);
        _theirs.Git("push", "--quiet", "origin", "--delete", "gone", "gone2");

        AssertDone(await _git.FetchAsync(_mine.Path, null, true, Ct));
        Assert.DoesNotContain("origin/gone", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);

        _theirs.Git("push", "--quiet", "origin", "main:refs/heads/gone3");
        AssertDone(await _git.FetchRemoteAsync(_mine.Path, "origin", null, true, Ct));
        _theirs.Git("push", "--quiet", "origin", "--delete", "gone3");
        AssertDone(await _git.FetchRemoteAsync(_mine.Path, "origin", null, true, Ct));
        Assert.DoesNotContain("origin/gone3", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);

        _theirs.Git("push", "--quiet", "origin", "main:refs/heads/gone4");
        AssertDone(await _git.FetchInBackgroundAsync(_mine.Path, true, Ct));
        _theirs.Git("push", "--quiet", "origin", "--delete", "gone4");
        AssertDone(await _git.FetchInBackgroundAsync(_mine.Path, true, Ct));
        Assert.DoesNotContain("origin/gone4", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_prune_the_git_config_decides()
    {
        _theirs.Git("push", "--quiet", "origin", "main:refs/heads/gone");
        AssertDone(await _git.FetchAsync(_mine.Path, null, false, Ct));
        _theirs.Git("push", "--quiet", "origin", "--delete", "gone");

        AssertDone(await _git.FetchAsync(_mine.Path, null, false, Ct));
        AssertDone(await _git.FetchRemoteAsync(_mine.Path, "origin", null, false, Ct));
        AssertDone(await _git.FetchInBackgroundAsync(_mine.Path, false, Ct));
        Assert.Contains("origin/gone", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);

        _mine.Git("config", "fetch.prune", "true");
        AssertDone(await _git.FetchAsync(_mine.Path, null, false, Ct));
        Assert.DoesNotContain("origin/gone", _mine.Git("branch", "--remotes"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_reports_when_it_starts_merging_and_keeps_git_trace_out_of_the_output()
    {
        _theirs.Commit("theirs", ("b.txt", "b\n"));
        _theirs.Git("push", "--quiet");
        var progress = new Recorder();

        AssertDone(await _git.PullAsync(_mine.Path, PullMode.Default, progress, Ct));

        Assert.Single(progress.Items, p => p.Phase == GitProgressPhase.Merging);
        Assert.DoesNotContain(progress.Items, p => p.Phase != GitProgressPhase.Merging && p.Text.Contains("trace:", StringComparison.Ordinal));
        Assert.Equal(RemoteHead(), _mine.Head());
    }

    [Fact]
    public async Task A_refused_pull_has_no_git_trace_in_its_details()
    {
        _mine.Git("config", "pull.ff", "only");
        Diverge();

        var failure = Assert.IsType<Failure>((await _git.PullAsync(_mine.Path, PullMode.Default, null, Ct)).Value);

        Assert.DoesNotContain("trace:", failure.Details ?? failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_of_one_remote_leaves_the_others_alone()
    {
        var theirs = _theirs.Commit("theirs", ("b.txt", "b\n"));
        _theirs.Git("push", "--quiet");
        using var other = TestRepoBuilder.Create(bare: true);
        _mine.Git("remote", "add", "other", other.Path);

        AssertDone(await _git.FetchRemoteAsync(_mine.Path, "origin", null, true, Ct));

        Assert.Equal(theirs, _mine.Git("rev-parse", "origin/main").Trim());
        Assert.Equal(string.Empty, _mine.Git("for-each-ref", "refs/remotes/other").Trim());
    }

    [Fact]
    public async Task Fetch_of_a_remote_named_like_an_option_is_refused()
    {
        Assert.IsType<Failure>((await _git.FetchRemoteAsync(_mine.Path, "--all", null, true, Ct)).Value);
    }

    [Fact]
    public async Task Pull_fast_forwards()
    {
        var theirs = _theirs.Commit("theirs", ("b.txt", "b\n"));
        _theirs.Git("push", "--quiet");

        AssertDone(await _git.PullAsync(_mine.Path, PullMode.FastForwardOnly, null, Ct));

        Assert.Equal(theirs, _mine.Head());
    }

    [Fact]
    public async Task Pull_merge_creates_a_merge_commit()
    {
        Diverge();

        AssertDone(await _git.PullAsync(_mine.Path, PullMode.Merge, null, Ct));

        Assert.Equal(3, _mine.Git("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);
        Assert.Equal("mine\n1\n2\n3\nthem\n", File.ReadAllText(Path.Combine(_mine.Path, "a.txt")));
    }

    [Fact]
    public async Task Pull_rebase_replays_local_commits_on_top()
    {
        Diverge();

        AssertDone(await _git.PullAsync(_mine.Path, PullMode.Rebase, null, Ct));

        Assert.Equal(_theirs.Head(), _mine.Git("rev-parse", "HEAD^").Trim());
        Assert.Equal("mine", _mine.Git("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task Pull_fast_forward_only_explains_diverged_branches()
    {
        Diverge();
        var before = _mine.Head();

        var result = await _git.PullAsync(_mine.Path, PullMode.FastForwardOnly, null, Ct);

        var failure = Assert.IsType<Failure>(result.Value);
        Assert.StartsWith("Fast-forward impossible", failure.Message, StringComparison.Ordinal);
        Assert.Equal(before, _mine.Head());
    }

    [Fact]
    public async Task Pull_with_conflicting_changes_reports_the_conflicted_paths()
    {
        Diverge(theirsContent: "1\ntheirs\n3\n", mineContent: "1\nmine\n3\n");

        var result = await _git.PullAsync(_mine.Path, PullMode.Merge, null, Ct);

        var conflict = Assert.IsType<Conflict>(result.Value);
        Assert.Equal(["a.txt"], conflict.Paths);
        _mine.Git("merge", "--abort");
    }

    [Fact]
    public async Task Push_sends_the_current_branch_to_its_upstream()
    {
        var mine = _mine.Commit("mine", ("b.txt", "b\n"));
        var progress = new Recorder();

        AssertDone(await _git.PushAsync(_mine.Path, new PushRequest(), progress, Ct));

        Assert.Equal(mine, RemoteHead());
        Assert.NotEmpty(progress.Items);
    }

    [Fact]
    public async Task Push_of_a_new_branch_sets_its_upstream()
    {
        _mine.Branch("feature");
        var mine = _mine.Commit("feature work", ("b.txt", "b\n"));

        AssertDone(await _git.PushAsync(_mine.Path, new PushRequest(), null, Ct));

        Assert.Equal(mine, RemoteHead("feature"));
        Assert.Equal("origin/feature", _mine.Git("rev-parse", "--abbrev-ref", "@{upstream}").Trim());
    }

    [Fact]
    public async Task Rejected_push_is_explained_and_force_with_lease_overwrites()
    {
        Diverge();
        var theirs = RemoteHead();

        var rejected = await _git.PushAsync(_mine.Path, new PushRequest(), null, Ct);
        var failure = Assert.IsType<Failure>(rejected.Value);
        Assert.StartsWith("Push refusé : origin/main contient des commits absents en local", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[rejected]", failure.Details, StringComparison.Ordinal);
        // Their commit was never fetched ("fetch first"): a lease would erase it, so no forced push is offered.
        Assert.NotEqual(FailureReason.PushNeedsForce, failure.Reason);
        Assert.Equal(theirs, RemoteHead());

        // After a (background) fetch the lease alone would pass and erase their commit: --force-if-includes refuses it
        // until their commit is part of the branch.
        AssertDone(await _git.FetchAsync(_mine.Path, null, true, Ct));
        var forced = Assert.IsType<Failure>((await _git.PushAsync(_mine.Path, new PushRequest(ForceWithLease: true), null, Ct)).Value);
        Assert.StartsWith("Push forcé refusé", forced.Message, StringComparison.Ordinal);
        Assert.Equal(theirs, RemoteHead());

        _mine.Git("rebase", "--quiet", "origin/main");
        AssertDone(await _git.PushAsync(_mine.Path, new PushRequest(ForceWithLease: true), null, Ct));
        Assert.Equal(_mine.Head(), RemoteHead());
    }

    [Fact]
    public async Task Push_after_amending_a_pushed_commit_needs_force()
    {
        // Amend of the pushed tip: the remote tip is known locally, so offering a lease is safe.
        _mine.Git("commit", "--quiet", "--amend", "--allow-empty", "-m", "amended");
        var amended = Assert.IsType<Failure>((await _git.PushAsync(_mine.Path, new PushRequest(), null, Ct)).Value);
        Assert.Equal(FailureReason.PushNeedsForce, amended.Reason);
        AssertDone(await _git.PushAsync(_mine.Path, new PushRequest(ForceWithLease: true), null, Ct));
    }

    [Fact]
    public async Task Force_with_lease_refuses_when_the_remote_moved_since_the_last_fetch()
    {
        // Their commit is on origin but was never fetched here: the lease (origin/main) is stale.
        Diverge();
        _mine.Git("update-ref", "refs/remotes/origin/main", _mine.Git("rev-parse", "HEAD^").Trim());
        var theirs = RemoteHead();

        var result = await _git.PushAsync(_mine.Path, new PushRequest(ForceWithLease: true), null, Ct);

        var failure = Assert.IsType<Failure>(result.Value);
        Assert.StartsWith("Push forcé refusé", failure.Message, StringComparison.Ordinal);
        Assert.Equal(theirs, RemoteHead());
    }

    [Fact]
    public async Task Push_from_a_detached_HEAD_is_refused()
    {
        _mine.Git("checkout", "--quiet", "--detach");

        var result = await _git.PushAsync(_mine.Path, new PushRequest(), null, Ct);

        Assert.StartsWith("HEAD est détaché", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canceled_operation_returns_canceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _git.FetchAsync(_mine.Path, null, true, cts.Token);

        Assert.IsType<Canceled>(result.Value);
    }
}
