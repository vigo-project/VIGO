using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class ConflictEditorViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly TestRepoBuilder _repo;

    /// <summary>
    /// main and feature both change line 2 of a.txt (and b.txt when asked); feature is merged into main and stops on
    /// the conflicts. LF line endings whatever the user's core.autocrlf.
    /// </summary>
    public ConflictEditorViewModelTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Git("config", "core.autocrlf", "false");
        _repo.Commit("base", ("a.txt", "1\n2\n3\n"), ("b.txt", "x\n"));
    }

    public void Dispose() => _repo.Dispose();

    private async Task<RepositoryTabViewModel> MergeWithConflictsAsync(bool twoFiles = false, Preferences? preferences = null)
    {
        _repo.Branch("feature");
        _repo.Commit("feature", twoFiles ? [("a.txt", "1\nfeature\n3\n"), ("b.txt", "feature\n")] : [("a.txt", "1\nfeature\n3\n")]);
        _repo.Checkout("main");
        _repo.Commit("main", twoFiles ? [("a.txt", "1\nmain\n3\n"), ("b.txt", "main\n")] : [("a.txt", "1\nmain\n3\n")]);

        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, _confirmations,
            preferences: preferences);
        await tab.LoadAsync();
        Assert.IsType<Results.Conflict>((await _git.MergeAsync(_repo.Path, "refs/heads/feature", TestContext.Current.CancellationToken)).Value);
        await tab.WorkingTree.RefreshAsync();
        return tab;
    }

    private static async Task OpenAsync(RepositoryTabViewModel tab, string path)
    {
        tab.WorkingTree.OpenFileCommand.Execute(tab.WorkingTree.Conflicted.Single(c => c.Path == path));
        // Opened asynchronously (event handler): wait until the file is read.
        for (var i = 0; i < 200 && (tab.Conflicts.IsLoading || tab.Conflicts.File?.Path != path); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(path, tab.Conflicts.File?.Path);
    }

    [Fact]
    public void A_choice_keeps_the_lines_of_a_side_a_block_or_single_lines()
    {
        var choice = new ConflictChoice(new ConflictHunk(["o1", "o2"], ["b"], ["t1"]));
        Assert.False(choice.IsResolved);
        Assert.Empty(choice.ResultLines);

        choice.Take(ConflictTake.Both);
        Assert.True(choice.IsResolved);
        Assert.Equal([("o1", ConflictSide.Ours), ("o2", ConflictSide.Ours), ("t1", ConflictSide.Theirs)], choice.ResultLines);

        choice.Toggle(ConflictSide.Ours, 0);
        Assert.Equal([("o2", ConflictSide.Ours), ("t1", ConflictSide.Theirs)], choice.ResultLines);

        choice.Take(ConflictTake.Theirs);
        Assert.Equal([("t1", ConflictSide.Theirs)], choice.ResultLines);

        choice.Reset();
        Assert.False(choice.IsResolved);
    }

    [Fact]
    public void The_result_text_keeps_the_line_ending_and_final_newline_of_the_file()
    {
        var crlf = new ConflictFile("a.txt", ConflictKind.BothModified, true, true, false, [], "\r\n", EndsWithNewline: true);
        var noFinalNewline = crlf with { LineEnding = "\n", EndsWithNewline = false };

        Assert.Equal("a\r\nb\r\n", crlf.ToFileText("a\nb\n"));
        Assert.Equal("a\nb", noFinalNewline.ToFileText("a\r\nb\n"));
    }

    [Fact]
    public async Task A_conflicted_file_opens_in_the_conflict_editor()
    {
        using var tab = await MergeWithConflictsAsync();

        await OpenAsync(tab, "a.txt");

        Assert.True(tab.Conflicts.IsOpen);
        Assert.False(tab.Diff.IsOpen);
        Assert.False(tab.ShowGraph);
        Assert.True(tab.Conflicts.IsText);
        var choice = Assert.Single(tab.Conflicts.Choices);
        Assert.Equal(["main"], choice.Hunk.Ours);
        Assert.Equal(["feature"], choice.Hunk.Theirs);
        Assert.Equal("Fusion de feature dans main", tab.Conflicts.Title);
        Assert.Equal("a.txt · fichier 1 sur 1 en conflit", tab.Conflicts.Subtitle);
        Assert.Equal("1 conflit restant", tab.Conflicts.RemainingText);
        Assert.Equal("Conflit 1 / 1", tab.Conflicts.PositionText);
        Assert.Equal("Conflit 1 · 1 ligne", tab.Conflicts.BlockLabel(0, ConflictSide.Ours));
        Assert.Equal("feature", tab.Conflicts.TheirsDetail);
        Assert.False(tab.Conflicts.CanMarkResolved);
    }

    [Fact]
    public async Task Marking_resolved_writes_the_result_stages_it_and_goes_back_to_the_graph()
    {
        using var tab = await MergeWithConflictsAsync();
        await OpenAsync(tab, "a.txt");
        var changed = new List<int>();
        tab.Conflicts.BlockChanged += (_, block) => changed.Add(block);

        tab.Conflicts.TakeBlock(0, ConflictTake.Both);
        Assert.Equal([0], changed);
        Assert.True(tab.Conflicts.CanMarkResolved);
        Assert.Equal("Conflit 1 · 1 ligne · résolu", tab.Conflicts.BlockLabel(0, ConflictSide.Theirs));
        // The editor's text, as the result pane builds it: common lines + the lines of each conflict.
        await tab.Conflicts.MarkResolvedAsync("1\nmain\nfeature\n3\n");

        Assert.Equal("1\nmain\nfeature\n3\n", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")));
        Assert.Equal("1\nmain\nfeature\n3\n", _repo.Git("show", ":a.txt"));
        Assert.False(tab.WorkingTree.HasConflicts);
        Assert.False(tab.Conflicts.IsOpen);
        Assert.True(tab.ShowGraph);
        Assert.Contains(_notifier.Items, n => n.Title == "Conflits résolus");
    }

    [Fact]
    public async Task Resolving_a_file_opens_the_next_conflicted_one()
    {
        using var tab = await MergeWithConflictsAsync(twoFiles: true);
        await OpenAsync(tab, "a.txt");

        tab.Conflicts.TakeAll(ConflictSide.Ours);
        await tab.Conflicts.MarkResolvedAsync("1\nmain\n3\n");

        Assert.True(tab.Conflicts.IsOpen);
        Assert.Equal("b.txt", tab.Conflicts.File?.Path);
        Assert.Equal("b.txt · fichier 1 sur 1 en conflit", tab.Conflicts.Subtitle);
    }

    [Fact]
    public async Task Closing_with_choices_asks_first()
    {
        using var tab = await MergeWithConflictsAsync();
        await OpenAsync(tab, "a.txt");
        tab.Conflicts.ToggleLine(0, ConflictSide.Theirs, 0);
        Assert.True(tab.Conflicts.HasChanges);

        _confirmations.Answer = false;
        await tab.Conflicts.CloseCommand.ExecuteAsync(null);
        Assert.True(tab.Conflicts.IsOpen);

        _confirmations.Answer = true;
        await tab.Conflicts.CloseCommand.ExecuteAsync(null);
        Assert.False(tab.Conflicts.IsOpen);
    }

    [Fact]
    public async Task Reset_unresolves_every_conflict()
    {
        using var tab = await MergeWithConflictsAsync();
        await OpenAsync(tab, "a.txt");
        var version = tab.Conflicts.Version;
        tab.Conflicts.TakeBlock(0, ConflictTake.Ours);

        tab.Conflicts.ResetCommand.Execute(null);

        Assert.False(tab.Conflicts.Choices[0].IsResolved);
        Assert.False(tab.Conflicts.HasChanges);
        Assert.True(tab.Conflicts.Version > version);
    }

    [Fact]
    public async Task A_file_deleted_on_one_side_is_resolved_with_a_whole_version()
    {
        _repo.Branch("feature");
        _repo.Git("rm", "-q", "a.txt");
        _repo.Git("commit", "-q", "-m", "delete");
        _repo.Checkout("main");
        _repo.Commit("main", ("a.txt", "1\nmain\n3\n"));
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", null, false, false, false);
        using var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, _confirmations);
        await tab.LoadAsync();
        Assert.IsType<Results.Conflict>((await _git.MergeAsync(_repo.Path, "refs/heads/feature", TestContext.Current.CancellationToken)).Value);
        await tab.WorkingTree.RefreshAsync();
        await OpenAsync(tab, "a.txt");

        Assert.True(tab.Conflicts.IsWholeFile);
        Assert.Equal("Modifié de notre côté, supprimé de leur côté", tab.Conflicts.WholeFileTitle);
        Assert.Equal("Garder la suppression (leur version)", tab.Conflicts.KeepTheirsLabel);
        await tab.Conflicts.KeepSideCommand.ExecuteAsync(ConflictSide.Ours);

        Assert.Equal("1\nmain\n3\n", _repo.Git("show", ":a.txt"));
        Assert.False(tab.WorkingTree.HasConflicts);
        Assert.False(tab.Conflicts.IsOpen);
    }

    [Fact]
    public async Task The_preferred_merge_tool_replaces_the_editor()
    {
        // A "tool" git runs through sh: takes their version and reports success.
        _repo.Git("config", "mergetool.vigo-test.cmd", "cp \"$REMOTE\" \"$MERGED\"");
        _repo.Git("config", "mergetool.vigo-test.trustExitCode", "true");
        var store = new InMemoryPreferencesStore();
        using var preferences = new Preferences(store, new UserPreferences { MergeTool = "vigo-test" });
        using var tab = await MergeWithConflictsAsync(preferences: preferences);

        await tab.Conflicts.OpenAsync(tab.WorkingTree.Conflicted.Single().Entry);

        Assert.Equal("1\nfeature\n3\n", _repo.Git("show", ":a.txt"));
        Assert.False(tab.WorkingTree.HasConflicts);
        Assert.False(tab.Conflicts.IsOpen);
        Assert.Contains(_notifier.Items, n => n.Title == "Conflit résolu");
    }

    private sealed class FakeConfirmations : IConfirmationService
    {
        public bool Answer { get; set; } = true;

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer);

        public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConfirmationOutcome(Answer, request.OptionChecked));
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
