using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>
/// The account token handed to git as an <c>Authorization</c> header (J6.16, ADR-085), with the credential helpers as the fallback.
/// Every repository resets its helper list locally so that the machine's Git Credential Manager is never called.
/// </summary>
public sealed class HttpCredentialsTests : IDisposable
{
    private readonly UnauthorizedGitServer _server = new();
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();

    public HttpCredentialsTests()
    {
        _repo.Commit("init", ("a.txt", "a\n"));
        _repo.Git("remote", "add", "origin", _server.Url);
        _repo.Git("config", "--add", "credential.helper", "");
    }

    public void Dispose()
    {
        _repo.Dispose();
        _server.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeCredentials(params GitHttpHeader[] headers) : IGitHttpCredentials
    {
        public List<string?> Rejections { get; } = [];

        public List<string?> Asked { get; } = [];

        public Task<IReadOnlyList<GitHttpHeader>> GetHeadersAsync(string? repositoryPath, CancellationToken cancellationToken)
        {
            Asked.Add(repositoryPath);
            return Task.FromResult<IReadOnlyList<GitHttpHeader>>(headers);
        }

        public void ReportRejected(string? repositoryPath, IReadOnlyList<GitHttpHeader> sent) => Rejections.Add(repositoryPath);
    }

    private static GitHttpHeader HeaderFor(string url) => new(HostingProvider.GitHub, [url], "Authorization: Basic dG9rZW4=");

    [Fact]
    public async Task The_header_goes_to_the_matching_host_before_any_challenge_and_a_refusal_runs_the_command_again_without_it()
    {
        var credentials = new FakeCredentials(HeaderFor(_server.Origin));
        var git = new GitService(new GitCli { HttpCredentials = credentials });

        var result = await git.FetchAsync(_repo.Path, null, true, Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
        var requests = _server.Authorizations;
        Assert.Equal("Basic dG9rZW4=", requests[0]);
        Assert.Contains(null, requests.Skip(1));
        Assert.Equal([_repo.Path], credentials.Rejections);
    }

    [Fact]
    public async Task The_header_is_not_sent_to_another_host()
    {
        var credentials = new FakeCredentials(HeaderFor("https://github.com/"));
        var git = new GitService(new GitCli { HttpCredentials = credentials });

        var result = await git.FetchAsync(_repo.Path, null, true, Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.All(_server.Authorizations, a => Assert.Null(a));
        Assert.Equal([_repo.Path], credentials.Rejections);
    }

    [Fact]
    public async Task A_background_fetch_gets_the_header_but_reports_nothing_when_it_is_refused()
    {
        var credentials = new FakeCredentials(HeaderFor(_server.Origin));
        var git = new GitService(new GitCli { HttpCredentials = credentials });

        var result = await git.FetchInBackgroundAsync(_repo.Path, true, Ct);

        Assert.IsType<AuthenticationFailed>(result.Value);
        Assert.Equal("Basic dG9rZW4=", _server.Authorizations[0]);
        Assert.Empty(credentials.Rejections);
    }

    [Fact]
    public async Task Commands_that_stay_local_do_not_ask_for_credentials()
    {
        var credentials = new FakeCredentials(HeaderFor(_server.Origin));
        var git = new GitService(new GitCli { HttpCredentials = credentials });

        await git.GetStatusAsync(_repo.Path, Ct);

        Assert.Empty(credentials.Asked);
    }
}
