using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// "Working directory" panel of a repository tab (mockup: showWip): unmerged, unstaged and staged files, kept up to
/// date by a file-system watcher; staging (J3.2) and the commit form (J3.3).
/// </summary>
/// <remarks>
/// Refreshes run on the synchronization context that called <see cref="StartAsync"/> (the UI thread); git.exe runs
/// in the background. Refresh requests arriving during a refresh are coalesced into one more refresh. The lists are
/// updated in place (insertions, removals, replaced rows) so that the panel does not flicker nor lose its scroll
/// position. User-facing texts come from <see cref="Loc"/> (ADR-075).
/// </remarks>
public sealed partial class WorkingTreeViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly RepositoryInfo _repository;
    private readonly IRepositoryWatcherFactory? _watchers;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposed = new();
    private SynchronizationContext? _context;
    private IRepositoryWatcher? _watcher;
    private bool _started;
    private Task? _refresh;
    private bool _refreshPending;
    private (string Path, WorkingDiffSide Side)? _opened;
    private readonly HashSet<string> _collapsedConflicted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedUnstaged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedStaged = new(StringComparer.Ordinal);

    public WorkingTreeViewModel(
        IGitService git,
        RepositoryInfo repository,
        IRepositoryWatcherFactory? watchers,
        ILogger logger,
        IUserNotifier? notifier = null,
        IConfirmationService? confirmations = null,
        GitProfiles? profiles = null,
        CommitMessageGenerator? commitMessages = null)
    {
        _git = git;
        _repository = repository;
        _watchers = watchers;
        _logger = logger;
        Actions = new StagingActions(notifier, confirmations, RefreshAsync, _disposed.Token);
        CommitForm = new CommitFormViewModel(git, RepositoryPath, this, notifier, _disposed.Token, profiles, commitMessages);
    }

    /// <summary>Index and working-tree operations, shared with the diff view.</summary>
    public StagingActions Actions { get; }

    /// <summary>Commit form at the bottom of the panel.</summary>
    public CommitFormViewModel CommitForm { get; }

    private string RepositoryPath => _repository.WorkingDirectory ?? _repository.GitDirectory;

    /// <summary>Absolute path of a file of the list (status paths are relative to the top-level directory).</summary>
    public string? FullPath(WorkingFileItem item) => _repository.WorkingDirectory is { } root
        ? Path.GetFullPath(Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar)))
        : null;

    /// <summary>Absolute path of a folder of the tree.</summary>
    public string? FullPath(FolderRow folder) => _repository.WorkingDirectory is { } root
        ? Path.GetFullPath(Path.Combine(root, folder.Path.Replace('/', Path.DirectorySeparatorChar)))
        : null;

    /// <summary>The files of the folder's group below it, even those hidden by a collapsed folder.</summary>
    public IReadOnlyList<WorkingFileItem> FilesIn(FolderRow folder) => FilesBelow(folder);

    /// <summary>"Rejeter toutes les modifications" is possible: changes and no conflict (resolution: J5).</summary>
    public bool CanDiscardAll => ChangeCount > 0 && !HasConflicts;

    /// <summary>The repository has a working directory (not bare).</summary>
    public bool IsAvailable => _repository.WorkingDirectory is not null && !_repository.IsBare;

    /// <summary>Last status read; null before the first refresh.</summary>
    [ObservableProperty]
    public partial WorkingTreeStatus? Status { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    // Files of each group, sorted by path (every file, even those of a collapsed folder); a row whose entry did not
    // change is kept across refreshes.
    public IReadOnlyList<WorkingFileItem> Conflicted { get; private set; } = [];

    public IReadOnlyList<WorkingFileItem> Unstaged { get; private set; } = [];

    public IReadOnlyList<WorkingFileItem> Staged { get; private set; } = [];

    // Rows shown for each group: the files, or folders and files in tree mode.
    public ObservableCollection<FileListRow> ConflictedRows { get; } = [];

    public ObservableCollection<FileListRow> UnstagedRows { get; } = [];

    public ObservableCollection<FileListRow> StagedRows { get; } = [];

    /// <summary>The files are shown as a tree of folders rather than as paths (shared layout setting).</summary>
    public bool ShowAsTree
    {
        get;
        set
        {
            if (SetProperty(ref field, value)) ArrangeFiles();
        }
    }

    /// <summary>Number of changed paths (a partially staged file counts once).</summary>
    public int ChangeCount => Status?.Entries.Count ?? 0;

    public bool HasConflicts => Conflicted.Count > 0;

    public bool IsUnstagedEmpty => Unstaged.Count == 0;

    public bool IsStagedEmpty => Staged.Count == 0;

    public string ConflictedCount => Conflicted.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string UnstagedCount => Unstaged.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string StagedCount => Staged.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>"3 fichiers · sur main", "Aucune modification · sur main", "… · HEAD détaché (a3f9c12)".</summary>
    public string Subtitle
    {
        get
        {
            var where = Status switch
            {
                null => string.Empty,
                { Branch: { } branch } => Loc.Format("WorkingTree.WhereBranch", branch),
                { Head: { } head } => Loc.Format("WorkingTree.WhereDetached", head.ToShortString()),
                _ => string.Empty,
            };
            return ChangeCount switch
            {
                _ when Status is null => string.Empty,
                0 => Loc.Format("WorkingTree.ChangesNone", where),
                1 => Loc.Format("WorkingTree.ChangesOne", where),
                var n => Loc.Format("WorkingTree.ChangesMany", n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), where),
            };
        }
    }

    /// <summary>Raised on the UI thread after a new status was applied.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>
    /// Raised on the UI thread when the watcher saw a branch, tag, stash or HEAD change (commit, fetch, checkout,
    /// tag created in a terminal…).
    /// </summary>
    public event EventHandler? RefsChanged;

    /// <summary>Raised when the user asks to see the diff of a working-directory file.</summary>
    public event EventHandler<WorkingFileItem>? FileOpened;

    /// <summary>
    /// First call: reads the status and starts watching the working directory. Later calls (the tab is shown
    /// again) refresh the status. Must be called on the UI thread.
    /// </summary>
    public Task StartAsync()
    {
        if (!IsAvailable || _disposed.IsCancellationRequested) return Task.CompletedTask;
        if (!_started)
        {
            _started = true;
            _context = SynchronizationContext.Current;
            _watcher = _watchers?.Watch(_repository);
            if (_watcher is not null)
            {
                _watcher.Changed += OnWatcherChanged;
                _watcher.RefsChanged += OnWatcherRefsChanged;
            }
        }

        return RefreshAsync();
    }

    /// <summary>Reads the status again; a request made during a refresh runs once the current one completes.</summary>
    public Task RefreshAsync()
    {
        if (_refresh is { IsCompleted: false })
        {
            _refreshPending = true;
            return _refresh;
        }

        return _refresh = RefreshLoopAsync();
    }

    /// <summary>
    /// The file shown right after <paramref name="path"/> in the list of <paramref name="side"/>, in the order of the list (tree or
    /// paths); the last one when none follows; null when the list is empty. <paramref name="path"/> may be gone from the list.
    /// </summary>
    public WorkingFileItem? NextAfter(string path, WorkingDiffSide side)
    {
        var files = side == WorkingDiffSide.Staged ? Staged : Unstaged;
        return files.Where(f => FileListLayout.Compare(f.Path, path, ShowAsTree) > 0).MinBy(f => f.Path, Comparer<string>.Create((a, b) => FileListLayout.Compare(a, b, ShowAsTree)))
            ?? files.MaxBy(f => f.Path, Comparer<string>.Create((a, b) => FileListLayout.Compare(a, b, ShowAsTree)));
    }

    /// <summary>Highlights the row whose diff is shown (null: none).</summary>
    public void MarkOpened(string? path, WorkingDiffSide side)
    {
        _opened = path is null ? null : (path, side);
        foreach (var item in Conflicted.Concat(Unstaged).Concat(Staged))
            item.IsOpen = IsOpened(item);
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        CommitForm.Dispose();
        if (_watcher is not null)
        {
            _watcher.Changed -= OnWatcherChanged;
            _watcher.RefsChanged -= OnWatcherRefsChanged;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    [RelayCommand]
    private void OpenFile(WorkingFileItem item)
    {
        // A conflicted file opens in the conflict editor (J5.4), the others in the diff.
        FileOpened?.Invoke(this, item);
    }

    /// <summary>Raised by "Historique du fichier" / "Blame" of a file's menu (J5.6).</summary>
    public event EventHandler<FileInspection>? InspectionRequested;

    /// <summary>The file is in HEAD: it has a history and a blame (not untracked, nor added or renamed in the index).</summary>
    public static bool CanInspect(WorkingFileItem item) =>
        item.Entry is { IsUntracked: false, IndexChange: not (FileChangeKind.Added or FileChangeKind.Renamed or FileChangeKind.Copied) };

    /// <summary>
    /// History or blame of a file of the working directory: the blame shows the file on disk with its uncommitted lines,
    /// or HEAD's version when the file is deleted or in conflict (no blamable text on disk).
    /// </summary>
    public void Inspect(WorkingFileItem item, FileInspectionKind kind)
    {
        if (!CanInspect(item)) return;
        var onDisk = item.Entry is { IsConflicted: false, WorkTreeChange: not FileChangeKind.Deleted, IndexChange: not FileChangeKind.Deleted };
        InspectionRequested?.Invoke(this, new FileInspection(kind, item.Path, onDisk ? null : "HEAD"));
    }

    /// <summary>Collapses or expands a folder of the tree, in the group that shows it.</summary>
    [RelayCommand]
    private void ToggleFolder(FolderRow folder)
    {
        var collapsed = StagedRows.Contains(folder) ? _collapsedStaged
            : UnstagedRows.Contains(folder) ? _collapsedUnstaged
            : _collapsedConflicted;
        if (!collapsed.Remove(folder.Path)) collapsed.Add(folder.Path);
        ArrangeFiles();
    }

    /// <summary>
    /// Row button: stages an unstaged file, unstages a staged one (both paths of a rename), marks a conflict as
    /// resolved (stages it as it is).
    /// </summary>
    [RelayCommand]
    private Task ToggleStageAsync(WorkingFileItem item) => StageOrUnstageAsync(TargetsOf(item));

    /// <summary>Folder button of the tree: stages (or unstages) every file below the folder, in its group.</summary>
    [RelayCommand]
    private Task ToggleFolderStageAsync(FolderRow folder)
    {
        var files = FilesBelow(folder);
        return files.Count == 0 ? Task.CompletedTask : StageOrUnstageAsync(files);
    }

    /// <summary>Number of files selected in the group of <paramref name="item"/>, when the item is one of them (else 1).</summary>
    public int SelectionSizeOf(WorkingFileItem item) => TargetsOf(item).Count;

    /// <summary>
    /// The files a row action applies to: the whole selection of the group when the row belongs to it, else the row alone.
    /// </summary>
    private List<WorkingFileItem> TargetsOf(WorkingFileItem item)
    {
        if (!item.IsSelected) return [item];
        var selected = ItemsOf(GroupOf(item)).Where(i => i.IsSelected).ToList();
        return selected.Count > 1 ? selected : [item];
    }

    /// <summary>All the files are in the same group: unstages staged ones (both paths of a rename), else stages / resolves.</summary>
    private async Task StageOrUnstageAsync(List<WorkingFileItem> items)
    {
        var first = items[0];
        if (first.Side == WorkingDiffSide.Staged && !first.IsConflicted)
        {
            var paths = items.SelectMany(i => i.Entry.OldPath is { } old ? new[] { old, i.Entry.Path } : [i.Entry.Path]).Distinct(StringComparer.Ordinal).ToList();
            await Actions.RunAsync(Loc.Get("WorkingTree.UnstageFailed"), ct => _git.UnstageAsync(RepositoryPath, paths, ct));
            return;
        }

        var staged = items.Select(i => i.Entry.Path).Distinct(StringComparer.Ordinal).ToList();
        await Actions.RunAsync(Loc.Get(first.IsConflicted ? "WorkingTree.ResolveFailed" : "WorkingTree.StageFailed"),
            ct => _git.StageAsync(RepositoryPath, staged, ct));
    }

    // ---- Selection (Ctrl / Maj + clic) ------------------------------------------------------------------------------

    private readonly HashSet<(FileGroup Group, string Path)> _selection = [];
    private (FileGroup Group, string Key)? _anchor;

    private static FileGroup GroupOf(WorkingFileItem item) =>
        item.IsConflicted ? FileGroup.Conflicted : item.Side == WorkingDiffSide.Staged ? FileGroup.Staged : FileGroup.Unstaged;

    private IReadOnlyList<WorkingFileItem> ItemsOf(FileGroup group) => group switch
    {
        FileGroup.Conflicted => Conflicted,
        FileGroup.Staged => Staged,
        _ => Unstaged,
    };

    private ObservableCollection<FileListRow> RowsOf(FileGroup group) => group switch
    {
        FileGroup.Conflicted => ConflictedRows,
        FileGroup.Staged => StagedRows,
        _ => UnstagedRows,
    };

    private List<WorkingFileItem> FilesBelow(FolderRow folder) =>
        [.. ItemsOf(folder.Group).Where(i => i.Path.StartsWith(folder.Path + "/", StringComparison.Ordinal))];

    private List<WorkingFileItem> FilesOf(FileListRow row) => row switch
    {
        WorkingFileItem file => [file],
        FolderRow folder => FilesBelow(folder),
        _ => [],
    };

    /// <summary>
    /// Selects the files of a row (a folder: the files below it) like a list does: a plain click replaces the selection,
    /// Ctrl adds or removes, Shift selects the rows between the last clicked one and this one. A selection stays in one group.
    /// </summary>
    public void Select(FileListRow row, FileSelectionMode mode)
    {
        var group = row is WorkingFileItem file ? GroupOf(file) : ((FolderRow)row).Group;
        if (mode != FileSelectionMode.Replace && _selection.Any(k => k.Group != group)) mode = FileSelectionMode.Replace;

        switch (mode)
        {
            case FileSelectionMode.Toggle:
            {
                var files = FilesOf(row);
                var allSelected = files.Count > 0 && files.All(f => _selection.Contains((group, f.Path)));
                foreach (var f in files)
                {
                    if (allSelected) _selection.Remove((group, f.Path));
                    else _selection.Add((group, f.Path));
                }

                _anchor = (group, row.Key);
                break;
            }

            case FileSelectionMode.Range when _anchor is { } anchor && anchor.Group == group
                && RowsOf(group).ToList() is var rows && rows.FindIndex(r => r.Key == anchor.Key) is var from and >= 0
                && rows.FindIndex(r => r.Key == row.Key) is var to and >= 0:
            {
                _selection.Clear();
                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                {
                    foreach (var f in FilesOf(rows[i])) _selection.Add((group, f.Path));
                }

                break;
            }

            default:
                _selection.Clear();
                foreach (var f in FilesOf(row)) _selection.Add((group, f.Path));
                _anchor = (group, row.Key);
                break;
        }

        ApplySelection();
    }

    /// <summary>Empties the selection.</summary>
    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        _anchor = null;
        ApplySelection();
    }

    private void ApplySelection()
    {
        foreach (var group in Enum.GetValues<FileGroup>())
        {
            foreach (var item in ItemsOf(group)) item.IsSelected = _selection.Contains((group, item.Path));
        }
    }

    /// <summary>After a refresh: forgets the files that are gone (or moved to another group, once staged) and marks the new rows.</summary>
    private void PruneSelection()
    {
        if (_selection.Count > 0)
        {
            var existing = Enum.GetValues<FileGroup>().SelectMany(g => ItemsOf(g).Select(i => (g, i.Path))).ToHashSet();
            _selection.RemoveWhere(k => !existing.Contains(k));
            if (_selection.Count == 0) _anchor = null;
        }

        ApplySelection();
    }

    [RelayCommand]
    private async Task StageAllAsync() =>
        await Actions.RunAsync(Loc.Get("WorkingTree.StageAllFailed"), ct => _git.StageAllAsync(RepositoryPath, ct));

    [RelayCommand]
    private async Task UnstageAllAsync()
    {
        var paths = StagedPaths(Status?.Entries ?? []);
        await Actions.RunAsync(Loc.Get("WorkingTree.UnstageAllFailed"), ct => _git.UnstageAsync(RepositoryPath, paths, ct));
    }

    /// <summary>
    /// Discards the unstaged changes of a file, or of the whole selection when the row belongs to it (deletes untracked
    /// files), after confirmation.
    /// </summary>
    [RelayCommand]
    private async Task DiscardAsync(WorkingFileItem item)
    {
        if (item.IsConflicted || item.Side != WorkingDiffSide.Unstaged || Actions.IsBusy) return;
        var entries = TargetsOf(item).Where(i => !i.IsConflicted && i.Side == WorkingDiffSide.Unstaged).Select(i => i.Entry).ToList();
        var message = entries.Count > 1
            ? Loc.Format("WorkingTree.DiscardMany", entries.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture))
            : Loc.Format(item.Entry.IsUntracked ? "WorkingTree.DiscardUntracked" : "WorkingTree.DiscardTracked", item.Path);
        if (!await Actions.ConfirmAsync(Loc.Get("WorkingTree.DiscardTitle"), message, Loc.Get("WorkingTree.DiscardConfirm"), isDestructive: true)) return;
        await Actions.RunAsync(Loc.Get("WorkingTree.DiscardFailed"), ct => _git.DiscardAsync(RepositoryPath, entries, ct));
    }

    /// <summary>
    /// Header button: unstages everything then discards every change, untracked files included, after confirmation.
    /// </summary>
    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        if (!CanDiscardAll || Actions.IsBusy) return;
        var message = Loc.Format("WorkingTree.DiscardAllMessage", ChangeCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
        if (!await Actions.ConfirmAsync(Loc.Get("WorkingTree.DiscardAllTitle"), message, Loc.Get("WorkingTree.DiscardAllConfirm"), isDestructive: true)) return;

        await Actions.RunAsync(Loc.Get("WorkingTree.DiscardFailed"), async ct =>
        {
            var staged = StagedPaths(Status?.Entries ?? []);
            if (staged.Count > 0 && await _git.UnstageAsync(RepositoryPath, staged, ct) is var unstage and not { Value: Done })
                return unstage;

            // Re-read: files that were only staged (added) are untracked now and must be deleted too.
            var status = await _git.GetStatusAsync(RepositoryPath, ct);
            if (status.Value is not Success<WorkingTreeStatus> s)
                return status.Value as Failure ?? (GitResult)new Failure(Loc.Get("WorkingTree.StatusUnreadable"));
            return await _git.DiscardAsync(RepositoryPath, s.Value.Entries, ct);
        });
    }

    private static List<string> StagedPaths(IEnumerable<StatusEntry> entries) =>
        [.. entries.Where(e => e.IsStaged).SelectMany(e => e.OldPath is { } old ? [old, e.Path] : new[] { e.Path })];

    private void OnWatcherChanged(object? sender, EventArgs e)
    {
        if (_context is null) _ = RefreshAsync();
        else _context.Post(_ => _ = RefreshAsync(), null);
    }

    private void OnWatcherRefsChanged(object? sender, EventArgs e)
    {
        if (_context is null) RaiseRefsChanged();
        else _context.Post(_ => RaiseRefsChanged(), null);
    }

    private void RaiseRefsChanged()
    {
        if (!_disposed.IsCancellationRequested) RefsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshLoopAsync()
    {
        do
        {
            _refreshPending = false;
            await RefreshOnceAsync();
        }
        while (_refreshPending && !_disposed.IsCancellationRequested);
    }

    private async Task RefreshOnceAsync()
    {
        var path = RepositoryPath;
        var result = await _git.GetStatusAsync(path, _disposed.Token);
        if (_disposed.IsCancellationRequested) return;

        switch (result)
        {
            case Success<WorkingTreeStatus> s:
                ErrorMessage = null;
                Apply(s.Value);
                break;
            case Failure f:
                LogStatusFailed(_logger, path, f.Message);
                ErrorMessage = Loc.Format("WorkingTree.StatusUnreadableDetail", f.Message);
                break;
            case Canceled:
                break;
            case Conflict or AuthenticationFailed:
                ErrorMessage = Loc.Get("WorkingTree.StatusUnreadable");
                break;
        }
    }

    private void Apply(WorkingTreeStatus status)
    {
        Status = status;
        var entries = status.Entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        Conflicted = Reuse(Conflicted, [.. entries.Where(e => e.IsConflicted).Select(e => new WorkingFileItem(e, WorkingDiffSide.Unstaged))]);
        Unstaged = Reuse(Unstaged, [.. entries.Where(e => e.IsUnstaged).Select(e => new WorkingFileItem(e, WorkingDiffSide.Unstaged))]);
        Staged = Reuse(Staged, [.. entries.Where(e => e.IsStaged).Select(e => new WorkingFileItem(e, WorkingDiffSide.Staged))]);
        PruneSelection();
        ArrangeFiles();

        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(IsUnstagedEmpty));
        OnPropertyChanged(nameof(IsStagedEmpty));
        OnPropertyChanged(nameof(ConflictedCount));
        OnPropertyChanged(nameof(UnstagedCount));
        OnPropertyChanged(nameof(StagedCount));
        OnPropertyChanged(nameof(CanDiscardAll));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Keeps the row of a file whose entry did not change (no flicker, no lost state); new and changed entries get
    /// the new row.
    /// </summary>
    private List<WorkingFileItem> Reuse(IReadOnlyList<WorkingFileItem> current, List<WorkingFileItem> desired)
    {
        var byPath = current.ToDictionary(c => c.Path, StringComparer.Ordinal);
        for (var i = 0; i < desired.Count; i++)
        {
            if (byPath.TryGetValue(desired[i].Path, out var kept) && kept.Entry == desired[i].Entry) desired[i] = kept;
            desired[i].IsOpen = IsOpened(desired[i]);
        }

        return desired;
    }

    private void ArrangeFiles()
    {
        FileListLayout.Sync(ConflictedRows, Arranged(Conflicted, _collapsedConflicted, FileGroup.Conflicted));
        FileListLayout.Sync(UnstagedRows, Arranged(Unstaged, _collapsedUnstaged, FileGroup.Unstaged));
        FileListLayout.Sync(StagedRows, Arranged(Staged, _collapsedStaged, FileGroup.Staged));
    }

    private List<FileListRow> Arranged(IReadOnlyList<WorkingFileItem> files, HashSet<string> collapsed, FileGroup group)
    {
        var rows = FileListLayout.Arrange(files, ShowAsTree, collapsed);
        foreach (var folder in rows.OfType<FolderRow>()) folder.Group = group;
        return rows;
    }

    private bool IsOpened(WorkingFileItem item) => _opened is { } o && o.Path == item.Path && o.Side == item.Side;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the status of {Path}: {Message}")]
    private static partial void LogStatusFailed(ILogger logger, string path, string message);
}

