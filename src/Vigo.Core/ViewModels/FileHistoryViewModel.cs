using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Screen showing a file: its history or its blame.</summary>
public enum FileInspectionKind
{
    History,
    Blame,
}

/// <summary>Request to show the history or the blame of a file (menus of the file lists, palette, links between the two screens).</summary>
/// <param name="Path">Repository-relative path.</param>
/// <param name="Revision">Version to blame (SHA or <c>HEAD</c>); null for the file on disk. Ignored for a history.</param>
public sealed record FileInspection(FileInspectionKind Kind, string Path, string? Revision = null);

/// <summary>Commit of the history list (mockup: subject, avatar, author · date · SHA, rename note, +/− lines).</summary>
public sealed class FileHistoryRow(FileHistoryEntry entry, string date)
{
    public FileHistoryEntry Entry { get; } = entry;

    public CommitInfo Commit => Entry.Commit;

    public ChangedFile File => Entry.File;

    public string Subject => Commit.Subject;

    public string AuthorName => Commit.AuthorName;

    public string AuthorInitials => new GitIdentity(Commit.AuthorName, null).Initials;

    public int AuthorColorIndex => StableColor.IndexFor(Commit.AuthorName);

    public string DateText { get; } = date;

    public string ShortSha => Commit.Sha.ToShortString();

    /// <summary>"Ada Lovelace · il y a 2 j · a1b2c3d".</summary>
    public string Meta => $"{AuthorName} · {DateText} · {ShortSha}";

    public string AdditionsText => File.IsBinary ? Loc.Get("FileHistory.Binary") : $"+{File.Additions}";

    public string DeletionsText => File is { IsBinary: false, Deletions: > 0 } ? $"−{File.Deletions}" : string.Empty;

    /// <summary>Rename, creation or deletion of the file in this commit; null for a plain change.</summary>
    public string? Note => File.Kind switch
    {
        FileChangeKind.Renamed => Loc.Format("FileHistory.Note.Renamed", System.IO.Path.GetFileName(File.OldPath)),
        FileChangeKind.Copied => Loc.Format("FileHistory.Note.Copied", System.IO.Path.GetFileName(File.OldPath)),
        FileChangeKind.Added => Loc.Get("FileHistory.Note.Added"),
        FileChangeKind.Deleted => Loc.Get("FileHistory.Note.Deleted"),
        _ => null,
    };

    public bool HasNote => Note is not null;

    /// <summary>Name read by the narrator (and by UI Automation).</summary>
    public override string ToString() => $"{Subject}, {ShortSha}";
}

/// <summary>
/// History of a file (J5.6, mockup VIGO Outils.dc.html, "Historique" screen), shown in place of the graph: the commits
/// that changed it (renames followed) on the left, the diff of the selected one on the right, "Blame à ce commit" and
/// "Restaurer cette version". Two selected commits compare the file's versions in them; the diff is unified or side by
/// side (layout shared with the diff view).
/// </summary>
public sealed partial class FileHistoryViewModel : ObservableObject
{
    private const int PageSize = 200;

    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly TimeProvider _time;
    private readonly bool _hasWorkingTree;
    private readonly IConfirmationService _confirmations;
    private readonly IUserNotifier? _notifier;
    private readonly Preferences? _preferences;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _diffLoading;
    private bool _selecting;

    // Versions compared by the shown diff, read whole for the side-by-side layout.
    private (FileVersion? Old, FileVersion? New) _versions;

