using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class SubmoduleActionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly FakeInputs _inputs = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly List<string> _opened = [];
    private readonly TestRepoBuilder _repo;
    private readonly TestRepoBuilder _dependency;

    public SubmoduleActionsViewModelTests()
    {
        _dependency = TestRepoBuilder.Create();
        _dependency.Commit("dep", ("lib.txt", "lib\n"));
        _repo = TestRepoBuilder.Create();
        _repo.Commit("one", ("a.txt", "1\n"));
        TestRepoBuilder.AllowFileSubmodules();
        _repo.Git("submodule", "add", "--", _dependency.Path, "libs/dep");
        // Committed: git refuses to deinitialize a submodule that is only staged.
        _repo.Commit("add dep");
    }

    public void Dispose()
    {
        _repo.Dispose();
        _dependency.Dispose();
    }

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, _notifier, _confirmations, authentication: null, _inputs,
            openRepository: path =>
            {
                _opened.Add(path);
                return Task.CompletedTask;
            });
        await tab.LoadAsync();
        return tab;
    }

    private string DependencyFolder => Path.Combine(_repo.Path, "libs", "dep");

    /// <summary>"other" records a newer commit of the submodule than main; the repository is left on "other" with the submodule at it.</summary>
    private (string OnMain, string OnOther) PrepareTwoPointers()
    {
        var onMain = _dependency.Head();
        _dependency.Commit("dep 2", ("lib.txt", "lib 2\n"));
        _repo.Branch("other");
        _repo.Git("submodule", "update", "--remote", "--quiet");
        _repo.Stage("libs/dep");
        _repo.Commit("bump dep");
        return (onMain, _dependency.Head());
    }

    private string SubmoduleHead() => _repo.Git("-C", DependencyFolder, "rev-parse", "HEAD").Trim();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_checkout_updates_the_submodules_only_with_the_preference(bool keepUpToDate)
    {
        var (onMain, onOther) = PrepareTwoPointers();
        var preferences = new Preferences(new InMemoryPreferencesStore(), new UserPreferences { KeepSubmodulesUpToDate = keepUpToDate });
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "other", null, false, false, false);
        using var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, _notifier, _confirmations, authentication: null, _inputs, preferences: preferences);
        await tab.LoadAsync();
        Assert.Equal(onOther, SubmoduleHead());

        await tab.Stash.CheckoutAsync(new CheckoutBranch("main"), "main");

        Assert.Equal(keepUpToDate ? onMain : onOther, SubmoduleHead());
    }

    [Fact]
    public async Task The_submodule_is_listed_when_the_tab_opens_and_shown_in_the_panel()
    {
        using var tab = await OpenAsync();

        var submodule = Assert.Single(tab.Submodules.Submodules);
        Assert.True(tab.Submodules.HasSubmodules);
        Assert.Equal(SubmoduleState.UpToDate, submodule.State);

        var rows = RefRowBuilder.BuildRows(tab.Graph.Refs, [], string.Empty, new HashSet<string>(), _ => null, null,
            submodules: tab.Submodules.Submodules);
        Assert.Contains(rows, r => r is { Kind: RefRowKind.Section, Section: RefSection.Submodules, Meta: "1" });
        var row = Assert.Single(rows, r => r.Kind == RefRowKind.Submodule);
        Assert.Equal("dep", row.Label);
        Assert.Equal(submodule.Head!.Value.ToShortString(), row.Meta);
        Assert.False(row.IsMetaAccent);
        Assert.Contains("libs/dep", row.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_section_is_collapsed_by_default_and_hidden_without_submodules()
    {
        using var tab = await OpenAsync();

        var collapsed = tab.Refs.Rows.Single(r => r.Row is { Kind: RefRowKind.Section, Section: RefSection.Submodules }).Row;
        Assert.False(collapsed.IsExpanded);
        Assert.DoesNotContain(tab.Refs.Rows, r => r.Row.Kind == RefRowKind.Submodule);

        var none = RefRowBuilder.BuildRows(tab.Graph.Refs, [], string.Empty, new HashSet<string>(), _ => null, null, submodules: []);
        Assert.DoesNotContain(none, r => r.Section == RefSection.Submodules);
    }

    [Fact]
    public async Task A_filter_matches_the_path_of_a_submodule()
    {
        using var tab = await OpenAsync();

        var rows = RefRowBuilder.BuildRows(tab.Graph.Refs, [], "libs", new HashSet<string>(), _ => null, null,
            submodules: tab.Submodules.Submodules);
        Assert.Contains(rows, r => r.Kind == RefRowKind.Submodule);

        var other = RefRowBuilder.BuildRows(tab.Graph.Refs, [], "zzz", new HashSet<string>(), _ => null, null,
            submodules: tab.Submodules.Submodules);
        Assert.DoesNotContain(other, r => r.Kind == RefRowKind.Submodule);
    }

    [Fact]
    public async Task Deinitializing_asks_first_then_the_row_shows_it_is_not_cloned()
    {
        using var tab = await OpenAsync();
        var submodule = tab.Submodules.Submodules[0];

        _confirmations.Answer = false;
        await tab.Submodules.DeinitCommand.ExecuteAsync(submodule);
        Assert.True(File.Exists(Path.Combine(DependencyFolder, "lib.txt")));

        _confirmations.Answer = true;
        await tab.Submodules.DeinitCommand.ExecuteAsync(submodule);

        var after = Assert.Single(tab.Submodules.Submodules);
        Assert.Equal(SubmoduleState.NotInitialized, after.State);
        Assert.False(File.Exists(Path.Combine(DependencyFolder, "lib.txt")));
        Assert.Contains(_notifier.Items, n => n.Title == "Submodule désinitialisé");

        var rows = RefRowBuilder.BuildRows(tab.Graph.Refs, [], string.Empty, new HashSet<string>(), _ => null, null,
            submodules: tab.Submodules.Submodules);
        var row = Assert.Single(rows, r => r.Kind == RefRowKind.Submodule);
        Assert.Equal("non initialisé", row.Meta);
        Assert.True(row.IsMetaAccent);

        var menu = tab.Refs.MenuFor(row)!;
        Assert.Equal("Initialiser et mettre à jour", menu.UpdateSubmoduleLabel);
        Assert.False(menu.CanOpenSubmodule);
        Assert.False(menu.CanDeinitSubmodule);
    }

    [Fact]
    public async Task A_submodule_with_changes_is_only_deinitialized_after_a_second_confirmation()
    {
        using var tab = await OpenAsync();
        File.WriteAllText(Path.Combine(DependencyFolder, "lib.txt"), "edited\n");
        _confirmations.Answer = true;

        await tab.Submodules.DeinitCommand.ExecuteAsync(tab.Submodules.Submodules[0]);

        Assert.Equal(2, _confirmations.Requests.Count);
        Assert.Contains("Forcer", _confirmations.Requests[1].Title, StringComparison.Ordinal);
        Assert.Equal(SubmoduleState.NotInitialized, Assert.Single(tab.Submodules.Submodules).State);
    }

    [Fact]
    public async Task Refusing_the_forced_deinitialization_leaves_the_submodule_alone()
    {
        using var tab = await OpenAsync();
        File.WriteAllText(Path.Combine(DependencyFolder, "lib.txt"), "edited\n");
        var answers = new Queue<bool>([true, false]);
        _confirmations.Next = () => answers.Dequeue();

        await tab.Submodules.DeinitCommand.ExecuteAsync(tab.Submodules.Submodules[0]);

        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(DependencyFolder, "lib.txt")));
        Assert.DoesNotContain(_notifier.Items, n => n.Title == "Submodule désinitialisé");
    }

    [Fact]
    public async Task Updating_a_deinitialized_submodule_clones_it_without_asking()
    {
        using var tab = await OpenAsync();
        _confirmations.Answer = true;
        await tab.Submodules.DeinitCommand.ExecuteAsync(tab.Submodules.Submodules[0]);
        _confirmations.Requests.Clear();

        await tab.Submodules.UpdateCommand.ExecuteAsync(tab.Submodules.Submodules[0]);

        Assert.Empty(_confirmations.Requests);
        Assert.Equal(SubmoduleState.UpToDate, Assert.Single(tab.Submodules.Submodules).State);
        Assert.True(File.Exists(Path.Combine(DependencyFolder, "lib.txt")));
    }

    [Fact]
    public async Task Putting_a_moved_submodule_back_on_its_recorded_commit_asks_first()
    {
        using var tab = await OpenAsync();
        var inner = TestRepoBuilder.Attach(DependencyFolder);
        var moved = inner.Commit("local work", ("lib.txt", "changed\n"));
        await tab.Submodules.LoadAsync();
        var submodule = Assert.Single(tab.Submodules.Submodules);
        Assert.Equal(SubmoduleState.OutOfSync, submodule.State);
        Assert.Equal($"≠ {submodule.Head!.Value.ToShortString()}", RowOf(tab).Meta);

        _confirmations.Answer = false;
        await tab.Submodules.UpdateCommand.ExecuteAsync(submodule);
        Assert.Equal(moved, inner.Head());

        _confirmations.Answer = true;
        await tab.Submodules.UpdateCommand.ExecuteAsync(submodule);
        Assert.Equal(_dependency.Head(), inner.Head());
        Assert.Equal(SubmoduleState.UpToDate, Assert.Single(tab.Submodules.Submodules).State);
    }

    [Fact]
    public async Task Opening_an_initialized_submodule_opens_its_folder_in_a_tab()
    {
        using var tab = await OpenAsync();

        await tab.Submodules.OpenCommand.ExecuteAsync(tab.Submodules.Submodules[0]);

        Assert.Equal([Path.GetFullPath(DependencyFolder)], _opened);
    }

    [Fact]
    public async Task Opening_a_submodule_that_is_not_cloned_warns_instead()
    {
        using var tab = await OpenAsync();
        _confirmations.Answer = true;
        await tab.Submodules.DeinitCommand.ExecuteAsync(tab.Submodules.Submodules[0]);

        await tab.Submodules.OpenCommand.ExecuteAsync(tab.Submodules.Submodules[0]);

        Assert.Empty(_opened);
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Warning, Title: "Submodule non initialisé" });
    }

    [Fact]
    public async Task Double_click_on_a_row_opens_it_or_initializes_it()
    {
        using var tab = await OpenAsync();
        var row = RowOf(tab);

        await tab.Refs.OpenSubmoduleCommand.ExecuteAsync(row);
        Assert.Single(_opened);

        _confirmations.Answer = true;
        await tab.Submodules.DeinitCommand.ExecuteAsync(tab.Submodules.Submodules[0]);
        await tab.Refs.OpenSubmoduleCommand.ExecuteAsync(RowOf(tab));

        Assert.Single(_opened);
        Assert.Equal(SubmoduleState.UpToDate, Assert.Single(tab.Submodules.Submodules).State);
    }

    [Fact]
    public async Task Adding_a_submodule_stages_it_and_lists_it()
    {
        using var tab = await OpenAsync();
        _inputs.Answer = d =>
        {
            d.Name = _dependency.Path;
            d.SecondText = "vendor/second";
        };

        await tab.Submodules.AddCommand.ExecuteAsync(null);

        Assert.Equal(2, tab.Submodules.Submodules.Count);
        Assert.Contains(tab.Submodules.Submodules, s => s.Path == "vendor/second");
        Assert.True(File.Exists(Path.Combine(_repo.Path, "vendor", "second", "lib.txt")));
        Assert.Contains(_notifier.Items, n => n.Title == "Submodule ajouté");
    }

    [Fact]
    public async Task The_add_dialog_refuses_a_missing_url_and_paths_leaving_the_repository()
    {
        using var tab = await OpenAsync();
        InputDialogViewModel? dialog = null;
        _inputs.Answer = d => dialog = d;
        _inputs.Confirm = false;

        await tab.Submodules.AddCommand.ExecuteAsync(null);

        Assert.NotNull(dialog);
        Assert.False(dialog.CanConfirm);
        dialog.Name = "-u";
        Assert.False(dialog.CanConfirm);
        dialog.Name = "https://example.test/x.git";
        Assert.True(dialog.CanConfirm); // the folder is optional
        dialog.SecondText = "../outside";
        Assert.Equal("Le dossier doit rester dans le dépôt.", dialog.SecondError);
        dialog.SecondText = @"C:\elsewhere";
        Assert.Equal("Saisissez un chemin relatif au dépôt.", dialog.SecondError);
        dialog.SecondText = "libs/x";
        Assert.True(dialog.CanConfirm);
    }

    [Fact]
    public async Task The_menus_follow_the_kind_of_row()
    {
        using var tab = await OpenAsync();
        var section = tab.Refs.Rows.Single(r => r.Row is { Kind: RefRowKind.Section, Section: RefSection.Submodules }).Row;

        var sectionMenu = tab.Refs.MenuFor(section)!;
        Assert.True(sectionMenu.CanAddSubmodule);
        Assert.True(sectionMenu.CanUpdateAllSubmodules);

        var menu = tab.Refs.MenuFor(RowOf(tab))!;
        Assert.True(menu.CanOpenSubmodule);
        Assert.Equal("Mettre à jour", menu.UpdateSubmoduleLabel);
        Assert.True(menu.CanDeinitSubmodule);
        Assert.Equal("libs/dep", menu.CopyText);
    }

    [Fact]
    public async Task The_palette_offers_to_add_and_update_submodules()
    {
        using var tab = await OpenAsync();

        var labels = tab.GetPaletteEntries().Select(e => e.Label).ToList();

        Assert.Contains("Ajouter un submodule…", labels);
        Assert.Contains("Mettre à jour les submodules", labels);
    }

    /// <summary>The row of the only submodule, built as the panel does (the section is collapsed in the live rows).</summary>
    private static RefRow RowOf(RepositoryTabViewModel tab) =>
        Assert.Single(RefRowBuilder.BuildRows(tab.Graph.Refs, [], string.Empty, new HashSet<string>(), _ => null, null,
            submodules: tab.Submodules.Submodules), r => r.Kind == RefRowKind.Submodule);

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
        public bool Answer { get; set; }

        /// <summary>Answers in sequence when set; <see cref="Answer"/> otherwise.</summary>
        public Func<bool>? Next { get; set; }

        public List<ConfirmationRequest> Requests { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer);

        public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ConfirmationOutcome(Next?.Invoke() ?? Answer, false));
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
