using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Read-only unified diff of one file, shown in place of the graph (mockup: diff view): a file of a commit against
/// its first parent, or the staged / unstaged part of a working-directory change.
/// </summary>
public sealed partial class DiffViewModel(
    IGitService git, string repositoryPath, StagingActions? actions = null, Preferences? preferences = null) : ObservableObject, IDisposable
{
    private readonly HashSet<DiffLineRef> _selection = [];
    private CancellationTokenSource? _loading;
    private DiffLineRef? _anchor;
    private DiffLayout _layout = preferences?.Current.DiffLayout ?? DiffLayout.Unified;

    // Versions compared by the shown diff, read whole for the side-by-side layout.
    private (FileVersion? Old, FileVersion? New) _versions;

    /// <summary>The shown diff as one document for the code editor; null while there is no diff.</summary>
    public DiffDocument? Document { get; private set; }

    partial void OnDiffChanged(FileDiff? value) => Document = value is null ? null : DiffDocument.Build(value);

    /// <summary>Selected lines as document lines (1-based, ascending), for the editor.</summary>
    public IReadOnlyList<int> SelectedDocumentLines =>
        Document is { } document ? [.. _selection.Select(document.DocumentLine).Where(l => l > 0).Order()] : [];

    /// <summary><see cref="ToggleLine"/> from a document line of the editor.</summary>
    public void ToggleDocumentLine(int documentLine, bool extend)
    {
        if (Document?.LineAt(documentLine) is { } line) ToggleLine(line, extend);
    }

    /// <summary>Unified (staging of hunks and lines) or side by side (whole files); remembered in the preferences.</summary>
    public DiffLayout Layout
    {
        get => _layout;
        private set
        {
            if (!SetProperty(ref _layout, value)) return;
            OnPropertyChanged(nameof(IsSideBySide));
            OnPropertyChanged(nameof(LayoutToggleLabel));
            if (preferences is not null) _ = preferences.UpdateAsync(p => p with { DiffLayout = value });
        }
    }

    public bool IsSideBySide => Layout == DiffLayout.SideBySide;

    public string LayoutToggleLabel => IsSideBySide ? Loc.Get("Diff.Layout.Unified") : Loc.Get("Diff.Layout.SideBySide");

    /// <summary>Old and new texts of the whole file (side-by-side layout); null until read, or in the unified layout.</summary>
    [ObservableProperty]
    public partial DiffSides? Sides { get; private set; }

    /// <summary>Both versions of an image file (J5.7); null for any other diff, or until read.</summary>
    [ObservableProperty]
    public partial DiffImages? Images { get; private set; }

    /// <summary>The shown diff is the one of an image file, compared as pictures rather than as text.</summary>
    public bool IsImageDiff => Diff is { IsBinary: true } d && DiffImages.IsImagePath(d.Path);

    /// <summary>How the two images are compared; chosen per session.</summary>
    [ObservableProperty]
    public partial ImageCompareMode ImageMode { get; set; } = ImageCompareMode.SideBySide;

    [RelayCommand]
    private Task ToggleLayoutAsync()
    {
        Layout = IsSideBySide ? DiffLayout.Unified : DiffLayout.SideBySide;
        if (!IsSideBySide || !IsOpen) return Task.CompletedTask;
        var loading = _loading;
        return loading is null ? Task.CompletedTask : LoadSidesAsync(loading);
    }

    /// <summary>Raised when the selected lines change (the view redraws them).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Hunks and lines of the shown diff can be staged, unstaged or discarded: a plain modification of a tracked
    /// file (renames, additions, deletions, binaries and conflicts: whole file only, from the file list).
    /// </summary>
    public bool CanEditLines => actions is not null
        && WorkingFile is { Entry: { IsUntracked: false, IsConflicted: false } }
        && File is { Kind: FileChangeKind.Modified, OldPath: null }
        && Diff is { IsBinary: false, IsLossy: false, Hunks.Count: > 0 };

    /// <summary>Discarding (back to the index) only applies to the unstaged side.</summary>
    public bool CanDiscardLines => CanEditLines && WorkingFile?.Side == WorkingDiffSide.Unstaged;

    public int SelectionCount => _selection.Count;

    public bool HasSelection => _selection.Count > 0;

    public bool IsStagedSide => WorkingFile?.Side == WorkingDiffSide.Staged;

    public string HunkActionLabel => IsStagedSide ? Loc.Get("Diff.Hunk.Unstage") : Loc.Get("Diff.Hunk.Stage");

    public string SelectionActionLabel => Loc.Format(IsStagedSide ? "Diff.Selection.Unstage" : "Diff.Selection.Stage", LinesText(_selection.Count));

    public string DiscardSelectionLabel => Loc.Format("Diff.Selection.Discard", LinesText(_selection.Count));

    /// <summary>
    /// Click on a changed line: toggles it; with <paramref name="extend"/> (Shift), selects every changed line from
    /// the previous click to this one.
    /// </summary>
    public void ToggleLine(DiffLineRef line, bool extend)
    {
        if (!CanEditLines || Diff is not { } diff || !IsChange(diff, line)) return;

        if (extend && _anchor is { } anchor)
        {
            var (from, to) = Compare(anchor, line) <= 0 ? (anchor, line) : (line, anchor);
            for (var h = from.Hunk; h <= to.Hunk; h++)
            {
                foreach (var changed in PatchBuilder.ChangedLines(diff, h))
                {
                    if (Compare(changed, from) >= 0 && Compare(changed, to) <= 0) _selection.Add(changed);
                }
            }
        }
        else if (!_selection.Remove(line))
        {
            _selection.Add(line);
        }

        _anchor = line;
        NotifySelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        _anchor = null;
        NotifySelection();
    }

    /// <summary>Stages (unstaged side) or unstages (staged side) a whole hunk.</summary>
    [RelayCommand]
    private Task ApplyHunkAsync(int hunk) =>
        Diff is { } diff && hunk >= 0 && hunk < diff.Hunks.Count
            ? ApplyLinesAsync([.. PatchBuilder.ChangedLines(diff, hunk)], Loc.Get("Diff.What.Hunk"))
            : Task.CompletedTask;

    /// <summary>Stages or unstages the selected lines.</summary>
    [RelayCommand]
    private Task ApplySelectionAsync() => ApplyLinesAsync([.. _selection], LinesText(_selection.Count));

    /// <summary>Discards a hunk of the unstaged side from the working tree, after confirmation.</summary>
    [RelayCommand]
    private Task DiscardHunkAsync(int hunk) =>
        Diff is { } diff && hunk >= 0 && hunk < diff.Hunks.Count
            ? DiscardLinesAsync([.. PatchBuilder.ChangedLines(diff, hunk)], Loc.Get("Diff.What.ThisHunk"))
            : Task.CompletedTask;

    /// <summary>Discards the selected lines of the unstaged side, after confirmation.</summary>
    [RelayCommand]
    private Task DiscardSelectionAsync() => DiscardLinesAsync([.. _selection], LinesText(_selection.Count));

    private async Task ApplyLinesAsync(HashSet<DiffLineRef> lines, string what)
    {
        if (!CanEditLines || actions is null || Diff is not { } diff || WorkingFile is not { } working) return;
        var unstage = working.Side == WorkingDiffSide.Staged;
        if (PatchBuilder.Build(diff, lines, reverse: unstage) is not { } patch) return;

        var title = Loc.Format(unstage ? "Diff.Unstage.Failed" : "Diff.Stage.Failed", what);
        if (await actions.RunAsync(title, ct => git.ApplyPatchAsync(repositoryPath, patch, PatchTarget.Index, unstage, ct)))
            ClearSelection();
    }

    private async Task DiscardLinesAsync(HashSet<DiffLineRef> lines, string what)
    {
        if (!CanDiscardLines || actions is null || Diff is not { } diff || File is not { } file) return;
        if (PatchBuilder.Build(diff, lines, reverse: true) is not { } patch) return;
        if (!await actions.ConfirmAsync(Loc.Get("Diff.Discard.Title"),
                Loc.Format("Diff.Discard.Message", what, file.Path), Loc.Get("Diff.Discard.Confirm"), isDestructive: true)) return;

        if (await actions.RunAsync(Loc.Format("Diff.Discard.Failed", what),
                ct => git.ApplyPatchAsync(repositoryPath, patch, PatchTarget.WorkingTree, reverse: true, ct)))
            ClearSelection();
    }

    private static string LinesText(int count) => count == 1 ? Loc.Get("Diff.Lines.One") : Loc.Format("Diff.Lines.Many", count);

    private static bool IsChange(FileDiff diff, DiffLineRef line) =>
        line.Hunk >= 0 && line.Hunk < diff.Hunks.Count
        && line.Line >= 0 && line.Line < diff.Hunks[line.Hunk].Lines.Count
        && diff.Hunks[line.Hunk].Lines[line.Line].IsChange;

    private static int Compare(DiffLineRef a, DiffLineRef b) => a.Hunk != b.Hunk ? a.Hunk.CompareTo(b.Hunk) : a.Line.CompareTo(b.Line);

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionActionLabel));
        OnPropertyChanged(nameof(DiscardSelectionLabel));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial ChangedFile? File { get; private set; }

    [ObservableProperty]
    public partial FileDiff? Diff { get; private set; }

    /// <summary>Hunk the view should scroll to; -1 when there is none.</summary>
    [ObservableProperty]
    public partial int CurrentHunk { get; private set; } = -1;

    /// <summary>Working-directory change shown, with its side; null for a file of a commit.</summary>
    public (StatusEntry Entry, WorkingDiffSide Side)? WorkingFile { get; private set; }

    public string StatusLetter => File is { } f ? ChangedFileItem.LetterFor(f.Kind) : string.Empty;

    public string PathDisplay => File switch
    {
        null => string.Empty,
        { OldPath: { } old } f => $"{old} → {f.Path}",
        { } f => f.Path,
    };

    /// <summary>"Indexé", "Non indexé" or "Non suivi" for a working-directory change; empty for a commit.</summary>
    public string SideDisplay => WorkingFile switch
    {
        null => string.Empty,
        { Entry.IsUntracked: true } => Loc.Get("Diff.Side.Untracked"),
        { Side: WorkingDiffSide.Staged } => Loc.Get("Diff.Side.Staged"),
        _ => Loc.Get("Diff.Side.Unstaged"),
    };

    // Counted from the diff once loaded: git status gives no line counts for working-directory changes.
    public string AdditionsDisplay => Diff is { } d ? $"+{d.Additions}" : File is { } f ? $"+{f.Additions}" : string.Empty;

    public string DeletionsDisplay => Diff is { } d ? $"−{d.Deletions}" : File is { } f ? $"−{f.Deletions}" : string.Empty;

    public string HunkDisplay => Diff is { Hunks.Count: > 0 } d ? Loc.Format("Diff.HunkOf", CurrentHunk + 1, d.Hunks.Count) : string.Empty;

    /// <summary>Message shown instead of hunks (binary file, pure rename, mode change).</summary>
    public string? PlaceholderMessage => Diff switch
    {
        { IsBinary: true } when IsImageDiff => null,
        { IsBinary: true } => Loc.Get("Diff.Placeholder.Binary"),
        { Hunks.Count: 0, OldPath: not null } => Loc.Get("Diff.Placeholder.Renamed"),
        { Hunks.Count: 0 } => Loc.Get("Diff.Placeholder.NoContent"),
        _ => null,
    };

    /// <summary>Opens the diff of <paramref name="file"/> in <paramref name="commit"/> against its first parent.</summary>
    public Task OpenAsync(CommitDetails commit, ChangedFile file)
    {
        GitSha? parent = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        WorkingFile = null;
        _versions = (
            parent is { } p ? FileVersion.AtCommit(p.ToString(), file.OldPath ?? file.Path) : null,
            FileVersion.AtCommit(commit.Sha.ToString(), file.Path));
        return LoadAsync(file, ct => git.GetFileDiffAsync(repositoryPath, commit.Sha, parent, file, ct), keepCurrent: false);
    }

    /// <summary>
    /// Opens the staged or unstaged part of a working-directory change. With <paramref name="refresh"/>, the diff
    /// already shown stays visible until the new one is read (the file changed on disk).
    /// </summary>
    public Task OpenWorkingAsync(StatusEntry entry, WorkingDiffSide side, bool refresh = false)
    {
        var kind = (side == WorkingDiffSide.Staged ? entry.IndexChange : entry.WorkTreeChange) ?? FileChangeKind.Modified;
        var file = new ChangedFile(entry.Path, side == WorkingDiffSide.Staged ? entry.OldPath : null, kind);
        WorkingFile = (entry, side);
        // Staged: HEAD → index; unstaged: index → disk (nothing before an untracked file).
        _versions = side == WorkingDiffSide.Staged
            ? (FileVersion.AtCommit("HEAD", entry.OldPath ?? entry.Path), FileVersion.InIndex(entry.Path))
            : (entry.IsUntracked ? null : FileVersion.InIndex(entry.Path), FileVersion.OnDisk(entry.Path));
        return LoadAsync(file, ct => git.GetWorkingFileDiffAsync(repositoryPath, entry, side, ct), keepCurrent: refresh && IsOpen);
    }

    [RelayCommand]
    private void Close()
    {
        _loading?.Cancel();
        IsOpen = false;
        IsLoading = false;
        WorkingFile = null;
        File = null;
        Diff = null;
        Sides = null;
        Images = null;
        _versions = default;
        CurrentHunk = -1;
        NotifyDisplay();
    }

    [RelayCommand]
    private void NextHunk() => MoveHunk(1);

    [RelayCommand]
    private void PreviousHunk() => MoveHunk(-1);

    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    private async Task LoadAsync(ChangedFile file, Func<CancellationToken, Task<GitResult<FileDiff>>> read, bool keepCurrent)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var loading = _loading = new CancellationTokenSource();

        File = file;
        ErrorMessage = null;
        IsOpen = true;
        if (!keepCurrent)
        {
            Diff = null;
            Sides = null;
            Images = null;
            CurrentHunk = -1;
            IsLoading = true;
        }
        NotifyDisplay();

        var result = await read(loading.Token);
        if (!ReferenceEquals(loading, _loading)) return;

        IsLoading = false;
        switch (result)
        {
            case Success<FileDiff> s:
                Diff = s.Value;
                CurrentHunk = s.Value.Hunks.Count == 0 ? -1 : Math.Clamp(CurrentHunk, 0, s.Value.Hunks.Count - 1);
                if (IsImageDiff)
                {
                    Sides = null;
                    NotifyDisplay();
                    await LoadImagesAsync(loading);
                    return;
                }
                Images = null;
                if (IsSideBySide)
                {
                    NotifyDisplay();
                    await LoadSidesAsync(loading);
                    return;
                }
                Sides = null;
                break;
            case Failure f:
                Diff = null;
                ErrorMessage = Loc.Format("Diff.ShowFailed", f.Message);
                break;
            case Canceled:
                break;
            case Conflict or AuthenticationFailed:
                Diff = null;
                ErrorMessage = Loc.Get("Diff.LoadFailed");
                break;
        }

        NotifyDisplay();
    }

    /// <summary>Reads the encoded bytes of both versions of the shown image.</summary>
    private async Task LoadImagesAsync(CancellationTokenSource loading)
    {
        var (oldVersion, newVersion) = _versions;
        var oldTask = Read(oldVersion);
        var newTask = Read(newVersion);
        var (original, modified) = (await oldTask, await newTask);
        if (!ReferenceEquals(loading, _loading)) return;

        if (original is Success<byte[]?> o && modified is Success<byte[]?> m)
        {
            Images = new DiffImages(Diff!.Path, o.Value, m.Value);
        }
        else
        {
            var error = Error(original) ?? Error(modified);
            Images = null;
            ErrorMessage = string.IsNullOrEmpty(error) ? null : Loc.Format("Diff.ImagesFailed", error);
        }
        NotifyDisplay();

        Task<GitResult<byte[]?>> Read(FileVersion? version) => version is null
            ? Task.FromResult<GitResult<byte[]?>>(new Success<byte[]?>(null))
            : git.ReadFileBytesAsync(repositoryPath, version, loading.Token);

        // Null when the bytes were read; the reason otherwise (empty when canceled: nothing to show).
        static string? Error(GitResult<byte[]?> result) => result switch
        {
            Success<byte[]?> => null,
            Failure f => f.Message,
            Canceled => string.Empty,
            Conflict or AuthenticationFailed => Loc.Get("Diff.ReadImpossible"),
        };
    }

    /// <summary>Reads the whole old and new texts of the shown file (side-by-side layout).</summary>
    private async Task LoadSidesAsync(CancellationTokenSource loading)
    {
        if (Diff is not { IsBinary: false } diff)
        {
            Sides = null;
            return;
        }

        var (oldVersion, newVersion) = _versions;
        var oldTask = Read(oldVersion);
        var newTask = Read(newVersion);
        var (original, modified) = (await oldTask, await newTask);
        if (!ReferenceEquals(loading, _loading)) return;

        if (original is not Success<string?> o || modified is not Success<string?> m)
        {
            var error = Error(original) ?? Error(modified);
            Sides = null;
            ErrorMessage = string.IsNullOrEmpty(error) ? null : Loc.Format("Diff.BothVersionsFailed", error);
            return;
        }

        Sides = new DiffSides(diff.Path, diff.OldPath ?? diff.Path, o.Value ?? string.Empty, m.Value ?? string.Empty);

        Task<GitResult<string?>> Read(FileVersion? version) => version is null
            ? Task.FromResult<GitResult<string?>>(new Success<string?>(null))
            : git.ReadFileTextAsync(repositoryPath, version, loading.Token);

        // Null when the text was read; the reason otherwise (empty when canceled: nothing to show).
        static string? Error(GitResult<string?> result) => result switch
        {
            Success<string?> => null,
            Failure f => f.Message,
            Canceled => string.Empty,
            Conflict or AuthenticationFailed => Loc.Get("Diff.ReadImpossible"),
        };
    }

    private void MoveHunk(int delta)
    {
        if (Diff is not { Hunks.Count: > 0 } diff) return;
        CurrentHunk = Math.Clamp(CurrentHunk + delta, 0, diff.Hunks.Count - 1);
        OnPropertyChanged(nameof(HunkDisplay));
    }

    private void NotifyDisplay()
    {
        OnPropertyChanged(nameof(StatusLetter));
        OnPropertyChanged(nameof(PathDisplay));
        OnPropertyChanged(nameof(SideDisplay));
        OnPropertyChanged(nameof(AdditionsDisplay));
        OnPropertyChanged(nameof(DeletionsDisplay));
        OnPropertyChanged(nameof(HunkDisplay));
        OnPropertyChanged(nameof(PlaceholderMessage));
        OnPropertyChanged(nameof(IsImageDiff));
        OnPropertyChanged(nameof(CanEditLines));
        OnPropertyChanged(nameof(CanDiscardLines));
        OnPropertyChanged(nameof(IsStagedSide));
        OnPropertyChanged(nameof(HunkActionLabel));
        // Line indexes refer to the previous diff.
        _selection.Clear();
        _anchor = null;
        NotifySelection();
    }
}
