using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

public sealed class CommitMessageGeneratorTests : IDisposable
{
    private readonly InMemoryPreferencesStore _store = new();
    private readonly Preferences _preferences;
    private readonly FakeGit _git = new();

    public CommitMessageGeneratorTests() => _preferences = new Preferences(_store, new UserPreferences());

    public void Dispose() => _preferences.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeBackend(AiProvider provider, GitResult<string> reply) : IAiBackend
    {
        public AiProvider Provider => provider;

        public List<AiRequest> Requests { get; } = [];

        public Task<GitResult> CheckAsync(AiRequest settings, CancellationToken cancellationToken = default) => Task.FromResult<GitResult>(Done.Instance);

        public Task<GitResult<string>> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(reply);
        }
    }

    private sealed class FakeVault : ISecretVault
    {
        public Dictionary<string, string> Secrets { get; } = [];

        public string? Retrieve(string name) => Secrets.GetValueOrDefault(name);

        public void Store(string name, string secret) => Secrets[name] = secret;

        public void Remove(string name) => Secrets.Remove(name);
    }

    private CommitMessageGenerator Create(params IAiBackend[] backends) => new(_preferences, _git, new FakeVault(), backends);

    [Theory]
    [InlineData("Fix the thing", "Fix the thing", "")]
    [InlineData("```\nFix the thing\n\nBecause.\n```", "Fix the thing", "Because.")]
    [InlineData("Commit message: Fix the thing", "Fix the thing", "")]
    [InlineData("\"Fix the thing\"", "Fix the thing", "")]
    [InlineData("Fix\r\n\r\nBody line 1\r\nBody line 2\r\n", "Fix", "Body line 1\nBody line 2")]
    public void Parses_replies_of_engines(string reply, string summary, string description)
    {
        var message = CommitMessagePrompt.Parse(reply);

        Assert.Equal(new GeneratedCommitMessage(summary, description), message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("```\n```")]
    public void Empty_replies_are_rejected(string reply) => Assert.Null(CommitMessagePrompt.Parse(reply));

    [Fact]
    public void Prompt_holds_the_patch_the_subjects_and_the_truncation_note()
    {
        var prompt = CommitMessagePrompt.Build(new StagedPatch("a.txt | 1 +\n+x", true, ["Add a", "Fix b"]));

        Assert.Contains("+x", prompt, StringComparison.Ordinal);
        Assert.Contains("- Add a", prompt, StringComparison.Ordinal);
        Assert.Contains("truncated", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Off_by_default_nothing_is_sent()
    {
        var backend = new FakeBackend(AiProvider.Cli, new Success<string>("x"));
        var sut = Create(backend);
        _git.StagedPatch = new StagedPatch("diff", false, []);

        Assert.False(sut.IsEnabled);
        Assert.IsType<Failure>((await sut.GenerateAsync("repo", Ct)).Value);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task Uses_the_chosen_backend_with_the_command_of_the_chosen_tool_and_the_model()
    {
        var api = new FakeBackend(AiProvider.Api, new Success<string>("api"));
        var copilot = new FakeBackend(AiProvider.Cli,new Success<string>("Add feature\n\nWhy."));
        var sut = Create(api, copilot);
        _git.StagedPatch = new StagedPatch("diff", false, []);
        await _preferences.UpdateAsync(p => p with
        {
            AiProvider = AiProvider.Cli, AiCliTool = AiCliTool.Copilot, AiCliCommand = "claude -p", AiCopilotCommand = "copilot -p {prompt}", AiModel = "m1",
        }, Ct);

        var result = await sut.GenerateAsync("repo", Ct);

        Assert.Equal(new GeneratedCommitMessage("Add feature", "Why."), Assert.IsType<Success<GeneratedCommitMessage>>(result.Value).Value);
        Assert.Empty(api.Requests);
        var request = Assert.Single(copilot.Requests);
        Assert.Equal("copilot -p {prompt}", request.CliCommand);
        Assert.Equal(AiCliTool.Copilot, request.Tool);
        Assert.Equal("m1", request.Model);
        Assert.Contains("diff", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_staged_is_a_failure_without_calling_the_backend()
    {
        var backend = new FakeBackend(AiProvider.Cli, new Success<string>("x"));
        var sut = Create(backend);
        await _preferences.UpdateAsync(p => p with { AiProvider = AiProvider.Cli }, Ct);

        Assert.IsType<Failure>((await sut.GenerateAsync("repo", Ct)).Value);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task Backend_failures_and_empty_replies_are_reported()
    {
        _git.StagedPatch = new StagedPatch("diff", false, []);
        await _preferences.UpdateAsync(p => p with { AiProvider = AiProvider.Cli }, Ct);

        Assert.IsType<Failure>((await Create(new FakeBackend(AiProvider.Cli, new Failure("boom"))).GenerateAsync("repo", Ct)).Value);
        Assert.IsType<Failure>((await Create(new FakeBackend(AiProvider.Cli, new Success<string>("  "))).GenerateAsync("repo", Ct)).Value);
        Assert.IsType<AuthenticationFailed>(
            (await Create(new FakeBackend(AiProvider.Cli, new AuthenticationFailed("x", "no"))).GenerateAsync("repo", Ct)).Value);
    }

    [Fact]
    public void The_api_key_goes_through_the_vault()
    {
        var vault = new FakeVault();
        var sut = new CommitMessageGenerator(_preferences, _git, vault, []);

        Assert.False(sut.HasApiKey);
        sut.SaveApiKey("  sk-test \n");
        Assert.True(sut.HasApiKey);
        Assert.Equal("sk-test", vault.Secrets[SecretNames.AnthropicApiKey]);
        sut.RemoveApiKey();
        Assert.False(sut.HasApiKey);
    }

    [Fact]
    public async Task Preferences_normalize_the_ai_settings()
    {
        await _preferences.UpdateAsync(p => p with { AiCliCommand = "  ", AiModel = " m ", AiCopilotCommand = "" }, Ct);

        Assert.Null(_preferences.Current.AiCliCommand);
        Assert.Equal("m", _preferences.Current.AiModel);
        Assert.Null(_preferences.Current.AiCopilotCommand);
    }
}
