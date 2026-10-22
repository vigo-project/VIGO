using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>
/// Authentication failures and credential storage. Every repository resets its credential helper list locally
/// (empty <c>credential.helper</c>) so that the machine's Git Credential Manager is never called.
/// </summary>
public sealed class AuthenticationTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly UnauthorizedGitServer _server = new();
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();
    private readonly string _store;

    public AuthenticationTests()
    {
        _repo.Commit("init", ("a.txt", "a\n"));
        _repo.Git("remote", "add", "origin", _server.Url);
        _repo.Git("config", "--add", "credential.helper", "");
        _store = Path.Combine(_repo.Path, ".git", "test-credentials").Replace('\\', '/');
    }

    public void Dispose()
    {
        _repo.Dispose();
        _server.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void UseStoreHelper() => _repo.Git("config", "--add", "credential.helper", $"store --file=\"{_store}\"");

    [Fact]
    public async Task Missing_credentials_give_the_refused_url()
    {
        var result = await _git.FetchAsync(_repo.Path, null, true, Ct);

        var auth = Assert.IsType<AuthenticationFailed>(result.Value);
        // A single remote: "fetch --all" does not name it; the view model finds it by URL.
        Assert.StartsWith(_server.Url[..^"/team/project.git".Length], auth.Url, StringComparison.Ordinal);
        Assert.Equal(AuthenticationCause.NoCredentials, AuthenticationProblem.CauseOf(auth.Details!));
    }

    [Fact]
    public async Task Stored_token_is_sent_and_a_refusal_is_classified()
    {
        UseStoreHelper();
        var url = RemoteUrl.TryParse(_server.Url)!;

        var stored = await _git.StoreCredentialAsync(_repo.Path, url, "camille", "tok_123", Ct);

        Assert.IsType<Done>(stored.Value);
        Assert.Contains($"camille:tok_123@{url.Host}", File.ReadAllText(_store), StringComparison.Ordinal);

        var result = await _git.PushAsync(_repo.Path, new PushRequest(), null, Ct);

        var auth = Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.True(_server.AuthorizedAttempts > 0);
        Assert.Equal(AuthenticationCause.CredentialsRejected, AuthenticationProblem.CauseOf(auth.Details!));
        Assert.Contains("/team/project.git", auth.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("camille", "tok\nhost=evil.example")]
    [InlineData("cam\rille", "tok")]
    [InlineData("camille", "")]
    public async Task Credential_values_that_could_inject_attributes_are_refused(string user, string secret)
    {
        UseStoreHelper();

        var result = await _git.StoreCredentialAsync(_repo.Path, RemoteUrl.TryParse(_server.Url)!, user, secret, Ct);

        Assert.IsType<Failure>(result.Value);
        Assert.False(File.Exists(_store));
    }

    [Fact]
    public async Task Storing_without_a_credential_helper_fails()
    {
        var result = await _git.StoreCredentialAsync(_repo.Path, RemoteUrl.TryParse(_server.Url)!, "camille", "tok", Ct);

        Assert.Contains("credential.helper", Assert.IsType<Failure>(result.Value).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remotes_are_listed_and_their_url_replaced()
    {
        _repo.Git("remote", "add", "upstream", "https://github.com/atlas/api.git");
        _repo.Git("remote", "set-url", "--push", "upstream", "git@github.com:atlas/api.git");

        var remotes = Assert.IsType<Success<IReadOnlyList<GitRemote>>>((await _git.GetRemotesAsync(_repo.Path, Ct)).Value).Value;
        Assert.Equal(
            [new GitRemote("origin", _server.Url), new GitRemote("upstream", "https://github.com/atlas/api.git", "git@github.com:atlas/api.git")],
            remotes);

        Assert.IsType<Done>((await _git.SetRemoteUrlAsync(_repo.Path, "origin", "git@example.com:team/project.git", Ct)).Value);
        Assert.Equal("git@example.com:team/project.git", _repo.Git("remote", "get-url", "origin").Trim());
        Assert.IsType<Failure>((await _git.SetRemoteUrlAsync(_repo.Path, "--evil", "x", Ct)).Value);
    }

    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/atlas/api.git/': The requested URL returned error: 403", "https://github.com/atlas/api.git/", AuthenticationCause.AccessDenied)]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", "git@github.com", AuthenticationCause.SshKeyRejected)]
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.", null, AuthenticationCause.SshHostUnknown)]
    [InlineData("remote: Invalid username or token.\nfatal: Authentication failed for 'https://github.com/atlas/api.git/'", "https://github.com/atlas/api.git/", AuthenticationCause.CredentialsRejected)]
    public void Authentication_output_is_classified_with_its_url(string stderr, string? url, AuthenticationCause cause)
    {
        var result = GitCli.Classify(new CliOutput(128, "", stderr + "\nerror: could not fetch upstream\n"), ["fetch", "--all", "--progress"]);

        var auth = Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.Equal(url, auth.Url);
        Assert.Equal("upstream", auth.Remote);
        Assert.Equal(cause, AuthenticationProblem.CauseOf(auth.Details!));
    }
}
