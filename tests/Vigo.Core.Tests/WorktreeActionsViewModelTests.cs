using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class WorktreeActionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly FakeInputs _inputs = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly List<string> _opened = [];
    private readonly TestRepoBuilder _repo;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "vigo-tests", "wtvm-" + Guid.NewGuid().ToString("N")[..8]);

    public WorktreeActionsViewModelTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Commit("one", ("a.txt", "1\n"));
        _repo.Branch("topic", checkout: false);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { /* temp folder */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private string Folder(string name) => Path.Combine(_scratch, name);

    [Fact]
    public async Task The_main_worktree_is_listed_and_is_the_one_of_the_tab()
    {
        using var tab = await OpenAsync();

        var main = Assert.Single(tab.Worktrees.Worktrees);
        Assert.True(main.IsMain);
        Assert.True(tab.Worktrees.IsCurrent(main));
    }

    [Fact]
    public async Task New_worktree_creates_the_branch_on_the_selected_commit_in_the_chosen_folder()
    {
        using var tab = await OpenAsync();
        var folder = Folder("feature-wt");
        _inputs.Answer = d =>
        {
            d.Name = "feature/x";
            d.SecondText = folder;
        };

        await tab.Worktrees.AddCommand.ExecuteAsync(null);

        var dialog = Assert.Single(_inputs.Shown);
        Assert.StartsWith("Depuis ", dialog.Description, StringComparison.Ordinal);
        Assert.Equal(2, tab.Worktrees.Worktrees.Count);
        Assert.Equal("feature/x", tab.Worktrees.Worktrees[1].Branch);
        Assert.True(File.Exists(Path.Combine(folder, "a.txt")));
        Assert.Contains(_notifier.Items, n => n.Title == "Arbre de travail créé");
    }

    [Fact]
    public async Task The_new_worktree_dialog_refuses_relative_paths_and_existing_branches()
    {
        using var tab = await OpenAsync();
        InputDialogViewModel? dialog = null;
        _inputs.Answer = d => dialog = d;
        _inputs.Confirm = false;

        await tab.Worktrees.AddCommand.ExecuteAsync(null);

        Assert.NotNull(dialog);
        Assert.NotEqual(string.Empty, dialog.SecondText); // default: next to the repository
        Assert.True(Path.IsPathRooted(dialog.SecondText));
        dialog.Name = "topic";
        Assert.Equal("La branche topic existe déjà.", dialog.NameError);
        dialog.Name = "ok";
        dialog.SecondText = "relative/dir";
        Assert.Equal("Saisissez un chemin absolu.", dialog.SecondError);
        Assert.False(dialog.CanConfirm);
        dialog.SecondText = Folder("ok-wt");
        Assert.True(dialog.CanConfirm);
    }

    [Fact]
    public async Task A_branch_gets_its_own_worktree_and_is_then_checked_out_elsewhere()
    {
        using var tab = await OpenAsync();
        var topic = tab.Graph.Refs!.Refs.Single(r => r.Name == "topic");
        var folder = Folder("topic-wt");
        _inputs.Answer = d => d.Name = folder;

        await tab.Worktrees.AddForBranchCommand.ExecuteAsync(topic);

        Assert.Equal("topic", tab.Worktrees.Worktrees[1].Branch);
        Assert.NotNull(tab.Worktrees.WorktreeOf("topic"));
        // The menu no longer offers a second worktree for it.
        var row = tab.Refs.Rows.Select(r => r.Row).Single(r => r.Ref?.Name == "topic");
        Assert.False(tab.Refs.MenuFor(row)!.CanAddWorktree);
    }

    [Fact]
    public async Task The_panel_lists_worktrees_and_opens_one_in_a_tab()
    {
        using var tab = await OpenAsync();
        _inputs.Answer = d => d.Name = Folder("topic-wt");
        await tab.Worktrees.AddForBranchCommand.ExecuteAsync(tab.Graph.Refs!.Refs.Single(r => r.Name == "topic"));

        var rows = RefRowBuilder.BuildRows(tab.Graph.Refs, [], string.Empty, new HashSet<string>(), _ => null, null,
            tab.Worktrees.Worktrees, tab.Worktrees.IsCurrent);
        Assert.Contains(rows, r => r is { Kind: RefRowKind.Section, Section: RefSection.Worktrees, Meta: "2" });
        var row = Assert.Single(rows, r => r.Kind == RefRowKind.Worktree && r.Label == "topic-wt");
        Assert.Equal("topic", row.Meta);
        Assert.Contains(rows, r => r.Kind == RefRowKind.Worktree && r.IsHead);

        var menu = tab.Refs.MenuFor(row)!;
        Assert.True(menu.CanOpenWorktree);
        Assert.True(menu.CanRemoveWorktree);

        await tab.Worktrees.OpenCommand.ExecuteAsync(row.Worktree);
        Assert.Equal([row.Worktree!.Path], _opened);

        // The worktree of the tab itself is not opened again, nor removed.
        var main = tab.Worktrees.Worktrees[0];
        await tab.Worktrees.OpenCommand.ExecuteAsync(main);
        Assert.Single(_opened);
        Assert.False(tab.Refs.MenuFor(rows.Single(r => r.Worktree == main))!.CanRemoveWorktree);
    }

    [Fact]
    public async Task Removing_a_worktree_asks_first_and_keeps_the_branch()
    {
        using var tab = await OpenAsync();
        var folder = Folder("topic-wt");
        _inputs.Answer = d => d.Name = folder;
        await tab.Worktrees.AddForBranchCommand.ExecuteAsync(tab.Graph.Refs!.Refs.Single(r => r.Name == "topic"));
        var worktree = tab.Worktrees.Worktrees[1];

        _confirmations.Answer = false;
        await tab.Worktrees.RemoveCommand.ExecuteAsync(worktree);
        Assert.True(Directory.Exists(folder));

        _confirmations.Answer = true;
        await tab.Worktrees.RemoveCommand.ExecuteAsync(worktree);
        Assert.False(Directory.Exists(folder));
        Assert.Single(tab.Worktrees.Worktrees);
        Assert.Contains("topic", _repo.Git("branch", "--list", "topic"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worktree_with_changes_is_only_removed_after_a_second_confirmation()
    {
        using var tab = await OpenAsync();
        var folder = Folder("dirty-wt");
        _inputs.Answer = d => d.Name = folder;
        await tab.Worktrees.AddForBranchCommand.ExecuteAsync(tab.Graph.Refs!.Refs.Single(r => r.Name == "topic"));
        File.WriteAllText(Path.Combine(folder, "scratch.txt"), "x");
        var worktree = tab.Worktrees.Worktrees[1];
        _confirmations.Answer = true;

        await tab.Worktrees.RemoveCommand.ExecuteAsync(worktree);

        Assert.Equal(2, _confirmations.Requests.Count);
        Assert.Contains("Forcer", _confirmations.Requests[1].Title, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
        Assert.Single(tab.Worktrees.Worktrees);
    }

    [Fact]
    public async Task Refusing_the_forced_removal_leaves_the_worktree_alone()
    {
        using var tab = await OpenAsync();
        var folder = Folder("dirty-wt");
        _inputs.Answer = d => d.Name = folder;
        await tab.Worktrees.AddForBranchCommand.ExecuteAsync(tab.Graph.Refs!.Refs.Single(r => r.Name == "topic"));
        File.WriteAllText(Path.Combine(folder, "scratch.txt"), "x");
        var worktree = tab.Worktrees.Worktrees[1];
        var answers = new Queue<bool>([true, false]);
        _confirmations.Next = () => answers.Dequeue();

        await tab.Worktrees.RemoveCommand.ExecuteAsync(worktree);

        Assert.True(Directory.Exists(folder));
        Assert.Equal(2, tab.Worktrees.Worktrees.Count);
        Assert.DoesNotContain(_notifier.Items, n => n.Title == "Arbre de travail supprimé");
    }

    [Fact]
    public async Task Pruning_forgets_the_worktrees_whose_folder_is_gone()
    {
        using var tab = await OpenAsync();
        var folder = Folder("gone-wt");
        _inputs.Answer = d => d.Name = folder;
        await tab.Worktrees.AddForBranchCommand.ExecuteAsync(tab.Graph.Refs!.Refs.Single(r => r.Name == "topic"));
        Directory.Delete(folder, recursive: true);
        await tab.Worktrees.LoadAsync();
        Assert.True(tab.Worktrees.HasPrunable);

        await tab.Worktrees.PruneCommand.ExecuteAsync(null);

        Assert.False(tab.Worktrees.HasPrunable);
        Assert.Single(tab.Worktrees.Worktrees);
    }

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
