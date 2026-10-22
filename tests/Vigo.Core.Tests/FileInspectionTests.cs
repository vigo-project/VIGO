using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>History and blame of a file (J5.6), through the repository tab.</summary>
public sealed class FileInspectionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());
    private readonly RecordingNotifier _notifier = new();
    private readonly FakeConfirmations _confirmations = new();
    private readonly TestRepoBuilder _repo;
    private readonly string _first;
    private readonly string _second;

    /// <summary>first: a.txt (3 lines) and b.txt; second: line 2 of a.txt changed, a line added.</summary>
    public FileInspectionTests()
    {
        _repo = TestRepoBuilder.Create();
        _first = _repo.Commit("first", ("a.txt", "1\n2\n3\n"), ("b.txt", "b\n"));
        _second = _repo.Commit("second", ("a.txt", "1\nTWO\n3\n4\n"));
    }

    public void Dispose() => _repo.Dispose();

    private async Task<RepositoryTabViewModel> OpenAsync(Preferences? preferences = null)
    {
        var info = new RepositoryInfo(_repo.Path, Path.Combine(_repo.Path, ".git"), "repo", "main", _second, false, false, false);
        var tab = new RepositoryTabViewModel(
            info, _git, new FakeTimeProvider(Now), NullLogger.Instance, watchers: null, _notifier, _confirmations, preferences: preferences);
        await tab.LoadAsync();
        return tab;
    }

    /// <summary>The diff of the selected commit loads in the background.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task History_lists_the_commits_of_the_file_and_shows_the_diff_of_the_newest()
    {
        using var tab = await OpenAsync();

        await tab.InspectAsync(new FileInspection(FileInspectionKind.History, "a.txt"));

        Assert.True(tab.FileHistory.IsOpen);
        Assert.False(tab.ShowGraph);
        Assert.Equal(["second", "first"], tab.FileHistory.Entries.Select(e => e.Subject));
        Assert.Equal("+2", tab.FileHistory.Entries[0].AdditionsText);
        Assert.Equal("−1", tab.FileHistory.Entries[0].DeletionsText);
        Assert.Equal("Créé", tab.FileHistory.Entries[1].Note);
        Assert.Same(tab.FileHistory.Entries[0], tab.FileHistory.SelectedEntry);
        await WaitUntilAsync(() => tab.FileHistory.Diff is not null);
        Assert.Equal("a.txt", tab.FileHistory.Diff!.Path);

        tab.FileHistory.CloseCommand.Execute(null);
        Assert.True(tab.ShowGraph);
    }

    [Fact]
    public async Task Two_selected_commits_compare_the_versions_of_the_file_in_them()
    {
        _repo.Commit("third", ("a.txt", "1\nTWO\n3\n4\n5\n"));
        using var tab = await OpenAsync();
        await tab.InspectAsync(new FileInspection(FileInspectionKind.History, "a.txt"));
        var history = tab.FileHistory;
        Assert.Equal(["third", "second", "first"], history.Entries.Select(e => e.Subject));

        // Selected in any order: the oldest version against the newest one.
        history.SelectEntries([history.Entries[2], history.Entries[0]]);

        Assert.True(history.IsComparing);
        Assert.Same(history.Entries[0], history.SelectedEntry);
        Assert.Same(history.Entries[2], history.ComparedEntry);
        Assert.Equal($"{history.Entries[2].ShortSha} → {history.Entries[0].ShortSha}", history.SelectedShortSha);
        Assert.False(history.BlameCommand.CanExecute(null));
        Assert.False(history.RestoreCommand.CanExecute(null));
        await WaitUntilAsync(() => history.Diff is not null);
        Assert.Equal(3, history.Diff!.Additions);
        Assert.Equal(1, history.Diff.Deletions);

        // Back to one commit: its diff against its parent.
        history.SelectEntries([history.Entries[0]]);
        Assert.False(history.IsComparing);
        Assert.True(history.BlameCommand.CanExecute(null));
        await WaitUntilAsync(() => history.Diff is not null);
        Assert.Equal(1, history.Diff!.Additions);
        Assert.Equal(0, history.Diff.Deletions);
    }

    [Fact]
    public async Task Side_by_side_shows_both_whole_versions_and_follows_the_shared_layout()
    {
        var store = new InMemoryPreferencesStore();
        using var preferences = new Preferences(store, new UserPreferences { DiffLayout = DiffLayout.SideBySide });
        using var tab = await OpenAsync(preferences);
        await tab.InspectAsync(new FileInspection(FileInspectionKind.History, "a.txt"));
        var history = tab.FileHistory;

        Assert.True(history.IsSideBySide);
        await WaitUntilAsync(() => history.Diff is not null);
        Assert.Equal(new DiffSides("a.txt", "a.txt", "1\n2\n3\n", "1\nTWO\n3\n4\n"), history.Sides);

        // The root commit: nothing before it.
        history.SelectEntries([history.Entries[1]]);
        await WaitUntilAsync(() => history.Diff is not null);
        Assert.Equal(new DiffSides("a.txt", "a.txt", string.Empty, "1\n2\n3\n"), history.Sides);

        await history.ToggleLayoutCommand.ExecuteAsync(null);
        Assert.False(history.IsSideBySide);
        Assert.Equal(DiffLayout.Unified, store.Saved?.DiffLayout);

        // Back to side by side with a diff shown: its versions are read at once.
        await history.ToggleLayoutCommand.ExecuteAsync(null);
        Assert.Equal(new DiffSides("a.txt", "a.txt", string.Empty, "1\n2\n3\n"), history.Sides);
    }

    [Fact]
    public async Task Blame_gives_the_commit_of_each_line_and_goes_back_before_a_commit()
    {
        using var tab = await OpenAsync();

        await tab.InspectAsync(new FileInspection(FileInspectionKind.Blame, "a.txt", "HEAD"));

        var blame = tab.Blame;
        Assert.True(blame.IsOpen);
        Assert.Equal("1\nTWO\n3\n4\n", blame.Text);
        Assert.Equal("HEAD", blame.RevisionLabel);
        Assert.Equal(["first", "second", "first", "second"], blame.LineCommits.Select(i => blame.Commits[i].Summary));
        Assert.Equal(1, blame.Commits.Single(c => c.Summary == "second").Age);
        Assert.Equal(0, blame.Commits.Single(c => c.Summary == "first").Age);

        blame.SelectLine(2);
        Assert.Equal("second", blame.SelectedCommit!.Summary);
        Assert.Equal("2 lignes dans ce fichier", blame.SelectedLinesText);
        Assert.True(blame.BlameBeforeCommand.CanExecute(null));

        await blame.BlameBeforeCommand.ExecuteAsync(null);

        Assert.Equal(_first, blame.Revision);
        Assert.Equal("1\n2\n3\n", blame.Text);
        Assert.Equal(["first", "first", "first"], blame.LineCommits.Select(i => blame.Commits[i].Summary));
        Assert.Null(blame.SelectedCommit);
        blame.SelectLine(1);
        // The root commit: nothing before it.
        Assert.False(blame.BlameBeforeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Blame_of_a_modified_file_shows_its_uncommitted_lines()
    {
        _repo.WriteFile("a.txt", "1\nTWO\nlocal\n4\n");
        using var tab = await OpenAsync();
        var item = Assert.Single(tab.WorkingTree.Unstaged);
        Assert.True(item.CanInspect);

        tab.WorkingTree.Inspect(item, FileInspectionKind.Blame);
        await WaitUntilAsync(() => !tab.Blame.IsLoading);

        var blame = tab.Blame;
        Assert.Null(blame.Revision);
        Assert.Equal("répertoire de travail", blame.RevisionLabel);
        var uncommitted = blame.Commits[blame.LineCommits[2]];
        Assert.True(uncommitted.Commit.IsUncommitted);
        Assert.Equal("Modifications non validées", uncommitted.Summary);
        Assert.Equal(1, uncommitted.Age);
        blame.SelectLine(3);
        Assert.False(blame.ShowInGraphCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_two_screens_lead_to_each_other_and_to_the_graph()
    {
        using var tab = await OpenAsync();
        await tab.InspectAsync(new FileInspection(FileInspectionKind.History, "a.txt"));

        // "Blame à ce commit" of the older commit: the file as it was then.
        tab.FileHistory.SelectedEntry = tab.FileHistory.Entries[1];
        tab.FileHistory.BlameCommand.Execute(null);
        await WaitUntilAsync(() => tab.Blame.IsOpen && !tab.Blame.IsLoading);
        Assert.False(tab.FileHistory.IsOpen);
        Assert.Equal(_first, tab.Blame.Revision);

        // "Historique du fichier" of the blame.
        tab.Blame.ShowHistoryCommand.Execute(null);
        await WaitUntilAsync(() => tab.FileHistory.IsOpen && !tab.FileHistory.IsLoading);
        Assert.False(tab.Blame.IsOpen);

        // "Afficher dans le graphe": back to the graph, on the commit.
        await tab.InspectAsync(new FileInspection(FileInspectionKind.Blame, "a.txt", "HEAD"));
        tab.Blame.SelectLine(1);
        tab.Blame.ShowInGraphCommand.Execute(null);
        Assert.True(tab.ShowGraph);
        Assert.Equal(_first, tab.Graph.SelectedSha?.ToString());
    }

    [Fact]
    public async Task Restore_writes_the_selected_version_once_confirmed()
    {
        _repo.WriteFile("a.txt", "local\n");
        using var tab = await OpenAsync();
        await tab.InspectAsync(new FileInspection(FileInspectionKind.History, "a.txt"));
        tab.FileHistory.SelectedEntry = tab.FileHistory.Entries[1];

        await tab.FileHistory.RestoreCommand.ExecuteAsync(null);
        Assert.Equal("local\n", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")));

        _confirmations.Answer = true;
        await tab.FileHistory.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("1\n2\n3\n", File.ReadAllText(Path.Combine(_repo.Path, "a.txt")).ReplaceLineEndings("\n"));
        Assert.Equal(2, _confirmations.Requests.Count);
        Assert.True(_confirmations.Requests[0].IsDestructive);
        Assert.Contains(_notifier.Items, n => n is (NotificationSeverity.Success, "Version restaurée", _));
    }

    [Fact]
    public async Task The_palette_picks_a_file_of_the_repository_then_opens_its_blame()
    {
        using var tab = await OpenAsync();
        var command = tab.GetPaletteEntries().Single(e => e.Label == "Blame d'un fichier…");
        var palette = new CommandPaletteViewModel(new Source([command]));
        var closed = false;
        palette.Closed += (_, _) => closed = true;

        await palette.RunAsync(palette.Items.Single());

        Assert.False(closed);
        Assert.Equal("Blame de quel fichier ?", palette.Placeholder);
        Assert.Equal(["a.txt", "b.txt"], palette.Items.Select(i => i.Entry.Label));
        Assert.Equal("Fichiers du dépôt", palette.Items[0].GroupTitle);
        palette.Query = "b";
        Assert.Equal("b.txt", palette.Items[0].Entry.Label);

        await palette.RunSelectedAsync();
        await WaitUntilAsync(() => tab.Blame.IsOpen && !tab.Blame.IsLoading);

        Assert.True(closed);
        Assert.Equal("b.txt", tab.Blame.Path);
        Assert.Null(tab.Blame.Revision);
    }

    private sealed class Source(IReadOnlyList<PaletteEntry> entries) : IPaletteSource
    {
        public IReadOnlyList<PaletteEntry> Entries { get; } = entries;

        public IEnumerable<PaletteEntry> FindCommits(Func<string, bool> matches, int limit) => [];
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
