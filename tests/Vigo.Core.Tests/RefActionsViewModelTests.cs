using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class RefActionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly FakeInputs _inputs = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly TestRepoBuilder _remote;
    private readonly TestRepoBuilder _repo;

    public RefActionsViewModelTests()
    {
        _remote = TestRepoBuilder.Create(bare: true);
        _repo = TestRepoBuilder.Create();
        _repo.Commit("one", ("a.txt", "1\n"));
        _repo.Git("remote", "add", "origin", _remote.Path);
        _repo.Git("push", "--quiet", "--set-upstream", "origin", "main");
    }

    public void Dispose()
    {
        _repo.Dispose();
        _remote.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<RepositoryTabViewModel> OpenAsync(Preferences? preferences = null)
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, _notifier, _confirmations, authentication: null, _inputs, preferences);
        await tab.LoadAsync();
        await tab.RefActions.LoadRemotesAsync();
        return tab;
    }

    private string[] LocalBranches() =>
        _repo.Git("for-each-ref", "--format=%(refname:short)", "refs/heads").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static GitRef Ref(RepositoryTabViewModel tab, string name) => tab.Graph.Refs!.Refs.Single(r => r.Name == name);

    [Fact]
    public async Task New_branch_on_the_selected_commit_is_created_and_checked_out_by_default()
    {
        var first = GitSha.Parse(_repo.Head());
        _repo.Commit("two", ("a.txt", "2\n"));
        using var tab = await OpenAsync();
        Assert.True(tab.Graph.TrySelect(first));
        _inputs.Answer = d => d.Name = "feature/x";

        await tab.RefActions.CreateBranchCommand.ExecuteAsync(null);

        var dialog = Assert.Single(_inputs.Shown);
        Assert.Equal($"Depuis {first.ToShortString()} · one", dialog.Description);
        Assert.True(dialog.HasOption);
        Assert.Equal("feature/x", _repo.Git("branch", "--show-current").Trim());
        Assert.Equal(first.ToString(), _repo.Head());
        Assert.Contains(tab.Graph.Refs!.Refs, r => r.Name == "feature/x" && r.IsCurrent);
    }

    [Fact]
    public async Task Unchecking_the_option_only_creates_the_branch_and_is_remembered()
    {
        var store = new InMemoryPreferencesStore();
        using var preferences = new Preferences(store, new UserPreferences());
        using var tab = await OpenAsync(preferences);

        _inputs.Answer = d => { d.Name = "topic"; d.OptionChecked = false; };
        await tab.RefActions.CreateBranchCommand.ExecuteAsync(null);
        Assert.Equal("main", _repo.Git("branch", "--show-current").Trim());
        Assert.Contains("topic", LocalBranches());
        Assert.False(preferences.Current.CheckoutNewBranch);

        _inputs.Answer = d => d.Name = "other";
        await tab.RefActions.CreateBranchCommand.ExecuteAsync(null);
        Assert.False(_inputs.Shown[^1].OptionChecked);
    }

    [Fact]
    public async Task Branch_name_dialog_validates_while_typing()
    {
        _repo.Branch("feature/x", checkout: false);
        using var tab = await OpenAsync();
        InputDialogViewModel? dialog = null;
        _inputs.Answer = d => { dialog = d; d.Name = string.Empty; };
        _inputs.Confirm = false;

        await tab.RefActions.CreateBranchCommand.ExecuteAsync(null);

        Assert.NotNull(dialog);
        Assert.False(dialog.CanConfirm);
        Assert.False(dialog.HasNameError);
        dialog.Name = "a..b";
        Assert.Equal("Le nom ne peut pas contenir « .. ».", dialog.NameError);
        dialog.Name = "feature/x";
        Assert.Equal("La branche feature/x existe déjà.", dialog.NameError);
        dialog.Name = "feature";
        Assert.False(dialog.CanConfirm);
        dialog.Name = " feature/y ";
        Assert.True(dialog.CanConfirm);
        Assert.Equal("feature/y", dialog.Value);
    }

    [Fact]
    public async Task Renames_a_branch()
    {
        _repo.Branch("old", checkout: false);
        using var tab = await OpenAsync();
        _inputs.Answer = d =>
        {
            Assert.False(d.CanConfirm);
            d.Name = "new";
        };

        await tab.RefActions.RenameBranchCommand.ExecuteAsync(Ref(tab, "old"));

        Assert.Equal(["main", "new"], LocalBranches());
        Assert.Contains(tab.Graph.Refs!.Refs, r => r.Name == "new");
    }

    [Fact]
    public async Task Deleting_an_unmerged_branch_warns_with_its_commit_count()
    {
        _repo.Branch("feature");
        _repo.Commit("f1", ("a.txt", "f1\n"));
        _repo.Commit("f2", ("a.txt", "f2\n"));
        _repo.Checkout("main");
        using var tab = await OpenAsync();
        _confirmations.Answer = true;

        await tab.RefActions.DeleteBranchCommand.ExecuteAsync(Ref(tab, "feature"));

        var request = Assert.Single(_confirmations.Requests);
        Assert.Equal("Supprimer la branche feature ?", request.Title);
        Assert.StartsWith("2 commits de feature ne sont sur aucune autre branche", request.Message, StringComparison.Ordinal);
        Assert.Null(request.OptionText);
        Assert.True(request.IsDestructive);
        Assert.Equal(["main"], LocalBranches());
        Assert.Contains(_notifier.Items, n => n.Title == "Branche feature supprimée");
    }

    [Fact]
    public async Task Canceled_deletion_keeps_the_branch()
    {
        _repo.Branch("feature", checkout: false);
        using var tab = await OpenAsync();
        _confirmations.Answer = false;

        await tab.RefActions.DeleteBranchCommand.ExecuteAsync(Ref(tab, "feature"));

        Assert.Equal(["feature", "main"], LocalBranches());
    }

    [Fact]
    public async Task Deleting_a_branch_can_delete_its_remote_branch_too()
    {
        _repo.Branch("feature");
        _repo.Commit("f1", ("a.txt", "f1\n"));
        _repo.Git("push", "--quiet", "--set-upstream", "origin", "feature");
        _repo.Checkout("main");
        using var tab = await OpenAsync();
        _confirmations.Answer = true;
        _confirmations.CheckOption = true;

        await tab.RefActions.DeleteBranchCommand.ExecuteAsync(Ref(tab, "feature"));

        var request = Assert.Single(_confirmations.Requests);
        Assert.Equal("Supprimer aussi origin/feature", request.OptionText);
        Assert.StartsWith("Ses commits restent sur origin/feature. Si vous supprimez aussi origin/feature, 1 commit ne sera plus", request.Message, StringComparison.Ordinal);
        Assert.Equal(["main"], LocalBranches());
        Assert.DoesNotContain("feature", _remote.Git("for-each-ref", "--format=%(refname)"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pushes_a_branch_that_has_no_upstream_to_origin()
    {
        _repo.Branch("feature", checkout: false);
        using var tab = await OpenAsync();
        var branch = Ref(tab, "feature");
        Assert.Equal("Publier feature sur origin", tab.RefActions.PushBranchLabel(branch));

        await tab.RefActions.PushBranchCommand.ExecuteAsync(branch);

        Assert.Contains("refs/heads/feature", _remote.Git("for-each-ref", "--format=%(refname)"), StringComparison.Ordinal);
        Assert.Equal("origin/feature", _repo.Git("rev-parse", "--abbrev-ref", "feature@{upstream}").Trim());
        Assert.Contains(_notifier.Items, n => n.Title == "Push terminé");
    }

    [Fact]
    public async Task Annotated_tag_is_created_and_pushed_when_asked()
    {
        using var tab = await OpenAsync();
        _inputs.Answer = d =>
        {
            Assert.Equal("Pousser vers origin", d.OptionText);
            d.Name = "v1.0";
            d.SecondText = "First release";
            d.OptionChecked = true;
        };

        await tab.RefActions.CreateTagCommand.ExecuteAsync(null);

        Assert.Equal("tag", _repo.Git("cat-file", "-t", "v1.0").Trim());
        Assert.Contains("refs/tags/v1.0", _remote.Git("for-each-ref", "--format=%(refname)"), StringComparison.Ordinal);
        Assert.Contains(tab.Graph.Refs!.Refs, r => r.Name == "v1.0" && r.Kind == GitRefKind.Tag);
    }

    [Fact]
    public async Task Deleting_a_tag_keeps_the_remote_one_unless_asked()
    {
        _repo.Tag("v1");
        _repo.Git("push", "--quiet", "origin", "v1");
        using var tab = await OpenAsync();
        _confirmations.Answer = true;

        await tab.RefActions.DeleteTagCommand.ExecuteAsync(Ref(tab, "v1"));

        Assert.Equal("Supprimer aussi sur origin", Assert.Single(_confirmations.Requests).OptionText);
        Assert.Equal(string.Empty, _repo.Git("tag", "-l").Trim());
        Assert.Contains("refs/tags/v1", _remote.Git("for-each-ref", "--format=%(refname)"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adds_edits_and_removes_a_remote()
    {
        using var other = TestRepoBuilder.Create(bare: true);
        using var tab = await OpenAsync();
        _inputs.Answer = d =>
        {
            Assert.Equal(string.Empty, d.Name);
            d.Name = "origin";
            Assert.Equal("Le distant origin existe déjà.", d.NameError);
            d.Name = "upstream";
            d.SecondText = other.Path;
            d.OptionChecked = false;
        };
        await tab.RefActions.AddRemoteCommand.ExecuteAsync(null);
        Assert.Equal(["origin", "upstream"], tab.RefActions.Remotes.Select(r => r.Name));
        Assert.Contains(tab.Refs.Rows, r => r.Row.Kind == RefRowKind.Remote && r.Row.Label == "upstream");

        _inputs.Answer = d =>
        {
            Assert.Equal(other.Path, d.SecondText);
            d.Name = "mirror";
            d.SecondText = "https://example.com/mirror.git";
        };
        await tab.RefActions.EditRemoteCommand.ExecuteAsync("upstream");
        Assert.Equal(new GitRemote("mirror", "https://example.com/mirror.git"), tab.RefActions.Remotes.Single(r => r.Name != "origin"));

        _confirmations.Answer = true;
        await tab.RefActions.RemoveRemoteCommand.ExecuteAsync("mirror");
        Assert.Equal(["origin"], tab.RefActions.Remotes.Select(r => r.Name));
    }

    [Fact]
    public async Task Panel_menus_offer_what_applies_to_each_row()
    {
        _repo.Branch("feature", checkout: false);
        _repo.Tag("v1");
        _repo.Git("fetch", "--quiet", "origin");
        using var tab = await OpenAsync();
        RefRow Row(RefRowKind kind, string label) => tab.Refs.Rows.Select(r => r.Row).First(r => r.Kind == kind && r.Label == label);
        tab.Refs.FilterText = "";
        tab.Refs.ActivateCommand.Execute(tab.Refs.Rows.Select(r => r.Row).Single(r => r.Key == "section:Tags"));

        var main = tab.Refs.MenuFor(Row(RefRowKind.LocalBranch, "main"))!;
        Assert.Null(main.CheckoutLabel);
        Assert.Equal("Supprimer main…", main.DeleteLabel);
        Assert.False(main.CanDelete);
        Assert.Equal("Pousser main vers origin/main", main.PushLabel);

        var feature = tab.Refs.MenuFor(Row(RefRowKind.LocalBranch, "feature"))!;
        Assert.Equal("Checkout feature", feature.CheckoutLabel);
        Assert.True(feature.CanDelete && feature.CanRename && feature.CanCreateBranch);
        Assert.Equal("Publier feature sur origin", feature.PushLabel);

        var remoteBranch = tab.Refs.MenuFor(Row(RefRowKind.RemoteBranch, "main"))!;
        Assert.Equal("Supprimer origin/main du distant…", remoteBranch.DeleteLabel);
        Assert.False(remoteBranch.CanRename);

        var tag = tab.Refs.MenuFor(Row(RefRowKind.Tag, "v1"))!;
        Assert.Equal("Pousser v1 vers origin", tag.PushLabel);
        Assert.Equal("Supprimer le tag v1…", tag.DeleteLabel);

        var remote = tab.Refs.MenuFor(Row(RefRowKind.Remote, "origin"))!;
        Assert.True(remote.CanEditRemote && remote.CanRemoveRemote && remote.CanAddRemote);
        Assert.Null(remote.DeleteLabel);

        Assert.True(tab.Refs.MenuFor(Row(RefRowKind.Section, "Distants"))!.CanAddRemote);
        Assert.Null(tab.Refs.MenuFor(Row(RefRowKind.Section, "Stashes")));
    }

    [Theory]
    [InlineData("feature/x", null)]
    [InlineData("v1.2.3", null)]
    [InlineData("", "Le nom est obligatoire.")]
    [InlineData("-x", "Le nom ne peut pas commencer par « - ».")]
    [InlineData("a b", "Le nom ne peut pas contenir d'espace.")]
    [InlineData("a~1", "Le nom ne peut pas contenir « ~ ».")]
    [InlineData("a/.hidden", "Aucune partie du nom ne peut commencer par « . ».")]
    [InlineData("x.lock", "Aucune partie du nom ne peut finir par « .lock ».")]
    [InlineData("a//b", "Le nom ne peut pas contenir « // ».")]
    [InlineData("a/", "Le nom ne peut ni commencer ni finir par « / ».")]
    [InlineData("a@{1}", "Le nom ne peut pas contenir « @{ ».")]
    [InlineData("HEAD", "« HEAD » est réservé.")]
    public void Ref_names_follow_check_ref_format(string name, string? expected) => Assert.Equal(expected, RefNames.Check(name));

    private sealed class FakeInputs : IInputDialogService
    {
        public Action<InputDialogViewModel> Answer { get; set; } = _ => { };

        public bool Confirm { get; set; } = true;

        public List<InputDialogViewModel> Shown { get; } = [];

        public Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            Shown.Add(dialog);
            Answer(dialog);
            return Task.FromResult(Confirm && dialog.CanConfirm);
        }
    }

    private sealed class FakeConfirmations : IConfirmationService
    {
        public bool Answer { get; set; }

        public bool CheckOption { get; set; }

        public List<ConfirmationRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer);

        public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConfirmationOutcome(Answer, CheckOption));
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
