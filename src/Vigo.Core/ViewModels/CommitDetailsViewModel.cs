using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Right panel of a repository tab: details of the commit selected in the graph, loaded on each selection change
/// (the previous load is canceled).
/// </summary>
/// <remarks>User-facing texts come from <see cref="Loc"/> (ADR-075).</remarks>
public sealed partial class CommitDetailsViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loading;

    public CommitDetailsViewModel(IGitService git, string repositoryPath, GraphViewModel graph, TimeProvider time)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _time = time;
        _graph.PropertyChanged += OnGraphPropertyChanged;
    }

    /// <summary>A commit is selected (the panel shows it, possibly still loading).</summary>
    [ObservableProperty]
    public partial bool HasSelection { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Details of the selected commit; null while loading or on error.</summary>
    [ObservableProperty]
    public partial CommitDetails? Details { get; private set; }

    public string ShortSha => Details?.Sha.ToShortString() ?? string.Empty;

    public string FullSha => Details?.Sha.ToString() ?? string.Empty;

    public string Summary => Details?.Summary ?? string.Empty;

    public string Body => Details?.Body ?? string.Empty;

    public bool HasBody => Body.Length > 0;

    public string AuthorName => Details?.Author.Name ?? string.Empty;

    public string AuthorEmail => Details?.Author.Email ?? string.Empty;

    public string AuthorInitials => Details is null ? string.Empty : new GitIdentity(Details.Author.Name, null).Initials;

    public int AuthorColorIndex => StableColor.IndexFor(AuthorName);

    /// <summary>"lun. 28 sept. · 14:12 (UTC+2)", in the author's own time zone.</summary>
    public string AuthoredDisplay => Details is { } d ? FormatWhen(d.Author.When) : string.Empty;

    public string CommitterDisplay => Details switch
    {
        null => string.Empty,
        { } d when d.Committer.Name == d.Author.Name && d.Committer.Email == d.Author.Email => Loc.Get("CommitDetails.SameAsAuthor"),
        { } d => $"{d.Committer.Name} <{d.Committer.Email}>",
    };

    /// <summary>The commit carries a signature (valid or not).</summary>
    public bool IsSigned => Details?.Signature is { IsSigned: true };

    /// <summary>Verified signature of a trusted key.</summary>
    public bool IsSignatureGood => Details?.Signature?.Status == SignatureStatus.Good;

    /// <summary>Signature that is wrong, revoked or whose result is dubious (shown as an error).</summary>
    public bool IsSignatureBad => Details?.Signature?.Status is SignatureStatus.Bad or SignatureStatus.Revoked;

    /// <summary>Signature that could not be fully trusted (unknown key, expiry, verifier unavailable).</summary>
    public bool IsSignatureDoubtful => IsSigned && !IsSignatureGood && !IsSignatureBad;

    public string SignatureDisplay => Details?.Signature is not { IsSigned: true } s ? string.Empty : s.Status switch
    {
        SignatureStatus.Good => WithSigner(Loc.Get("CommitDetails.Sig.Good"), s),
        SignatureStatus.Bad => Loc.Get("CommitDetails.Sig.Bad"),
        SignatureStatus.UnknownValidity => WithSigner(Loc.Get("CommitDetails.Sig.Unknown"), s),
        SignatureStatus.Expired => Loc.Get("CommitDetails.Sig.Expired"),
        SignatureStatus.ExpiredKey => Loc.Get("CommitDetails.Sig.ExpiredKey"),
        SignatureStatus.Revoked => Loc.Get("CommitDetails.Sig.Revoked"),
        SignatureStatus.MissingKey => Loc.Get("CommitDetails.Sig.MissingKey"),
        _ => Loc.Get("CommitDetails.Sig.Unverifiable"),
    };

    /// <summary>Key used to sign the commit (tooltip of the signature line).</summary>
    public string SignatureTooltip => Details?.Signature is { IsSigned: true, Key.Length: > 0 } s ? Loc.Format("CommitDetails.Sig.KeyTooltip", s.Key) : string.Empty;

    private static string WithSigner(string label, CommitSignature signature) =>
        signature.Signer.Length > 0 ? Loc.Format("CommitDetails.Sig.WithSigner", label, signature.Signer) : label;

    /// <summary>Lane palette color of the commit node in the graph (badge borders).</summary>
    public int LaneColorIndex { get; private set; }

    // Observable collections rather than arrays: under Native AOT, ItemsRepeater.ItemsSource only accepts
    // collections CsWinRT can project ("Argument 'source' is not a supported vector" otherwise).
    public ObservableCollection<CommitBadge> Badges { get; } = [];

    public ObservableCollection<ParentLink> Parents { get; } = [];

    /// <summary>Changed files of the commit, sorted by path (every file, even those of a collapsed folder).</summary>
    public IReadOnlyList<ChangedFileItem> Files { get; private set; } = [];

    /// <summary>Rows of the changed files list: the files, or folders and files in tree mode.</summary>
    public ObservableCollection<FileListRow> FileRows { get; } = [];

    /// <summary>The changed files are shown as a tree of folders rather than as paths (shared layout setting).</summary>
    public bool ShowAsTree
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            ArrangeFiles();
        }
    }

    public string FilesSummary => Files.Count switch
    {
        0 => Loc.Get("CommitDetails.FilesNone"),
        1 => Loc.Get("CommitDetails.FilesOne"),
        var n => Loc.Format("CommitDetails.FilesMany", n.ToString("N0", CultureInfo.CurrentCulture)),
    };

    public string AdditionsDisplay => "+" + Files.Sum(f => f.File.Additions).ToString("N0", CultureInfo.CurrentCulture);

    public string DeletionsDisplay => "−" + Files.Sum(f => f.File.Deletions).ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The selected row is a stash (J7.16): its files get a check box, and the checked ones can be applied alone.</summary>
    public bool IsStash => Details is { } d && StashOf(d.Sha) is not null;

    private GitRef? StashOf(GitSha sha) => _graph.Refs?.OfKind(GitRefKind.Stash).FirstOrDefault(r => r.Target == sha);

    /// <summary>Files of the stash selected in the list (Ctrl and Maj extend the selection).</summary>
    public int SelectedCount => Files.Count(f => f.IsSelected);

    /// <summary>"Appliquer 3 fichiers depuis le stash": the text of the row menu.</summary>
    public string ApplySelectionText => SelectedCount == 1
        ? Loc.Get("CommitDetails.Stash.ApplyOne")
        : Loc.Format("CommitDetails.Stash.ApplyMany", SelectedCount);

    private ChangedFileItem? _anchor;

    /// <summary>Raised when the user asks to apply the checked files of a stash.</summary>
    public event EventHandler<StashFilesRequest>? StashFilesRequested;

    /// <summary>
    /// Click on a file of a stash: alone it selects the file (and its diff opens), with Ctrl it adds or removes it, with Maj it selects the
    /// range from the last clicked file (in the order of the list). Returns true when the click only changed the selection.
    /// </summary>
    public bool SelectFile(ChangedFileItem item, bool control, bool shift)
    {
        if (!IsStash) return false;
        if (shift && _anchor is { } anchor)
        {
            var rows = FileRows.OfType<ChangedFileItem>().ToList();
            var from = rows.IndexOf(anchor);
            var to = rows.IndexOf(item);
            if (from >= 0 && to >= 0)
            {
                foreach (var file in Files) file.IsSelected = false;
                for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) rows[i].IsSelected = true;
                return true;
            }
        }

        if (control)
        {
            item.IsSelected = !item.IsSelected;
            _anchor = item;
            return true;
        }

        foreach (var file in Files) file.IsSelected = file == item;
        _anchor = item;
        return false;
    }

    /// <summary>Right click: a file outside the selection becomes the selection.</summary>
    public void EnsureSelected(ChangedFileItem item)
    {
        if (!IsStash || item.IsSelected) return;
        foreach (var file in Files) file.IsSelected = file == item;
        _anchor = item;
    }

    /// <summary>"Appliquer X fichiers depuis le stash": the selected files only; the stash stays.</summary>
    public void ApplySelection()
    {
        if (Details is not { } commit || StashOf(commit.Sha) is not { } stash) return;
        var files = Files.Where(f => f.IsSelected).Select(f => f.File).ToList();
        if (files.Count > 0) StashFilesRequested?.Invoke(this, new StashFilesRequest(stash, files));
    }

    private void OnFileSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChangedFileItem.IsSelected)) return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ApplySelectionText));
    }

    /// <summary>Raised when the user asks to see the diff of a file of the current commit.</summary>
    public event EventHandler<ChangedFile>? FileOpened;

    public void Dispose()
    {
        _graph.PropertyChanged -= OnGraphPropertyChanged;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    [RelayCommand]
    private void SelectParent(ParentLink parent) => _ = _graph.TrySelectLoadingAsync(parent.Sha);

    [RelayCommand]
    private void Close() => _graph.SelectedRow = -1;

    [RelayCommand]
    private void OpenFile(ChangedFileItem item) => FileOpened?.Invoke(this, item.File);

    /// <summary>Raised by "Historique du fichier" / "Blame à ce commit" of a file's menu (J5.6).</summary>
    public event EventHandler<FileInspection>? InspectionRequested;

    /// <summary>A deleted file has no text to blame in this commit.</summary>
    public static bool CanBlame(ChangedFileItem item) => item.File.Kind != FileChangeKind.Deleted;

    /// <summary>History of the file (from HEAD, under its path in this commit), or its blame in this commit.</summary>
    public void Inspect(ChangedFileItem item, FileInspectionKind kind)
    {
        if (Details is not { } commit || (kind == FileInspectionKind.Blame && !CanBlame(item))) return;
        InspectionRequested?.Invoke(this, new FileInspection(kind, item.File.Path, commit.Sha.ToString()));
    }

    /// <summary>Collapses or expands a folder of the tree.</summary>
    [RelayCommand]
    private void ToggleFolder(FolderRow folder)
    {
        if (!_collapsed.Remove(folder.Path)) _collapsed.Add(folder.Path);
        ArrangeFiles();
    }

    private void ArrangeFiles() =>
        FileListLayout.Sync(FileRows, FileListLayout.Arrange(Files, ShowAsTree, _collapsed));

    private void OnGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The commit, not the row: rows shift when the work-in-progress row appears or disappears.
        if (e.PropertyName == nameof(GraphViewModel.SelectedSha))
            _ = LoadAsync(_graph.SelectedRow);
    }

    /// <summary>Loads the commit of a graph row (-1 or the work-in-progress row clears the panel). Internal for tests.</summary>
    internal async Task LoadAsync(int row)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
        ErrorMessage = null;
        var hadSelection = HasSelection;
        HasSelection = row >= 0 && !_graph.IsWorkInProgressRow(row);
        if (!HasSelection)
        {
            SetDetails(null);
            IsLoading = false;
            return;
        }

        // Moving from one commit to another: the previous details stay until the new ones arrive (no blank flash)
        // and the spinner only shows if the read is slow.
        if (!hadSelection) SetDetails(null);
        var loading = _loading = new CancellationTokenSource();
        LaneColorIndex = Graph.LanePalette.ColorIndexFor(_graph.GetNodeLane(row));
        IsLoading = false;
        _ = ShowSpinnerAfterDelayAsync(loading);
        var result = await _git.GetCommitAsync(_repositoryPath, _graph.GetCommit(row).Sha, loading.Token);
        if (!ReferenceEquals(loading, _loading)) return; // superseded by a newer selection

        // Also stops the pending spinner delay: a read faster than the delay must never turn the spinner on afterwards.
        loading.Cancel();
        IsLoading = false;
        switch (result)
        {
            case Success<CommitDetails> s:
                SetDetails(s.Value);
                break;
            case Failure f:
                SetDetails(null);
                ErrorMessage = Loc.Format("CommitDetails.ReadFailed", f.Message);
                break;
            case Canceled:
                SetDetails(null);
                break;
            case Conflict or AuthenticationFailed:
                SetDetails(null);
                ErrorMessage = Loc.Get("CommitDetails.ReadFailedShort");
                break;
        }
    }

    private static readonly TimeSpan SpinnerDelay = TimeSpan.FromMilliseconds(200);

    private async Task ShowSpinnerAfterDelayAsync(CancellationTokenSource loading)
    {
        try
        {
            await Task.Delay(SpinnerDelay, _time, loading.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        if (ReferenceEquals(loading, _loading)) IsLoading = true;
    }

    private void SetDetails(CommitDetails? details)
    {
        Details = details;
        Badges.Clear();
        Parents.Clear();
        if (details is not null)
        {
            foreach (var badge in _graph.GetBadges(details.Sha))
                Badges.Add(new CommitBadge(badge, LaneColorIndex));
            foreach (var parent in details.Parents)
                Parents.Add(new ParentLink(parent));
        }

        var max = details is { Files.Count: > 0 } ? Math.Max(1, details.Files.Max(f => f.Additions + f.Deletions)) : 1;
        Files = details is null
            ? []
            : [.. details.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(f => new ChangedFileItem(f, max))];
        foreach (var file in Files) file.PropertyChanged += OnFileSelectedChanged;
        _anchor = null;
        ArrangeFiles();

        // Every derived property depends on Details.
        OnPropertyChanged(string.Empty);
    }

    private string FormatWhen(DateTimeOffset when)
    {
        var date = RelativeTime.FormatCommitDate(when, _time.GetUtcNow(), _time.LocalTimeZone);
        var offset = when.Offset;
        var zone = offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? "−" : "+")}{offset.Duration():h\\:mm}".Replace(":00", "", StringComparison.Ordinal);
        return $"{date} · {when:HH:mm} ({zone})";
    }
}

