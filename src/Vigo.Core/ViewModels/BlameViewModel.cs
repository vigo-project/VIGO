using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Commit of the blame column (mockup: avatar, subject, date, age bar).</summary>
public sealed class BlameCommitItem(BlameCommit commit, string date, string fullDate)
{
    public BlameCommit Commit { get; } = commit;

    public string Summary => Commit.IsUncommitted ? Loc.Get("Blame.Uncommitted") : Commit.Summary;

    public string AuthorName => Commit.IsUncommitted ? Loc.Get("Blame.You") : Commit.AuthorName;

    public string AuthorInitials => new GitIdentity(AuthorName, null).Initials;

    public int AuthorColorIndex => StableColor.IndexFor(AuthorName);

    public string DateText { get; } = date;

    /// <summary>Date and time of the commit (card of the selected commit).</summary>
    public string FullDateText { get; } = fullDate;

    public string ShortSha => Commit.IsUncommitted ? string.Empty : Commit.Sha.ToShortString();

    /// <summary>0 for the oldest commit of the file, 1 for the newest (opacity of the age bar).</summary>
    public double Age { get; internal set; }

    /// <summary>Lines of the blamed file last changed by this commit.</summary>
    public int LineCount { get; internal set; }
}

/// <summary>
/// Blame of a file (J5.6, mockup VIGO Outils.dc.html, "Blame" screen), shown in place of the graph: the file in the
/// code editor with, on the left of each block of lines, the commit that last changed it; a card of the selected commit
/// ("Afficher dans le graphe", "Blame avant ce commit").
/// </summary>
/// <remarks>
/// The file text and the blame are read at the same time; the blame arrives in batches (<see cref="BlameVersion"/>),
/// the lines without a commit yet stay blank.
/// </remarks>
public sealed partial class BlameViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly TimeProvider _time;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<GitSha, int> _indexes = [];
    private readonly List<BlameCommitItem> _commits = [];
    private CancellationTokenSource? _loading;
    private int[] _lineCommits = [];

    public BlameViewModel(IGitService git, string repositoryPath, TimeProvider time, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _time = time;
        _lifetime = lifetime;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Blamed file (repository-relative path in <see cref="Revision"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(Directory))]
    public partial string? Path { get; private set; }

    public string FileName => Path is { } p ? p[(p.LastIndexOf('/') + 1)..] : string.Empty;

    public string Directory => Path is { } p && p.LastIndexOf('/') is var i and > 0 ? p[..(i + 1)] : string.Empty;

    /// <summary>Blamed version: a SHA, <c>HEAD</c>, or null for the file on disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RevisionLabel))]
    public partial string? Revision { get; private set; }

    /// <summary>"HEAD", "a1b2c3d" or "répertoire de travail" (mockup: chip "à HEAD").</summary>
    public string RevisionLabel => Revision switch
    {
        null => Loc.Get("Blame.WorkingTree"),
        "HEAD" => "HEAD",
        var sha => GitSha.TryParse(sha, out var id) ? id.ToShortString() : sha,
    };

    /// <summary>Text of the blamed version; null while it is read.</summary>
    public string? Text { get; private set; }

    /// <summary>Changes each time another file or version is shown (the editor gets the whole text again).</summary>
    [ObservableProperty]
    public partial int TextVersion { get; private set; }

    /// <summary>Commits found so far; <see cref="LineCommits"/> indexes them.</summary>
    public IReadOnlyList<BlameCommitItem> Commits => _commits;

    /// <summary>Index in <see cref="Commits"/> of the commit of each line (0-based line); -1 while unknown.</summary>
    public IReadOnlyList<int> LineCommits => _lineCommits;

    /// <summary>Changes with each batch of blamed lines.</summary>
    [ObservableProperty]
    public partial int BlameVersion { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedIndex), nameof(SelectedLinesText))]
    [NotifyCanExecuteChangedFor(nameof(ShowInGraphCommand), nameof(BlameBeforeCommand))]
    public partial BlameCommitItem? SelectedCommit { get; private set; }

    public bool HasSelection => SelectedCommit is not null;

    /// <summary>Index of <see cref="SelectedCommit"/> in <see cref="Commits"/>; -1 when none.</summary>
    public int SelectedIndex => SelectedCommit is { } c ? _commits.IndexOf(c) : -1;

    public string SelectedLinesText => SelectedCommit is { LineCount: var n }
        ? n == 1 ? Loc.Get("Blame.Lines.One") : Loc.Format("Blame.Lines.Many", n)
        : string.Empty;

    /// <summary>Raised by "Historique du fichier".</summary>
    public event EventHandler<FileInspection>? InspectionRequested;

    /// <summary>Raised by "Afficher dans le graphe".</summary>
    public event EventHandler<GitSha>? CommitShown;

    /// <summary>Shows the blame of <paramref name="path"/> at <paramref name="revision"/> (null: the file on disk).</summary>
    public Task OpenAsync(string path, string? revision)
    {
        IsOpen = true;
        return LoadAsync(path, revision);
    }

    private async Task LoadAsync(string path, string? revision)
    {
        _loading?.Cancel();
        var cts = _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Path = path;
        Revision = revision;
        ErrorMessage = null;
        SelectedCommit = null;
        Text = null;
        _commits.Clear();
        _indexes.Clear();
        _lineCommits = [];
        TextVersion++;
        BlameVersion++;
        IsLoading = true;
        var context = SynchronizationContext.Current;
        try
        {
            var version = revision is null ? FileVersion.OnDisk(path) : FileVersion.AtCommit(revision, path);
            var textTask = _git.ReadFileTextAsync(_repositoryPath, version, cts.Token);
            var pending = new List<BlameHunk>();
            var blameTask = _git.BlameAsync(_repositoryPath, path, revision, async (hunks, _) =>
            {
                await GraphViewModel.RunOnAsync(context, () =>
                {
                    if (cts.IsCancellationRequested) return;
                    // Lines are placed once the text is known (its line count); until then they wait.
                    if (Text is null) pending.AddRange(hunks);
                    else Apply(hunks);
                });
            }, cts.Token);

            var text = await textTask;
            if (cts.IsCancellationRequested) return;
            switch (text)
            {
                case Success<string?> { Value: { } content }:
                    Text = content;
                    _lineCommits = new int[LineCount(content)];
                    Array.Fill(_lineCommits, -1);
                    TextVersion++;
                    Apply(pending);
                    pending.Clear();
                    break;
                case Success<string?>:
                    ErrorMessage = Loc.Format("Blame.NotInVersion", path);
                    break;
                case Failure f:
                    ErrorMessage = f.Message;
                    break;
                case Canceled or Conflict or AuthenticationFailed:
                    break;
            }

            var blame = await blameTask;
            if (cts.IsCancellationRequested) return;
            switch (blame)
            {
                case Done:
                    break;
                case Failure f:
                    ErrorMessage ??= Loc.Format("Blame.Failed", f.Message);
                    break;
                case Canceled or Conflict or AuthenticationFailed:
                    break;
            }
        }
        finally
        {
            if (_loading == cts) IsLoading = false;
        }
    }

    /// <summary>Lines of a text as the editor counts them, without the empty one after a final line break.</summary>
    private static int LineCount(string text)
    {
        if (text.Length == 0) return 0;
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') count++;
            else if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) count++;
        }
        return text[^1] is '\n' or '\r' ? count - 1 : count;
    }

    private void Apply(IReadOnlyList<BlameHunk> hunks)
    {
        if (hunks.Count == 0) return;
        var now = _time.GetUtcNow();
        var added = false;
        foreach (var hunk in hunks)
        {
            if (!_indexes.TryGetValue(hunk.Commit.Sha, out var index))
            {
                index = _commits.Count;
                _indexes.Add(hunk.Commit.Sha, index);
                var local = TimeZoneInfo.ConvertTime(hunk.Commit.AuthorDate, _time.LocalTimeZone);
                _commits.Add(new BlameCommitItem(hunk.Commit,
                    RelativeTime.Format(hunk.Commit.AuthorDate, now, _time.LocalTimeZone),
                    local.ToString(Loc.Get("Blame.FullDateFormat"), CultureInfo.CurrentCulture)));
                added = true;
            }
            var item = _commits[index];
            for (var line = hunk.Line; line < hunk.Line + hunk.LineCount && line <= _lineCommits.Length; line++)
            {
                if (_lineCommits[line - 1] == index) continue;
                _lineCommits[line - 1] = index;
                item.LineCount++;
            }
        }
        if (added) UpdateAges();
        BlameVersion++;
        OnPropertyChanged(nameof(SelectedLinesText));
    }

    /// <summary>Age of each commit by rank of its date: the newest gets 1, the oldest 0 (uncommitted lines are the newest).</summary>
    private void UpdateAges()
    {
        var dates = _commits
            .Select(c => c.Commit.IsUncommitted ? DateTimeOffset.MaxValue : c.Commit.AuthorDate)
            .Distinct()
            .Order()
            .ToList();
        foreach (var item in _commits)
        {
            var date = item.Commit.IsUncommitted ? DateTimeOffset.MaxValue : item.Commit.AuthorDate;
            item.Age = dates.Count <= 1 ? 1 : (double)dates.BinarySearch(date) / (dates.Count - 1);
        }
    }

    /// <summary>A line of the editor was clicked (1-based): its commit becomes the selected one.</summary>
    public void SelectLine(int line)
    {
        if (line < 1 || line > _lineCommits.Length || _lineCommits[line - 1] is not (var index and >= 0)) return;
        SelectedCommit = _commits[index];
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        _loading?.Cancel();
        IsOpen = false;
        SelectedCommit = null;
        Text = null;
        _commits.Clear();
        _indexes.Clear();
        _lineCommits = [];
        Path = null;
        TextVersion++;
        BlameVersion++;
    }

    /// <summary>Chip "à …": blame of another version of the same file (<c>HEAD</c>, or null for the file on disk).</summary>
    public Task ShowRevisionAsync(string? revision) =>
        Path is { } path && revision != Revision ? LoadAsync(path, revision) : Task.CompletedTask;

    private bool CanShowInGraph() => SelectedCommit is { Commit.IsUncommitted: false };

    [RelayCommand(CanExecute = nameof(CanShowInGraph))]
    private void ShowInGraph()
    {
        if (SelectedCommit is { } item) CommitShown?.Invoke(this, item.Commit.Sha);
    }

    private bool CanBlameBefore() => SelectedCommit is { Commit.Previous: not null };

    /// <summary>"Blame avant ce commit": the file as it was in the parent of the selected commit (its path there).</summary>
    [RelayCommand(CanExecute = nameof(CanBlameBefore))]
    private Task BlameBeforeAsync() =>
        SelectedCommit?.Commit.Previous is { } previous ? LoadAsync(previous.Path, previous.Sha.ToString()) : Task.CompletedTask;

    [RelayCommand]
    private void ShowHistory()
    {
        if (Path is { } path) InspectionRequested?.Invoke(this, new FileInspection(FileInspectionKind.History, path));
    }
}