/// <summary>Row of a working-directory file list: one side (staged or not) of a <see cref="StatusEntry"/>.</summary>
public sealed partial class WorkingFileItem(StatusEntry entry, WorkingDiffSide side) : FileListRow
{
    public StatusEntry Entry { get; } = entry;

    public WorkingDiffSide Side { get; } = side;

    public override string Path => Entry.Path;

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public string Directory => Path.LastIndexOf('/') is var i and > 0 ? Path[..i] : string.Empty;

    public bool IsConflicted => Entry.IsConflicted;

    /// <summary>Shown on the unstaged row of a file that also has staged changes (mockup: ◐).</summary>
    public bool ShowPartiallyStaged => Side == WorkingDiffSide.Unstaged && Entry.IsPartiallyStaged;

    /// <summary>Status letter; "U" (unmerged, as in git) for a conflict so that it cannot be read as "copied".</summary>
    /// <summary>The file has a history and a blame (J5.6).</summary>
    public bool CanInspect => WorkingTreeViewModel.CanInspect(this);

    public string StatusLetter => Entry switch
    {
        { IsConflicted: true } => "U",
        _ => ChangedFileItem.LetterFor((Side == WorkingDiffSide.Staged ? Entry.IndexChange : Entry.WorkTreeChange) ?? FileChangeKind.Modified),
    };