/// <summary>Ref badge of the selected commit, with the lane color of its node.</summary>
public sealed record CommitBadge(RefBadge Badge, int ColorIndex)
{
    public string Label => Badge.Label;

    public RefBadgeKind Kind => Badge.Kind;
}

/// <summary>Parent of the selected commit, shown as a link.</summary>
public sealed record ParentLink(GitSha Sha)
{
    public string ShortSha => Sha.ToShortString();
}

/// <summary>Row of the changed files list: status letter and +/− bars scaled to the largest file of the commit.</summary>
public sealed partial class ChangedFileItem(ChangedFile file, int maxLines) : FileListRow
{
    /// <summary>The file is selected in the list of a stash (J7.16): the row is highlighted and the menu applies the selected files.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    private const double MaxBarWidth = 40;

    public ChangedFile File { get; } = file;

    /// <summary>A deleted file has no text to blame in this commit (J5.6).</summary>
    public bool CanBlame => CommitDetailsViewModel.CanBlame(this);

    public override string Path => File.Path;

    public string Name => File.Name;

    public string Directory => File.Directory;

    public string StatusLetter => LetterFor(File.Kind);

    /// <summary>Letter of a change kind in the file lists (A, M, D, R, C, T).</summary>
    public static string LetterFor(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => "A",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        FileChangeKind.Copied => "C",
        FileChangeKind.TypeChanged => "T",
        _ => "M",
    };

    public double AdditionsBarWidth { get; } = Math.Round(file.Additions * MaxBarWidth / maxLines);

    public double DeletionsBarWidth { get; } = Math.Round(file.Deletions * MaxBarWidth / maxLines);

    /// <summary>Tooltip: full path, previous path of a rename, line counts.</summary>
    public string Tooltip => File switch
    {
        { IsBinary: true } => Loc.Format("CommitDetails.Tip.Binary", File.Path),
        { OldPath: { } old } => $"{old} → {File.Path}  +{File.Additions} −{File.Deletions}",
        _ => $"{File.Path}  +{File.Additions} −{File.Deletions}",
    };
}

/// <summary>The files of a stash the user asked to apply alone (J7.16).</summary>
public sealed record StashFilesRequest(GitRef Stash, IReadOnlyList<ChangedFile> Files);
