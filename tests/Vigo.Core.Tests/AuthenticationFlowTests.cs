using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>
/// Refused fetch → dialog → choice applied → operation retried, against a local server that always answers 401.
/// The repository resets its credential helper list locally: the machine's credential manager is never called.
/// </summary>
public sealed class AuthenticationFlowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly UnauthorizedGitServer _server = new();
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();
    private readonly ScriptedPrompt _prompt = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly string _store;

    public AuthenticationFlowTests()
    {
        _repo.Commit("init", ("a.txt", "a\n"));
        _repo.Git("remote", "add", "origin", _server.Url);
        _store = Path.Combine(_repo.Path, ".git", "test-credentials").Replace('\\', '/');
        _repo.Git("config", "--add", "credential.helper", "");
        _repo.Git("config", "--add", "credential.helper", $"store --file=\"{_store}\"");
    }

    public void Dispose()
    {
        _repo.Dispose();
        _server.Dispose();
    }

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var tab = new RepositoryTabViewModel(
            new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false),
            _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, confirmations: null, _prompt);
        await tab.LoadAsync();
        return tab;
    }

    [Fact]
    public async Task Canceled_dialog_leaves_a_toast()
    {
        using var tab = await OpenAsync();

        await tab.Remote.FetchCommand.ExecuteAsync(null);

        var problem = Assert.Single(_prompt.Problems);
        Assert.Equal(("git fetch --all", "origin", "127.0.0.1", AuthenticationCause.NoCredentials),
            (problem.Command, problem.RemoteName, problem.Url?.Host, problem.Cause));
        Assert.Equal("team/project", problem.Url!.DisplayPath);
        Assert.StartsWith("store --file=", problem.CredentialHelper, StringComparison.Ordinal);
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Error, Title: "Échec d'authentification" });
        Assert.False(tab.Remote.IsRunning);
    }

    [Fact]
    public async Task Token_is_stored_then_the_operation_retried()
    {
        _prompt.Choices.Enqueue(new UseAccessToken("camille", "tok_123"));
        using var tab = await OpenAsync();

        await tab.Remote.FetchCommand.ExecuteAsync(null);

        // Sent with the token, refused again (the server never accepts): asked a second time, then canceled.
        Assert.Equal(2, _prompt.Problems.Count);
        Assert.True(_server.AuthorizedAttempts > 0);
        Assert.Equal(AuthenticationCause.CredentialsRejected, _prompt.Problems[1].Cause);
    }

    [Fact]
    public async Task Switching_the_remote_url_makes_the_retry_succeed()
    {
        using var origin = TestRepoBuilder.Create(bare: true);
        _repo.Git("push", "--quiet", origin.Path, "main");
        _prompt.Choices.Enqueue(new SwitchRemoteUrl("origin", origin.Path));
        using var tab = await OpenAsync();

        await tab.Remote.FetchCommand.ExecuteAsync(null);

        Assert.Single(_prompt.Problems);
        Assert.Equal(origin.Path, _repo.Git("remote", "get-url", "origin").Trim());
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Success, Title: "Récupération terminée" });
        Assert.Contains(_notifier.Items, n => n is { Title: "Distant origin modifié" });
    }

    [Fact]
    public async Task Endless_retries_stop_after_a_few_attempts()
    {
        for (var i = 0; i < 10; i++) _prompt.Choices.Enqueue(RetryAuthentication.Instance);
        using var tab = await OpenAsync();

        await tab.Remote.FetchCommand.ExecuteAsync(null);

        Assert.Equal(5, _prompt.Problems.Count);
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Error, Title: "Échec d'authentification" });
    }

    /// <summary>Returns the queued choices in order, then cancels.</summary>
    private sealed class ScriptedPrompt : IAuthenticationPrompt
    {
        public Queue<AuthenticationChoice> Choices { get; } = [];

        public List<AuthenticationProblem> Problems { get; } = [];

        public Task<AuthenticationChoice?> ResolveAsync(AuthenticationProblem problem, CancellationToken cancellationToken = default)
        {
            Problems.Add(problem);
            return Task.FromResult(Choices.TryDequeue(out var choice) ? choice : (AuthenticationChoice?)null);
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