    /// <summary>Row of the file whose diff is shown (accent pill).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    public partial bool IsOpen { get; set; }

    /// <summary>The row is part of the selection (Ctrl / Maj + clic), on which the stage buttons act together.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    public partial bool IsSelected { get; set; }

    /// <summary>Background of the row: open or selected.</summary>
    public bool IsHighlighted => IsOpen || IsSelected;

    public string Tooltip => Entry switch
    {
        { Conflict: { } conflict } => Loc.Format("WorkingTree.Tip.Conflict", Path, ConflictText(conflict)),
        { IsUntracked: true } => Loc.Format("WorkingTree.Tip.Untracked", Path),
        { OldPath: { } old } when Side == WorkingDiffSide.Staged => $"{old} → {Path}",
        _ => Path,
    };

    private static string ConflictText(ConflictKind kind) => kind switch
    {
        ConflictKind.BothModified => Loc.Get("WorkingTree.Conflict.BothModified"),
        ConflictKind.BothAdded => Loc.Get("WorkingTree.Conflict.BothAdded"),
        ConflictKind.BothDeleted => Loc.Get("WorkingTree.Conflict.BothDeleted"),
        ConflictKind.AddedByUs => Loc.Get("WorkingTree.Conflict.AddedByUs"),
        ConflictKind.AddedByThem => Loc.Get("WorkingTree.Conflict.AddedByThem"),
        ConflictKind.DeletedByUs => Loc.Get("WorkingTree.Conflict.DeletedByUs"),
        ConflictKind.DeletedByThem => Loc.Get("WorkingTree.Conflict.DeletedByThem"),
        _ => Loc.Get("WorkingTree.Conflict.Other"),
    };
}
