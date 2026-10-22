using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Clone from a local repository, destination checks and cleanup; automatic fetch.</summary>
public sealed class CloneTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _source = TestRepoBuilder.Create();
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "vigo-tests", "clone-" + Guid.NewGuid().ToString("N")[..12]);

    public CloneTests()
    {
        _source.Commit("first", ("a.txt", "a\n"));
        _source.Commit("second", ("b.txt", "b\n"));
        Directory.CreateDirectory(_parent);
    }

    public void Dispose()
    {
        _source.Dispose();
        if (!Directory.Exists(_parent)) return;
        foreach (var file in Directory.EnumerateFiles(_parent, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_parent, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorder : IProgress<GitProgress>
    {
        public List<GitProgress> Items { get; } = [];

        public void Report(GitProgress value)
        {
            lock (Items) Items.Add(value);
        }
    }

    [Fact]
    public async Task Clone_creates_the_destination_and_opens_the_repository()
    {
        var destination = Path.Combine(_parent, "nested", "copy");
        var progress = new Recorder();

        var result = await _git.CloneAsync(new CloneRequest(_source.Path, destination), progress, Ct);

        var info = Assert.IsType<Success<RepositoryInfo>>(result.Value).Value;
        Assert.Equal(destination, Path.TrimEndingDirectorySeparator(info.WorkingDirectory!), ignoreCase: true);
        Assert.Equal("main", info.HeadName);
        Assert.True(File.Exists(Path.Combine(destination, "b.txt")));
        Assert.Equal(_source.Head(), TestRepoBuilder.Attach(destination).Head());
    }

    [Fact]
    public async Task Shallow_clone_keeps_only_the_last_commit()
    {
        var destination = Path.Combine(_parent, "shallow");
        var url = new Uri(_source.Path).AbsoluteUri;

        var result = await _git.CloneAsync(new CloneRequest(url, destination, RecurseSubmodules: false, Shallow: true), null, Ct);

        Assert.IsType<Success<RepositoryInfo>>(result.Value);
        Assert.Equal("1", TestRepoBuilder.Attach(destination).Git("rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task Existing_empty_folder_is_used_but_a_non_empty_one_is_refused()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_parent, "empty")).FullName;
        Assert.IsType<Success<RepositoryInfo>>((await _git.CloneAsync(new CloneRequest(_source.Path, empty), null, Ct)).Value);

        var full = Directory.CreateDirectory(Path.Combine(_parent, "full")).FullName;
        File.WriteAllText(Path.Combine(full, "keep.txt"), "mine");

        var refused = Assert.IsType<Failure>((await _git.CloneAsync(new CloneRequest(_source.Path, full), null, Ct)).Value);

        Assert.Contains("n'est pas vide", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Failed_clone_removes_what_it_created_and_keeps_an_existing_folder()
    {
        var missing = Path.Combine(_parent, "does-not-exist");
        var created = Path.Combine(_parent, "created");
        var existing = Directory.CreateDirectory(Path.Combine(_parent, "existing")).FullName;

        var first = await _git.CloneAsync(new CloneRequest(missing, created), null, Ct);
        var second = await _git.CloneAsync(new CloneRequest(missing, existing), null, Ct);

        Assert.IsType<Failure>(first.Value);
        Assert.IsType<Failure>(second.Value);
        Assert.False(Directory.Exists(created));
        Assert.True(Directory.Exists(existing));
        Assert.Empty(Directory.EnumerateFileSystemEntries(existing));
    }

    [Theory]
    [InlineData("", @"C:\x")]
    [InlineData("--upload-pack=evil", @"C:\x")]
    [InlineData("https://example.com/a.git", "relative")]
    public async Task Invalid_requests_are_refused_before_running_git(string url, string directory)
    {
        var result = await _git.CloneAsync(new CloneRequest(url, directory), null, Ct);

        Assert.IsType<Failure>(result.Value);
    }

    [Fact]
    public async Task Canceled_clone_reports_canceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await cts.CancelAsync();

        var result = await _git.CloneAsync(new CloneRequest(_source.Path, Path.Combine(_parent, "canceled")), null, cts.Token);

        Assert.IsType<Canceled>(result.Value);
        Assert.False(Directory.Exists(Path.Combine(_parent, "canceled")));
    }

    [Fact]
    public async Task Background_fetch_updates_remote_branches_without_prompting()
    {
        var clone = TestRepoBuilder.Clone(_source.Path);
        try
        {
            var head = _source.Commit("third", ("c.txt", "c\n"));

            var result = await _git.FetchInBackgroundAsync(clone.Path, true, Ct);

            Assert.IsType<Done>(result.Value);
            Assert.Equal(head, clone.Git("rev-parse", "refs/remotes/origin/main").Trim());
        }
        finally
        {
            clone.Dispose();
        }
    }

    [Fact]
    public async Task Background_fetch_refused_by_the_server_fails_at_once()
    {
        using var server = new UnauthorizedGitServer();
        using var repo = TestRepoBuilder.Create();
        repo.Git("remote", "add", "origin", server.Url);
        repo.Git("config", "--add", "credential.helper", "");

        var result = await _git.FetchInBackgroundAsync(repo.Path, true, Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
    }
}
