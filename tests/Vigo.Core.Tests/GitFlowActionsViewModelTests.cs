using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class GitFlowActionsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly GitSha A = GitSha.Parse(new string('a', 40));
    private const string RepoPath = @"C:\repos\flow";
    private static readonly GitFlowConfig Config = GitFlowConfig.Defaults();

    private readonly FakeGit _git = new();
    private readonly FakeInputs _inputs = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly RecordingNotifier _notifier = new();

    private static GitRef Local(string name, bool current = false) =>
        new($"refs/heads/{name}", name, GitRefKind.LocalBranch, A) { IsCurrent = current };

    private async Task<RepositoryTabViewModel> OpenAsync(string current = "develop", params string[] others)
    {
        string[] names = ["main", "develop", .. others, current];
        _git.Refs = new RepositoryRefs([.. names.Distinct().Select(b => Local(b, b == current))], A, current);
        var info = new RepositoryInfo(RepoPath, Path.Combine(RepoPath, ".git"), "flow", current, null, false, false, false);
        _git.Repos[RepoPath] = new Success<RepositoryInfo>(info);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, _notifier, _confirmations, authentication: null, _inputs);
        await tab.LoadAsync();
        return tab;
    }

    [Fact]
    public async Task A_repository_that_is_not_initialized_has_no_section_and_offers_the_initialization()
    {
        using var tab = await OpenAsync();

        Assert.False(tab.GitFlow.IsInitialized);
        Assert.True(tab.GitFlow.CanInitialize);
        Assert.DoesNotContain(tab.Refs.Rows, r => r.Row.Section == RefSection.GitFlow);
        Assert.True(tab.Refs.MenuFor(tab.Refs.Rows.Single(r => r.Row is { Kind: RefRowKind.Section, Section: RefSection.Local }).Row)!.CanInitGitFlow);
        Assert.Contains(tab.GetPaletteEntries(), e => e.Label == "Initialiser Git Flow…");
    }

    [Fact]
    public async Task Initializing_asks_for_the_two_branches_and_shows_the_section()
    {
        using var tab = await OpenAsync();
        _inputs.Answer = d =>
        {
            d.Name = "main";
            d.SecondText = "dev";
        };

        await tab.GitFlow.InitializeCommand.ExecuteAsync(null);

        var config = Assert.Single(_git.GitFlowInits);
        Assert.Equal(GitFlowConfig.Defaults() with { Develop = "dev" }, config);
        Assert.True(tab.GitFlow.IsInitialized);
        Assert.False(tab.GitFlow.CanInitialize);
        Assert.Contains(tab.Refs.Rows, r => r.Row is { Kind: RefRowKind.Section, Section: RefSection.GitFlow, Label: "Git Flow" });

        // The Git Flow section sits above the local branches.
        var sections = tab.Refs.Rows.Where(r => r.Row.Kind == RefRowKind.Section).Select(r => r.Row.Section).ToList();
        Assert.True(sections.IndexOf(RefSection.GitFlow) < sections.IndexOf(RefSection.Local));
    }

    [Fact]
    public async Task Initializing_is_refused_when_both_branches_are_the_same()
    {
        using var tab = await OpenAsync();
        _inputs.Answer = d =>
        {
            d.Name = "main";
            d.SecondText = "main";
        };

        await tab.GitFlow.InitializeCommand.ExecuteAsync(null);

        Assert.Empty(_git.GitFlowInits);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning);
    }

    [Fact]
    public async Task The_section_lists_the_branches_in_progress_by_kind()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync("feature/login", "release/1.0.0", "hotfix/1.0.1", "feature/export", "topic");

        var rows = tab.Refs.Rows.Select(r => r.Row).Where(r => r.Section == RefSection.GitFlow).ToList();

        Assert.Equal(["Git Flow", "export", "login", "1.0.0", "1.0.1"], rows.Select(r => r.Label));
        Assert.Equal(["4", "feature", "feature", "release", "hotfix"], rows.Select(r => r.Meta));
        Assert.True(rows.Single(r => r.Label == "login").IsHead);
        Assert.All(rows.Skip(1), r => Assert.Equal(RefRowKind.FlowBranch, r.Kind));
    }

    [Fact]
    public async Task An_empty_section_says_so()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync();

        var hint = Assert.Single(tab.Refs.Rows, r => r.Row.Key == "hint:gitflow");
        Assert.Equal(RefRowKind.Hint, hint.Row.Kind);
    }

    [Fact]
    public async Task Starting_asks_for_a_name_and_runs_the_start()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync();
        _inputs.Answer = d => d.Name = "login";

        await tab.GitFlow.StartCommand.ExecuteAsync(GitFlowKind.Feature);

        Assert.Equal(new GitFlowStartRequest(GitFlowKind.Feature, "login"), Assert.Single(_git.GitFlowStarts));
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Success, Message: "feature/login est créée et extraite." });
    }

    [Fact]
    public async Task Starting_rejects_a_name_whose_branch_exists_and_a_dismissed_dialog()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync("develop", "feature/login");
        _inputs.Answer = d => d.Name = "login";
        await tab.GitFlow.StartCommand.ExecuteAsync(GitFlowKind.Feature);

        _inputs.Confirm = false;
        _inputs.Answer = d => d.Name = "other";
        await tab.GitFlow.StartCommand.ExecuteAsync(GitFlowKind.Feature);

        Assert.Empty(_git.GitFlowStarts);
    }

    [Fact]
    public async Task Starting_does_nothing_without_initialization()
    {
        using var tab = await OpenAsync();
        _inputs.Answer = d => d.Name = "login";

        await tab.GitFlow.StartCommand.ExecuteAsync(GitFlowKind.Feature);

        Assert.Empty(_git.GitFlowStarts);
    }

    [Fact]
    public async Task Finishing_confirms_then_reports_what_was_merged()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync("feature/login");

        await tab.GitFlow.FinishCurrentCommand.ExecuteAsync(null);

        Assert.Equal(new GitFlowFinishRequest(GitFlowKind.Feature, "login", false), Assert.Single(_git.GitFlowFinishes));
        Assert.Equal("Terminer feature login ?", Assert.Single(_confirmations.Requests).Title);
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Success, Message: "feature/login est fusionnée dans develop, branche supprimée." });
    }

    [Fact]
    public async Task Finishing_a_release_mentions_the_tag_and_can_keep_the_branch()
    {
        _git.GitFlow = Config with { VersionTagPrefix = "v" };
        using var tab = await OpenAsync("release/1.0.0");
        _confirmations.OptionChecked = true;

        await tab.GitFlow.FinishCurrentCommand.ExecuteAsync(null);

        Assert.Equal(new GitFlowFinishRequest(GitFlowKind.Release, "1.0.0", true), Assert.Single(_git.GitFlowFinishes));
        Assert.Contains("v1.0.0", Assert.Single(_confirmations.Requests).Message);
        Assert.Contains(_notifier.Items, n => n.Message == "release/1.0.0 est fusionnée dans develop, tag v1.0.0.");
    }

    [Fact]
    public async Task Declining_the_confirmation_finishes_nothing()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync("feature/login");
        _confirmations.Confirm = false;

        await tab.GitFlow.FinishCurrentCommand.ExecuteAsync(null);

        Assert.Empty(_git.GitFlowFinishes);
    }

    [Fact]
    public async Task A_conflict_is_reported_with_what_is_left_to_do()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync("feature/login");
        _git.GitFlowFinishResult = new Conflict(["a.txt"], "merge");

        await tab.GitFlow.FinishCurrentCommand.ExecuteAsync(null);

        var warning = Assert.Single(_notifier.Items, n => n.Severity == NotificationSeverity.Warning);
        Assert.Contains("a.txt", warning.Message);
        Assert.Contains("develop", warning.Message);
    }

    [Fact]
    public async Task Finishing_is_only_offered_on_a_git_flow_branch()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync("topic");

        Assert.Null(tab.GitFlow.CurrentBranch);
        await tab.GitFlow.FinishCurrentCommand.ExecuteAsync(null);
        Assert.Empty(_git.GitFlowFinishes);
        Assert.DoesNotContain(tab.GetPaletteEntries(), e => e.Label.StartsWith("Git Flow : terminer", StringComparison.Ordinal));
        Assert.Contains(tab.GetPaletteEntries(), e => e.Label == "Git Flow : démarrer une release…");
    }

    [Fact]
    public async Task The_menus_offer_what_applies()
    {
        _git.GitFlow = Config;
        using var tab = await OpenAsync("feature/login");

        var section = tab.Refs.MenuFor(tab.Refs.Rows.Single(r => r.Row is { Kind: RefRowKind.Section, Section: RefSection.GitFlow }).Row)!;
        Assert.True(section.CanStartGitFlow);
        Assert.Null(section.FinishGitFlowLabel);

        var branch = tab.Refs.MenuFor(tab.Refs.Rows.Single(r => r.Row.Kind == RefRowKind.FlowBranch).Row)!;
        Assert.Equal("Terminer la feature login…", branch.FinishGitFlowLabel);
        Assert.Equal("feature/login", branch.CopyText);
    }

    private sealed class FakeInputs : IInputDialogService
    {
        public Action<InputDialogViewModel> Answer { get; set; } = _ => { };

        public bool Confirm { get; set; } = true;

        public Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            Answer(dialog);
            return Task.FromResult(Confirm && dialog.CanConfirm);
        }
    }

    private sealed class FakeConfirmations : IConfirmationService
    {
        public List<ConfirmationRequest> Requests { get; } = [];

        public bool Confirm { get; set; } = true;

        public bool OptionChecked { get; set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Confirm);

        public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConfirmationOutcome(Confirm, Confirm && OptionChecked));
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
