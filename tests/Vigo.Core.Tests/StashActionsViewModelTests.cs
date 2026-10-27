using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class StashActionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly FakeInputs _inputs = new();
    private readonly TestRepoBuilder _repo;

    /// <summary>main: init (a.txt, b.txt); feature: a.txt changed.</summary>
    public StashActionsViewModelTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Commit("init", ("a.txt", "a\n"), ("b.txt", "b\n"));
        _repo.Branch("feature");
        _repo.Commit("feature", ("a.txt", "feature\n"));
        _repo.Checkout("main");
    }

    public void Dispose() => _repo.Dispose();

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier,
            _confirmations, inputs: _inputs);
        await tab.LoadAsync();
        return tab;
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(_repo.Path, path));

    private string[] StashList() => _repo.Git("stash", "list", "--format=%gs").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static RefRow Row(RepositoryTabViewModel tab, string labelStart) =>
        tab.Refs.Rows.Select(i => i.Row).Single(r => r.Label.StartsWith(labelStart, StringComparison.Ordinal) || r.Ref?.Name == labelStart);

    [Fact]
    public async Task Stash_puts_every_change_aside_and_lists_it()
    {
        _repo.WriteFile("a.txt", "changed\n").WriteFile("new.txt", "new\n");
        using var tab = await OpenAsync();
        Assert.True(tab.Stash.CanStash);
        Assert.False(tab.Stash.CanPop);

        await tab.Stash.StashCommand.ExecuteAsync(null);

        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Remise créée", "stash@{0} : 2 fichiers mis de côté."));
        Assert.Equal(0, tab.WorkingTree.ChangeCount);
        Assert.False(tab.Stash.CanStash);
        Assert.True(tab.Stash.CanPop);
        Assert.StartsWith("Réappliquer stash@{0} : ", tab.Stash.PopTooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("stash@{0}", Row(tab, "stash@{0}").Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stash_with_message_asks_for_the_message_and_untracked_files()
    {
        _repo.WriteFile("a.txt", "changed\n").WriteFile("new.txt", "new\n");
        using var tab = await OpenAsync();
        _inputs.Answer = d => { d.Name = "try paging"; d.OptionChecked = false; };

        await tab.Stash.StashWithMessageCommand.ExecuteAsync(null);

        var dialog = Assert.Single(_inputs.Shown);
        Assert.Equal("2 fichiers modifiés sur main", dialog.Description);
        Assert.Equal(["On main: try paging"], StashList());
        Assert.Contains(_notifier.Items, n => n.Message == "stash@{0} : 1 fichier mis de côté.");
        Assert.Equal("try paging", Row(tab, "stash@{0}").Label);
        // The untracked file stayed.
        Assert.Equal(1, tab.WorkingTree.ChangeCount);
    }

    [Fact]
    public async Task Stash_description_is_kept_in_the_commit_and_refused_without_a_title()
    {
        _repo.WriteFile("a.txt", "changed\n");
        using var tab = await OpenAsync();
        bool? confirmable = null;
        _inputs.Answer = d =>
        {
            d.SecondText = "why and how";
            confirmable = d.CanConfirm;
            d.Name = "try paging";
        };

        await tab.Stash.StashWithMessageCommand.ExecuteAsync(null);

        Assert.False(confirmable);
        Assert.Equal("On main: try paging\n\nwhy and how", _repo.Git("log", "-1", "--format=%B", "refs/stash").Trim());
        Assert.Equal("try paging", Row(tab, "stash@{0}").Label);
    }

    [Fact]
    public async Task Stash_uses_the_commit_form_message_and_pop_puts_it_back()
    {
        _repo.WriteFile("a.txt", "changed\n");
        using var tab = await OpenAsync();
        var form = tab.WorkingTree.CommitForm;
        form.Summary = "try paging";
        form.Description = "why and how";

        await tab.Stash.StashCommand.ExecuteAsync(null);

        Assert.Equal("On main: try paging\n\nwhy and how", _repo.Git("log", "-1", "--format=%B", "refs/stash").Trim());
        Assert.Equal(string.Empty, form.Summary);
        Assert.Equal(string.Empty, form.Description);

        await tab.Stash.PopLatestCommand.ExecuteAsync(null);

        Assert.Equal("try paging", form.Summary);
        Assert.Equal("why and how", form.Description);
    }

    [Fact]
    public async Task Pop_from_the_toolbar_restores_and_drops_the_newest_stash()
    {
        _repo.WriteFile("a.txt", "older\n");
        _repo.Stash("older");
        _repo.WriteFile("b.txt", "newer\n");
        _repo.Stash("newer");
        using var tab = await OpenAsync();

        await tab.Stash.PopLatestCommand.ExecuteAsync(null);

        Assert.Equal("newer\n", Read("b.txt"));
        Assert.Equal(["On main: older"], StashList());
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "stash@{0} appliqué", "1 fichier restauré."));
        Assert.Equal("Réappliquer stash@{0} : older", tab.Stash.PopTooltip);
    }

    [Fact]
    public async Task Stash_rows_apply_pop_and_delete_after_confirmation()
    {
        _repo.WriteFile("a.txt", "older\n");
        _repo.Stash("older");
        _repo.WriteFile("b.txt", "newer\n");
        _repo.Stash("newer");
        using var tab = await OpenAsync();
        var older = Row(tab, "stash@{1}");
        var menu = tab.Refs.MenuFor(older);
        Assert.True(menu?.CanApplyStash);
        Assert.Equal("Supprimer la remise « older »…", menu?.DeleteLabel);

        await tab.Refs.ApplyStashCommand.ExecuteAsync(older);
        Assert.Equal("older\n", Read("a.txt"));
        Assert.Equal(2, StashList().Length);

        _repo.Git("checkout", "--", "a.txt");
        await tab.Refs.DeleteCommand.ExecuteAsync(Row(tab, "stash@{0}"));
        Assert.Equal(2, StashList().Length);
        Assert.Equal("Supprimer stash@{0} ?", Assert.Single(_confirmations.Requests).Title);

        _confirmations.Answer = true;
        await tab.Refs.DeleteCommand.ExecuteAsync(Row(tab, "stash@{0}"));
        Assert.Equal(["On main: older"], StashList());

        await tab.Refs.PopStashCommand.ExecuteAsync(Row(tab, "stash@{0}"));
        Assert.Empty(StashList());
        Assert.Equal("older\n", Read("a.txt"));
    }

    [Fact]
    public async Task A_conflicting_pop_keeps_the_stash_and_shows_the_conflicts()
    {
        _repo.WriteFile("a.txt", "stashed\n");
        _repo.Stash("conflict");
        _repo.Commit("other", ("a.txt", "committed\n"));
        using var tab = await OpenAsync();

        await tab.Stash.PopLatestCommand.ExecuteAsync(null);

        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning && n.Title == "Conflit détecté");
        Assert.Single(StashList());
        Assert.True(tab.WorkingTree.HasConflicts);
        Assert.False(tab.Stash.CanStash);
    }

    [Fact]
    public async Task Checkout_refused_for_local_changes_carries_them_automatically()
    {
        _repo.WriteFile("b.txt", "local\n");
        _repo.WriteFile("a.txt", "local\n");
        using var tab = await OpenAsync();
        var feature = Row(tab, "feature");

        // No confirmation: stash, checkout, pop; a.txt conflicts with feature's version, b.txt comes back.
        await tab.Refs.CheckoutCommand.ExecuteAsync(feature);

        Assert.Empty(_confirmations.Requests);

        Assert.Equal("feature", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal("local\n", Read("b.txt"));
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning && n.Title == "Conflit détecté");
        Assert.Single(StashList());
    }

    [Fact]
    public async Task Checkout_carrying_changes_reapplies_them_without_conflict()
    {
        // c.txt differs on "other" (git refuses to overwrite it); the local change is on a line "other" does not touch.
        _repo.Commit("c", ("c.txt", "1\n2\n3\n4\n5\n6\n7\n8\n"));
        _repo.Branch("other");
        _repo.Commit("other", ("c.txt", "one\n2\n3\n4\n5\n6\n7\n8\n"));
        _repo.Checkout("main");
        _repo.WriteFile("c.txt", "1\n2\n3\n4\n5\n6\n7\neight\n").WriteFile("new.txt", "new\n");
        using var tab = await OpenAsync();

        await tab.Refs.CheckoutCommand.ExecuteAsync(Row(tab, "other"));

        Assert.Equal("other", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal("one\n2\n3\n4\n5\n6\n7\neight\n", Read("c.txt"));
        Assert.Equal("new\n", Read("new.txt"));
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Bascule sur other effectuée", _));
        Assert.Empty(StashList());
        Assert.False(tab.WorkingTree.HasConflicts);
    }

    private sealed class FakeInputs : IInputDialogService
    {
        public Action<InputDialogViewModel> Answer { get; set; } = _ => { };

        public List<InputDialogViewModel> Shown { get; } = [];

        public Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            Shown.Add(dialog);
            Answer(dialog);
            return Task.FromResult(dialog.CanConfirm);
        }
    }

    private sealed class FakeConfirmations : IConfirmationService
    {
        public bool Answer { get; set; }

        public List<ConfirmationRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer);

        public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConfirmationOutcome(Answer, request.OptionChecked));
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
