using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Git.Cli;
using Vigo.Git.Watching;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class RepositoryWatcherTests
{
    private const string GitDir = @"C:\repo\.git\";

    [Theory]
    [InlineData(@"C:\repo\src\a.cs", true)]
    [InlineData(@"C:\repo\.gitignore", true)]
    [InlineData(@"C:\repo\.git", false)]
    [InlineData(@"C:\repo\.git\objects\ab\cdef", false)]
    [InlineData(@"C:\repo\.git\index", false)]
    public void Working_directory_events_inside_the_git_directory_are_ignored(string path, bool relevant) =>
        Assert.Equal(relevant, RepositoryWatcher.IsWorkTreePathRelevant(path, GitDir));

    [Theory]
    [InlineData(@"C:\repo\.git\index", false, false)]
    [InlineData(@"C:\repo\.git\HEAD", false, false)]
    [InlineData(@"C:\repo\.git\worktrees\w\index", false, false)]
    [InlineData(@"C:\repo\.git\packed-refs", true, true)]
    [InlineData(@"C:\repo\.git\refs\heads\main", true, true)]
    [InlineData(@"C:\repo\.git\refs\remotes\origin\main", false, true)]
    public void Common_directory_events_only_matter_for_refs(string path, bool status, bool refs)
    {
        var change = RepositoryWatcher.ClassifyCommonDirectoryPath(path, GitDir);

        Assert.Equal(status, change.HasFlag(WatchedChange.Status));
        Assert.Equal(refs, change.HasFlag(WatchedChange.Refs));
    }

    [Fact]
    public void A_linked_worktree_resolves_its_common_directory_and_a_normal_repository_has_none()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        var worktree = Path.Combine(repo.Path, "..", "wt-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            repo.Git("worktree", "add", "-b", "linked", worktree);
            var gitDirectory = Directory.GetDirectories(Path.Combine(repo.Path, ".git", "worktrees")).Single();

            Assert.Equal(Path.GetFullPath(Path.Combine(repo.Path, ".git")), RepositoryWatcher.ResolveCommonDirectory(gitDirectory), ignoreCase: true);
            Assert.Null(RepositoryWatcher.ResolveCommonDirectory(Path.Combine(repo.Path, ".git")));
        }
        finally
        {
            if (Directory.Exists(worktree)) Directory.Delete(worktree, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\repo\.git\index", true, false)]
    [InlineData(@"C:\repo\.git\HEAD", true, true)]
    [InlineData(@"C:\repo\.git\packed-refs", true, true)]
    [InlineData(@"C:\repo\.git\refs\heads\feature\x", true, true)]
    [InlineData(@"C:\repo\.git\refs\remotes\origin\main", false, true)]
    [InlineData(@"C:\repo\.git\refs\tags\v1.0", false, true)]
    [InlineData(@"C:\repo\.git\refs\stash", false, true)]
    [InlineData(@"C:\repo\.git\logs\refs\stash", false, true)]
    [InlineData(@"C:\repo\.git\logs\HEAD", false, false)]
    [InlineData(@"C:\repo\.git\index.lock", false, false)]
    [InlineData(@"C:\repo\.git\refs\heads\main.lock", false, false)]
    [InlineData(@"C:\repo\.git\objects\pack\x.pack", false, false)]
    [InlineData(@"C:\repo\.git\FETCH_HEAD", false, false)]
    public void Git_directory_events_are_classified_as_status_or_refs_changes(string path, bool status, bool refs)
    {
        var change = RepositoryWatcher.ClassifyGitDirectoryPath(path, GitDir);

        Assert.Equal(status, change.HasFlag(WatchedChange.Status));
        Assert.Equal(refs, change.HasFlag(WatchedChange.Refs));
    }

    [Fact]
    public void Status_and_refs_events_of_a_burst_are_raised_once_each()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var watcher = new RepositoryWatcher(GitDir, time, NullLogger.Instance);
        var (status, refs) = (0, 0);
        watcher.Changed += (_, _) => status++;
        watcher.RefsChanged += (_, _) => refs++;

        watcher.Signal(WatchedChange.Refs);
        time.Advance(RepositoryWatcher.QuietPeriod);
        Assert.Equal((0, 1), (status, refs));

        watcher.Signal(WatchedChange.Status);
        watcher.Signal(WatchedChange.Status | WatchedChange.Refs);
        watcher.Signal(WatchedChange.None);
        time.Advance(RepositoryWatcher.QuietPeriod);
        Assert.Equal((1, 2), (status, refs));
    }

    [Fact]
    public void A_burst_of_events_raises_one_change_after_a_quiet_period()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var watcher = new RepositoryWatcher(GitDir, time, NullLogger.Instance);
        var changes = 0;
        watcher.Changed += (_, _) => changes++;

        for (var i = 0; i < 5; i++)
        {
            watcher.Signal();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Equal(0, changes);

        time.Advance(RepositoryWatcher.QuietPeriod);
        Assert.Equal(1, changes);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Continuous_events_still_raise_a_change_every_max_delay()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var watcher = new RepositoryWatcher(GitDir, time, NullLogger.Instance);
        var changes = 0;
        watcher.Changed += (_, _) => changes++;

        // An event every 100 ms for 5 s (a build): never quiet, but refreshed at least every MaxDelay.
        for (var i = 0; i < 50; i++)
        {
            watcher.Signal();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.InRange(changes, 2, 3);
    }

    [Fact]
    public void Disposed_watcher_raises_nothing()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var watcher = new RepositoryWatcher(GitDir, time, NullLogger.Instance);
        var changes = 0;
        watcher.Changed += (_, _) => changes++;

        watcher.Signal();
        watcher.Dispose();
        watcher.Signal();
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task Writing_a_file_or_staging_it_raises_a_change()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        var info = new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false);
        using var watcher = new RepositoryWatcherFactory(TimeProvider.System, NullLogger<RepositoryWatcher>.Instance).Watch(info);
        Assert.NotNull(watcher);

        var changed = new SemaphoreSlim(0);
        watcher.Changed += (_, _) => changed.Release();

        repo.WriteFile("a.txt", "b\n");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "working-directory change");

        // Let the burst settle, then stage: only the index changes.
        await Task.Delay(RepositoryWatcher.QuietPeriod * 2, TestContext.Current.CancellationToken);
        while (changed.CurrentCount > 0) await changed.WaitAsync(TestContext.Current.CancellationToken);
        repo.Stage("a.txt");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "index change");
    }

    [Fact]
    public async Task Creating_a_tag_raises_a_refs_change_only()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        var info = new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false);
        using var watcher = new RepositoryWatcherFactory(TimeProvider.System, NullLogger<RepositoryWatcher>.Instance).Watch(info);
        Assert.NotNull(watcher);
        var refs = new SemaphoreSlim(0);
        var status = 0;
        watcher.RefsChanged += (_, _) => refs.Release();
        watcher.Changed += (_, _) => Interlocked.Increment(ref status);

        repo.Tag("v1.0");

        Assert.True(await refs.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "refs change");
        Assert.Equal(0, Volatile.Read(ref status));
    }

    [Theory]
    [InlineData("bin/x.dll", true)]
    [InlineData("src/obj/y.cache", true)]
    [InlineData("bin", true)]
    [InlineData("src/a.cs", false)]
    [InlineData("binary/x.txt", false)]
    [InlineData("src/bin/x.dll", false)]
    public void Events_under_an_ignored_directory_are_recognized(string relative, bool ignored) =>
        Assert.Equal(ignored, RepositoryWatcher.IsInIgnoredDirectory(relative, new HashSet<string>(["bin/", "src/obj/"], StringComparer.OrdinalIgnoreCase)));

    [Fact]
    public async Task A_build_under_an_ignored_directory_raises_no_change_but_a_source_edit_does()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"), (".gitignore", "bin/\n"));
        repo.WriteFile("bin/old.dll", "x\n"); // an ignored directory is only known once it exists
        var info = new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false);
        using var watcher = new RepositoryWatcherFactory(TimeProvider.System, NullLogger<RepositoryWatcher>.Instance, new GitCli()).Watch(info);
        Assert.NotNull(watcher);
        var changes = 0;
        var changed = new SemaphoreSlim(0);
        watcher.Changed += (_, _) =>
        {
            Interlocked.Increment(ref changes);
            changed.Release();
        };
        // The list of ignored directories is read in the background.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        repo.WriteFile("bin/out.dll", "x\n");
        await Task.Delay(RepositoryWatcher.QuietPeriod * 4, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref changes));

        repo.WriteFile("a.txt", "b\n");
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "source edit");
    }

    [Fact]
    public void Bare_repository_is_not_watched()
    {
        using var repo = TestRepoBuilder.Create(bare: true);
        var info = new RepositoryInfo(null, repo.Path, "repo", null, null, true, false, true);

        Assert.Null(new RepositoryWatcherFactory(TimeProvider.System, NullLogger<RepositoryWatcher>.Instance).Watch(info));
    }
}
