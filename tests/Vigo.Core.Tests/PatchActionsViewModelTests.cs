using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>Patch files of a repository tab (J7.12): suggested names, create, apply, confirmation, operation banner.</summary>
public sealed class PatchActionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly FakeFileDialogs _dialogs = new();
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vigo-patchvm-" + Guid.NewGuid().ToString("N"));

    public PatchActionsViewModelTests()
    {
        Directory.CreateDirectory(_directory);
        _repo.Commit("Fix the cache!", ("a.txt", "1\n2\n3\n"));
    }

    public void Dispose()
    {
        _repo.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Left in the temporary folder.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<RepositoryTabViewModel> OpenAsync(TestRepoBuilder? repo = null, IFileDialogs? dialogs = null)
    {
        repo ??= _repo;
        var info = new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, _confirmations,
            fileDialogs: dialogs ?? _dialogs);
        await tab.LoadAsync();
        return tab;
    }

    [Theory]
    [InlineData("Fix the cache!", "fix-the-cache")]
    [InlineData("  Add   (new) feature -- v2 ", "add-new-feature-v2")]
    [InlineData("Ça marche", "a-marche")]
    [InlineData("!!!", "")]
    [InlineData(null, "")]
    public void The_slug_keeps_lowercase_letters_and_digits_between_single_dashes(string? text, string expected) =>
        Assert.Equal(expected, PatchActionsViewModel.Slug(text));

    [Fact]
    public void The_slug_is_cut_at_52_characters_without_a_trailing_dash()
    {
        var slug = PatchActionsViewModel.Slug(string.Join(' ', Enumerable.Repeat("word", 30)));

        Assert.True(slug.Length <= 52);
        Assert.DoesNotContain("--", slug, StringComparison.Ordinal);
        Assert.False(slug.EndsWith('-'));
    }

    [Fact]
    public void Suggested_names_follow_format_patch_and_the_branch()
    {
        var sha = GitSha.Parse("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678");

        Assert.Equal("0001-fix-the-cache.patch", PatchActionsViewModel.SuggestedCommitName("Fix the cache!", sha));
        Assert.Equal("0001-a1b2c3d.patch", PatchActionsViewModel.SuggestedCommitName("???", sha));
        Assert.Equal("feature-x-changes.patch", PatchActionsViewModel.SuggestedChangesName("feature/x", PatchScope.All));
        Assert.Equal("feature-x-staged.patch", PatchActionsViewModel.SuggestedChangesName("feature/x", PatchScope.Staged));
        Assert.Equal("working-tree-changes.patch", PatchActionsViewModel.SuggestedChangesName(null, PatchScope.Unstaged));
    }

    [Fact]
    public async Task Without_file_dialogs_nothing_is_offered()
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        using var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance);
        await tab.LoadAsync();

        Assert.False(tab.Patches.CanPrompt);
        Assert.False(tab.Patches.CanCreateFromCommit);
        Assert.False(tab.Patches.CanApply);
    }

    [Fact]
    public async Task A_commit_patch_is_saved_where_the_user_chose_with_a_name_from_the_subject()
    {
        using var tab = await OpenAsync();
        var path = Path.Combine(_directory, "chosen.patch");
        _dialogs.SavePath = path;

        await tab.Patches.CreateCommitPatchCommand.ExecuteAsync(tab.Graph.Head!.Value);

        Assert.Equal("0001-fix-the-cache.patch", _dialogs.SuggestedName);
        Assert.True(File.Exists(path));
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Patch créé", _));
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_creates_nothing_and_says_nothing()
    {
        using var tab = await OpenAsync();
        _dialogs.SavePath = null;

        await tab.Patches.CreateCommitPatchCommand.ExecuteAsync(tab.Graph.Head!.Value);

        Assert.Empty(_notifier.Items);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task The_working_tree_patch_is_offered_only_with_changes()
    {
        using var tab = await OpenAsync();
        Assert.False(tab.Patches.CanCreateFromWorkingTree);

        _repo.WriteFile("a.txt", "1\nTWO\n3\n");
        await tab.WorkingTree.RefreshAsync();
        Assert.True(tab.Patches.CanCreateFromWorkingTree);

        var path = Path.Combine(_directory, "changes.patch");
        _dialogs.SavePath = path;
        await tab.Patches.CreateWorkingTreePatchCommand.ExecuteAsync(PatchScope.All);

        Assert.Equal("main-changes.patch", _dialogs.SuggestedName);
        Assert.Contains("+TWO", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_to_create_the_patch_is_reported_as_an_error()
    {
        using var tab = await OpenAsync();
        _repo.WriteFile("a.txt", "1\nTWO\n3\n");
        await tab.WorkingTree.RefreshAsync();
        // Staged scope with nothing staged: git gives an empty diff.
        _dialogs.SavePath = Path.Combine(_directory, "none.patch");

        await tab.Patches.CreateWorkingTreePatchCommand.ExecuteAsync(PatchScope.Staged);

        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Error && n.Title == "Impossible de créer le patch");
        Assert.False(File.Exists(Path.Combine(_directory, "none.patch")));
    }

    [Fact]
    public async Task Applying_a_mailbox_creates_the_commits_and_can_be_undone()
    {
        using var source = TestRepoBuilder.Create();
        source.Commit("Fix the cache!", ("a.txt", "1\n2\n3\n"));
        using var target = TestRepoBuilder.Clone(source.Path, firstTick: 50_000);
        source.Commit("Add b", ("b.txt", "b\n"));
        var patch = Path.Combine(_directory, "b.patch");
        Assert.IsType<Results.Done>((await _git.CreatePatchFromCommitsAsync(source.Path, [GitSha.Parse(source.Head())], patch, Ct)).Value);
        _dialogs.OpenPath = patch;
        using var tab = await OpenAsync(target);
        Assert.True(tab.Patches.CanApply);

        await tab.Patches.ApplyPatchCommand.ExecuteAsync(null);

        Assert.Equal("Add b", target.Git("log", "-1", "--format=%s").Trim());
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Patch appliqué", _));
        Assert.Empty(_confirmations.Requested);
        Assert.True(tab.Undo.CanUndo);
    }

    [Fact]
    public async Task Applying_with_uncommitted_changes_asks_first_and_stops_when_refused()
    {
        using var source = TestRepoBuilder.Create();
        source.Commit("Fix the cache!", ("a.txt", "1\n2\n3\n"));
        using var target = TestRepoBuilder.Clone(source.Path, firstTick: 50_000);
        source.WriteFile("b.txt", "b\n").Stage("b.txt");
        var patch = Path.Combine(_directory, "b.patch");
        Assert.IsType<Results.Done>((await _git.CreatePatchFromWorkingTreeAsync(source.Path, PatchScope.Staged, null, patch, Ct)).Value);
        target.WriteFile("a.txt", "dirty\n");
        _dialogs.OpenPath = patch;
        using var tab = await OpenAsync(target);
        await tab.WorkingTree.RefreshAsync();
        _confirmations.Answer = false;

        await tab.Patches.ApplyPatchCommand.ExecuteAsync(null);

        Assert.Single(_confirmations.Requested);
        Assert.False(File.Exists(Path.Combine(target.Path, "b.txt")));

        _confirmations.Answer = true;
        await tab.Patches.ApplyPatchCommand.ExecuteAsync(null);

        Assert.Equal(2, _confirmations.Requested.Count);
        Assert.True(File.Exists(Path.Combine(target.Path, "b.txt")));
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Patch appliqué", _));
    }

    [Fact]
    public async Task A_mailbox_stopped_on_conflicts_shows_the_banner_and_aborts_from_it()
    {
        using var source = TestRepoBuilder.Create();
        source.Commit("Fix the cache!", ("a.txt", "1\n2\n3\n"));
        using var target = TestRepoBuilder.Clone(source.Path, firstTick: 50_000);
        source.Commit("Source edit", ("a.txt", "1\nSOURCE\n3\n"));
        var patch = Path.Combine(_directory, "edit.patch");
        Assert.IsType<Results.Done>((await _git.CreatePatchFromCommitsAsync(source.Path, [GitSha.Parse(source.Head())], patch, Ct)).Value);
        target.Commit("Target edit", ("a.txt", "1\nTARGET\n3\n"));
        _dialogs.OpenPath = patch;
        using var tab = await OpenAsync(target);

        await tab.Patches.ApplyPatchCommand.ExecuteAsync(null);

        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Warning && n.Title == "Conflit détecté");
        Assert.True(tab.Merge.HasOperation);
        Assert.Equal(RepositoryOperationKind.ApplyMailbox, tab.Merge.Operation?.Kind);
        Assert.True(tab.Merge.CanContinue);
        Assert.Equal("Abandonner le patch", tab.Merge.AbortLabel);
        Assert.Contains("Source edit", tab.Merge.OperationTitle, StringComparison.Ordinal);
        Assert.False(tab.Patches.CanApply);

        await tab.Merge.AbortCommand.ExecuteAsync(null);

        Assert.False(tab.Merge.HasOperation);
        Assert.Equal("Target edit", target.Git("log", "-1", "--format=%s").Trim());
    }

    private sealed class FakeFileDialogs : IFileDialogs
    {
        public string? SavePath { get; set; }

        public string? OpenPath { get; set; }

        public string? SuggestedName { get; private set; }

        public Task<string?> PickSaveFileAsync(string suggestedName, IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken = default)
        {
            SuggestedName = suggestedName;
            return Task.FromResult(SavePath);
        }

        public Task<string?> PickOpenFileAsync(IReadOnlyList<FileDialogFilter> filters, CancellationToken cancellationToken = default) =>
            Task.FromResult(OpenPath);
    }

    private sealed class FakeConfirmations : IConfirmationService
    {
        public bool Answer { get; set; } = true;

        public List<string> Requested { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default)
        {
            Requested.Add(title);
            return Task.FromResult(Answer);
        }

        public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Requested.Add(request.Title);
            return Task.FromResult(new ConfirmationOutcome(Answer, request.OptionChecked));
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