    public FileHistoryViewModel(
        IGitService git, string repositoryPath, TimeProvider time, bool hasWorkingTree, IConfirmationService? confirmations,
        IUserNotifier? notifier, CancellationToken lifetime, Preferences? preferences = null)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _time = time;
        _hasWorkingTree = hasWorkingTree;
        _confirmations = confirmations ?? RefuseConfirmations.Instance;
        _notifier = notifier;
        _preferences = preferences;
        _lifetime = lifetime;
        Layout = preferences?.Current.DiffLayout ?? DiffLayout.Unified;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why the list is empty (no commit, read error); null otherwise.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>File whose history is shown (repository-relative path, as it is now).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(Directory))]
    public partial string? Path { get; private set; }

    public string FileName => Path is { } p ? p[(p.LastIndexOf('/') + 1)..] : string.Empty;

    public string Directory => Path is { } p && p.LastIndexOf('/') is var i and > 0 ? p[..(i + 1)] : string.Empty;

    /// <summary>Renames followed (<c>git log --follow</c>; mockup: check box "Suivre les renommages").</summary>
    [ObservableProperty]
    public partial bool Follow { get; set; } = true;

    public ObservableCollection<FileHistoryRow> Entries { get; } = [];

    /// <summary>Selected commit; the newer one when two are compared.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BlameCommand), nameof(RestoreCommand))]
    public partial FileHistoryRow? SelectedEntry { get; set; }

    /// <summary>
    /// Older commit whose version of the file is compared with the one of <see cref="SelectedEntry"/> (several commits
    /// selected: the oldest one); null for the diff of <see cref="SelectedEntry"/> against its parent.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComparing))]
    [NotifyCanExecuteChangedFor(nameof(BlameCommand), nameof(RestoreCommand))]
    public partial FileHistoryRow? ComparedEntry { get; private set; }

    public bool IsComparing => ComparedEntry is not null;

    /// <summary>Unified or side by side (whole versions); remembered in the preferences, shared with the diff view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSideBySide), nameof(LayoutToggleLabel))]
    public partial DiffLayout Layout { get; private set; }

    public bool IsSideBySide => Layout == DiffLayout.SideBySide;

    public string LayoutToggleLabel => IsSideBySide ? Loc.Get("Diff.Layout.Unified") : Loc.Get("Diff.Layout.SideBySide");

    /// <summary>Whole texts of the two compared versions (side-by-side layout); null until read, or in the unified layout.</summary>
    [ObservableProperty]
    public partial DiffSides? Sides { get; private set; }

    /// <summary>Diff of the file in the selected commit (against its first parent); null while loading or when there is none.</summary>
    [ObservableProperty]
    public partial FileDiff? Diff { get; private set; }

    /// <summary>Why no diff is shown for the selected commit (binary file, read error); null otherwise.</summary>
    [ObservableProperty]
    public partial string? DiffMessage { get; private set; }

    [ObservableProperty]
    public partial bool IsDiffLoading { get; private set; }

    /// <summary>Header texts: the selected commit, or the two compared ones (older first).</summary>
    public string SelectedShortSha => (ComparedEntry, SelectedEntry) switch
    {
        ({ } older, { } newer) => $"{older.ShortSha} → {newer.ShortSha}",
        (_, { } row) => row.ShortSha,
        _ => string.Empty,
    };

    public string SelectedSubject => (ComparedEntry, SelectedEntry) switch
    {
        ({ }, { }) => Loc.Get("FileHistory.CompareTitle"),
        (_, { } row) => row.Subject,
        _ => string.Empty,
    };

    /// <summary>Raised by "Blame à ce commit".</summary>
    public event EventHandler<FileInspection>? InspectionRequested;

    /// <summary>Shows the history of <paramref name="path"/>, newest commit selected.</summary>
    public async Task OpenAsync(string path)
    {
        Path = path;
        // The diff view may have changed the shared layout since.
        if (_preferences is not null) Layout = _preferences.Current.DiffLayout;
        IsOpen = true;
        await LoadAsync();
    }

    /// <summary>
    /// Commits selected in the list: one shows its diff against its parent; two or more compare the file in the oldest
    /// one with the file in the newest one.
    /// </summary>
    public void SelectEntries(IReadOnlyList<FileHistoryRow> rows)
    {
        // Newest first, as listed.
        var ordered = rows.Where(Entries.Contains).OrderBy(Entries.IndexOf).ToList();
        var selected = ordered.Count > 0 ? ordered[0] : null;
        var compared = ordered.Count > 1 ? ordered[^1] : null;
        if (selected == SelectedEntry && compared == ComparedEntry) return;

        _selecting = true;
        try
        {
            ComparedEntry = compared;
            SelectedEntry = selected;
        }
        finally
        {
            _selecting = false;
        }
        OnSelectionChanged();
    }

    [RelayCommand]
    private async Task ToggleLayoutAsync()
    {
        var layout = Layout = IsSideBySide ? DiffLayout.Unified : DiffLayout.SideBySide;
        if (IsSideBySide && IsOpen && !IsDiffLoading && Diff is not null && _diffLoading is { } loading) await LoadSidesAsync(loading);
        if (_preferences is not null) await _preferences.UpdateAsync(p => p with { DiffLayout = layout });
    }

    partial void OnFollowChanged(bool value)
    {
        if (IsOpen) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (Path is not { } path) return;
        _loading?.Cancel();
        _diffLoading?.Cancel();
        var cts = _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Entries.Clear();
        ComparedEntry = null;
        SelectedEntry = null;
        Message = null;
        IsLoading = true;
        var context = SynchronizationContext.Current;
        try
        {
            var result = await _git.ReadFileHistoryAsync(_repositoryPath, path, Follow, PageSize, async (page, _) =>
            {
                await GraphViewModel.RunOnAsync(context, () =>
                {
                    if (cts.IsCancellationRequested) return;
                    var now = _time.GetUtcNow();
                    foreach (var entry in page)
                        Entries.Add(new FileHistoryRow(entry, RelativeTime.Format(entry.Commit.AuthorDate, now, _time.LocalTimeZone)));
                    SelectedEntry ??= Entries.FirstOrDefault();
                });
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            switch (result)
            {
                case Done:
                    if (Entries.Count == 0) Message = Loc.Format("FileHistory.NoCommit", FileName);
                    break;
                case Failure f:
                    Message = Loc.Format("FileHistory.LoadFailed", f.Message);
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

    partial void OnSelectedEntryChanged(FileHistoryRow? value)
    {
        // One commit chosen by the model (list loaded) or set directly: no comparison any more.
        if (_selecting) return;
        ComparedEntry = null;
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedShortSha));
        OnPropertyChanged(nameof(SelectedSubject));
        _ = LoadDiffAsync(SelectedEntry, ComparedEntry);
    }

    private async Task LoadDiffAsync(FileHistoryRow? row, FileHistoryRow? older)
    {
        _diffLoading?.Cancel();
        Diff = null;
        Sides = null;
        DiffMessage = null;
        _versions = default;
        if (row is null)
        {
            IsDiffLoading = false;
            return;
        }

        var cts = _diffLoading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        IsDiffLoading = true;
        try
        {
            var commit = row.Commit;
            GitSha? from;
            ChangedFile file;
            if (older is not null)
            {
                // The file as it was in each commit (its path then); a version where it is deleted reads as empty.
                from = older.Commit.Sha;
                var oldPath = older.File.Path;
                file = oldPath == row.File.Path
                    ? new ChangedFile(row.File.Path, null, FileChangeKind.Modified)
                    : new ChangedFile(row.File.Path, oldPath, FileChangeKind.Renamed);
                _versions = (FileVersion.AtCommit(older.Commit.Sha.ToString(), oldPath), FileVersion.AtCommit(commit.Sha.ToString(), row.File.Path));
            }
            else
            {
                from = commit.Parents.Count > 0 ? commit.Parents[0] : null;
                file = row.File;
                _versions = (
                    from is { } parent ? FileVersion.AtCommit(parent.ToString(), file.OldPath ?? file.Path) : null,
                    FileVersion.AtCommit(commit.Sha.ToString(), file.Path));
            }
            var result = await _git.GetFileDiffAsync(_repositoryPath, commit.Sha, from, file, cts.Token);
            if (cts.IsCancellationRequested) return;
            switch (result)
            {
                case Success<FileDiff> { Value: { IsBinary: true } }:
                    DiffMessage = Loc.Get("FileHistory.BinaryNoDiff");
                    break;
                case Success<FileDiff> { Value.Hunks.Count: 0 }:
                    DiffMessage = older is not null
                        ? Loc.Get("FileHistory.SameContent")
                        : Loc.Get("FileHistory.NoContentChange");
                    break;
                case Success<FileDiff> s:
                    // Side by side: both versions read before the diff is published, so the view shows them at once.
                    if (IsSideBySide) await LoadSidesAsync(cts);
                    if (cts.IsCancellationRequested) return;
                    Diff = s.Value;
                    break;
                case Failure f:
                    DiffMessage = Loc.Format("Diff.ShowFailed", f.Message);
                    break;
                case Canceled or Conflict or AuthenticationFailed:
                    break;
            }
        }
        finally
        {
            if (_diffLoading == cts) IsDiffLoading = false;
        }
    }

    /// <summary>Reads the whole texts of the two compared versions (side-by-side layout).</summary>
    private async Task LoadSidesAsync(CancellationTokenSource loading)
    {
        var (oldVersion, newVersion) = _versions;
        if (newVersion is null) return;
        var oldTask = Read(oldVersion);
        var newTask = Read(newVersion);
        var (original, modified) = (await oldTask, await newTask);
        if (loading.IsCancellationRequested || loading != _diffLoading) return;

        if (original is not Success<string?> o || modified is not Success<string?> m)
        {
            var error = Error(original) ?? Error(modified);
            Sides = null;
            if (!string.IsNullOrEmpty(error)) DiffMessage = Loc.Format("Diff.BothVersionsFailed", error);
            return;
        }
        Sides = new DiffSides(newVersion.Path, oldVersion?.Path ?? newVersion.Path, o.Value ?? string.Empty, m.Value ?? string.Empty);

        Task<GitResult<string?>> Read(FileVersion? version) => version is null
            ? Task.FromResult<GitResult<string?>>(new Success<string?>(null))
            : _git.ReadFileTextAsync(_repositoryPath, version, loading.Token);

        // Null when the text was read; the reason otherwise (empty when canceled: nothing to show).
        static string? Error(GitResult<string?> result) => result switch
        {
            Success<string?> => null,
            Failure f => f.Message,
            Canceled => string.Empty,
            Conflict or AuthenticationFailed => Loc.Get("Diff.ReadImpossible"),
        };
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        _loading?.Cancel();
        _diffLoading?.Cancel();
        IsOpen = false;
        Entries.Clear();
        ComparedEntry = null;
        SelectedEntry = null;
        Message = null;
        Path = null;
    }

    // Both act on one version: not while two are compared.
    private bool CanBlame() => ComparedEntry is null && SelectedEntry is { File.Kind: not FileChangeKind.Deleted };

    /// <summary>"Blame à ce commit": the file as it was in the selected commit (its path then).</summary>
    [RelayCommand(CanExecute = nameof(CanBlame))]
    private void Blame()
    {
        if (SelectedEntry is not { } row) return;
        InspectionRequested?.Invoke(this, new FileInspection(FileInspectionKind.Blame, row.File.Path, row.Commit.Sha.ToString()));
    }

    private bool CanRestore() => _hasWorkingTree && ComparedEntry is null && SelectedEntry is { File.Kind: not FileChangeKind.Deleted };

    /// <summary>
    /// "Restaurer cette version": the file of the working directory gets its content of the selected commit (not
    /// staged). Confirmed first: its uncommitted changes are lost.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        if (SelectedEntry is not { } row || Path is not { } path) return;
        var confirmed = await _confirmations.ConfirmAsync(new ConfirmationRequest(
            Loc.Format("FileHistory.Restore.Title", FileName),
            Loc.Format("FileHistory.Restore.Message", row.ShortSha, row.Subject),
            Loc.Get("FileHistory.Restore.Confirm"),
            IsDestructive: true), _lifetime);
        if (!confirmed.Confirmed) return;

        var result = await _git.RestoreFileAsync(_repositoryPath, row.Commit.Sha, row.File.Path, path, _lifetime);
        switch (result)
        {
            case Done:
                _notifier?.Notify(NotificationSeverity.Success, Loc.Get("FileHistory.Restored.Title"),
                    Loc.Format("FileHistory.Restored.Message", FileName, row.ShortSha));
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Error, Loc.Get("FileHistory.RestoreFailed"), f.Message);
                break;
            case Canceled or Conflict or AuthenticationFailed:
                break;
        }
    }
}
