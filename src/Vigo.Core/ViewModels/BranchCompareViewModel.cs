using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A file of the comparison list; the first row of a category carries its heading.</summary>
public sealed record BranchCompareRow(BranchCompareEntry Entry, string CategoryLabel, string? GroupTitle)
{
    public string Name => Entry.Path[(Entry.Path.LastIndexOf('/') + 1)..];

    /// <summary>Directory of the file, or the former path of a renamed one.</summary>
    public string Detail => Entry.OldPath is { } old ? $"{old} → {Entry.Path}" : Entry.Path.LastIndexOf('/') is var i and > 0 ? Entry.Path[..i] : string.Empty;

    public bool HasDetail => Detail.Length > 0;

    public bool HasGroupTitle => GroupTitle is not null;

    /// <summary>A diff A ↔ B exists (not for identical files).</summary>
    public bool CanOpen => Entry.Kind is not null;

    public string Icon => Entry.Category switch
    {
        BranchCompareCategory.OnlyInA => "arrow_left",
        BranchCompareCategory.OnlyInB => "arrow_right",
        BranchCompareCategory.ModifiedBothMerges => "merge",
        BranchCompareCategory.ModifiedBothConflicts => "warning",
        BranchCompareCategory.Identical => "checkmark",
        _ => "document_edit",
    };

    // One flag per colour of the badge (a theme brush cannot be picked from code under AOT).
    public bool IsOnlyOneSide => Entry.Category is BranchCompareCategory.OnlyInA or BranchCompareCategory.OnlyInB;

    public bool IsAhead => Entry.Category is BranchCompareCategory.ModifiedInA or BranchCompareCategory.ModifiedInB;

    public bool IsMergeable => Entry.Category == BranchCompareCategory.ModifiedBothMerges;

    public bool IsConflict => Entry.Category == BranchCompareCategory.ModifiedBothConflicts;

    public bool IsIdentical => Entry.Category == BranchCompareCategory.Identical;

    /// <summary>Read by the screen reader.</summary>
    public override string ToString() => $"{Entry.Path}, {CategoryLabel}";
}

/// <summary>A category filter button with its file count.</summary>
public sealed partial class BranchCompareChip(BranchCompareCategory category, string label, int count) : ObservableObject
{
    public BranchCompareCategory Category { get; } = category;

    public int Count { get; } = count;

    [ObservableProperty]
    public partial string Label { get; set; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>The A ↔ B file diff to show (the code editor, in place of the comparison).</summary>
public sealed record BranchCompareDiffRequest(GitSha A, GitSha B, ChangedFile File, GitSha MergeBase, string NameA, string NameB);

/// <summary>
/// Comparison of two branches (or tags) A and B through their merge base, shown in place of the graph: files only on one
/// side, files changed on one side (who is ahead), files changed on both (clean merge or conflict). Read only: nothing in
/// the repository changes.
/// </summary>
public sealed partial class BranchCompareViewModel : ObservableObject, IDisposable
{
    private static readonly BranchCompareCategory[] Order =
    [
        BranchCompareCategory.ModifiedBothConflicts, BranchCompareCategory.ModifiedBothMerges, BranchCompareCategory.ModifiedInA,
        BranchCompareCategory.ModifiedInB, BranchCompareCategory.OnlyInA, BranchCompareCategory.OnlyInB, BranchCompareCategory.Identical,
    ];

    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _loading;
    private BranchComparison? _comparison;
    private GitRef? _refA;
    private GitRef? _refB;

    public BranchCompareViewModel(IGitService git, string repositoryPath, GraphViewModel graph, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _lifetime = lifetime;
        PickerA = new BranchPickerViewModel(graph, null, false, reference => SetAsync(reference, _refB));
        PickerB = new BranchPickerViewModel(graph, null, false, reference => SetAsync(_refA, reference));
    }

    /// <summary>Selectors of the two sides (the branch list of the toolbar, picking sets a side instead of checking out).</summary>
    public BranchPickerViewModel PickerA { get; }

    public BranchPickerViewModel PickerB { get; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial string NameA { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NameB { get; private set; } = string.Empty;

    /// <summary>"12 files · base 1a2b3c4"; empty until a comparison is shown.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Files matching the filters, the first of each category carrying its heading (an ObservableCollection: bound as an ItemsSource).</summary>
    public ObservableCollection<BranchCompareRow> Rows { get; } = [];

    /// <summary>One filter button per category that has files.</summary>
    public ObservableCollection<BranchCompareChip> Chips { get; } = [];

    /// <summary>Nothing to list: no comparison yet, or no difference.</summary>
    [ObservableProperty]
    public partial string? EmptyMessage { get; private set; }

    public bool HasEmptyMessage => EmptyMessage is not null;

    partial void OnEmptyMessageChanged(string? value) => OnPropertyChanged(nameof(HasEmptyMessage));

    /// <summary>The dry-run merge was unavailable (old git): conflicts are not told apart.</summary>
    [ObservableProperty]
    public partial bool ConflictsUnknown { get; private set; }

    /// <summary>Text the file paths are filtered by, without regard to case.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    partial void OnFilterChanged(string value) => Rebuild();

    public event EventHandler<BranchCompareDiffRequest>? DiffRequested;

    /// <summary>Shows the comparison of <paramref name="a"/> and <paramref name="b"/> (null: the user picks it).</summary>
    public Task OpenAsync(GitRef? a, GitRef? b)
    {
        IsOpen = true;
        return SetAsync(a, b);
    }

    /// <summary>Opens with the checked-out branch as A and, when <paramref name="b"/> is null, the repository's main branch as B.</summary>
    public Task OpenFromCurrentAsync(GitRef? b = null)
    {
        var refs = _graph.Refs;
        if (b?.IsCurrent == true) b = null;
        var current = refs?.OfKind(GitRefKind.LocalBranch).FirstOrDefault(r => r.IsCurrent);
        b ??= refs?.OfKind(GitRefKind.LocalBranch).Where(r => !r.IsCurrent).FirstOrDefault(r => r.Name is "main" or "master" or "develop");
        return OpenAsync(current, b);
    }

    [RelayCommand]
    public void Close()
    {
        _loading?.Cancel();
        IsOpen = false;
        IsLoading = false;
        _comparison = null;
        Rows.Clear();
        Chips.Clear();
    }

    [RelayCommand]
    private Task SwapAsync() => SetAsync(_refB, _refA);

    /// <summary>Reads the comparison again (the branches moved: fetch, commit).</summary>
    [RelayCommand]
    private Task RefreshAsync() => SetAsync(Latest(_refA), Latest(_refB));

    // The ref as the graph now has it: its tip may have moved since the comparison was made.
    private GitRef? Latest(GitRef? reference) => reference is null ? null
        : _graph.Refs?.OfKind(reference.Kind).FirstOrDefault(r => r.CanonicalName == reference.CanonicalName) ?? reference;

    private async Task SetAsync(GitRef? a, GitRef? b)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var loading = _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _refA = a;
        _refB = b;
        NameA = a?.Name ?? string.Empty;
        NameB = b?.Name ?? string.Empty;
        _comparison = null;
        ErrorMessage = null;
        Summary = string.Empty;
        ConflictsUnknown = false;
        Chips.Clear();
        Rebuild();
        if (a is null || b is null)
        {
            IsLoading = false;
            EmptyMessage = Loc.Get("Compare.ChooseBoth");
            return;
        }

        IsLoading = true;
        EmptyMessage = null;
        var result = await _git.CompareBranchesAsync(_repositoryPath, a.Target, b.Target, loading.Token);
        if (!ReferenceEquals(loading, _loading)) return;

        IsLoading = false;
        switch (result)
        {
            case Success<BranchComparison> s:
                _comparison = s.Value;
                ConflictsUnknown = !s.Value.ConflictsKnown;
                Summary = Loc.Format("Compare.Summary", s.Value.Entries.Count, s.Value.MergeBase.ToShortString());
                BuildChips();
                Rebuild();
                break;
            case Failure f:
                ErrorMessage = Loc.Format("Compare.Failed", f.Message);
                break;
            case Canceled:
                break;
            case Conflict or AuthenticationFailed:
                ErrorMessage = Loc.Format("Compare.Failed", Loc.Get("Diff.LoadFailed"));
                break;
        }
    }

    private string LabelOf(BranchCompareCategory category) => category switch
    {
        BranchCompareCategory.OnlyInA => Loc.Format("Compare.Cat.OnlyIn", NameA),
        BranchCompareCategory.OnlyInB => Loc.Format("Compare.Cat.OnlyIn", NameB),
        BranchCompareCategory.ModifiedInA => Loc.Format("Compare.Cat.ModifiedIn", NameA),
        BranchCompareCategory.ModifiedInB => Loc.Format("Compare.Cat.ModifiedIn", NameB),
        BranchCompareCategory.ModifiedBothMerges => Loc.Get("Compare.Cat.BothMerges"),
        BranchCompareCategory.ModifiedBothConflicts => Loc.Get("Compare.Cat.BothConflicts"),
        _ => Loc.Get("Compare.Cat.Identical"),
    };

    private void BuildChips()
    {
        Chips.Clear();
        if (_comparison is null) return;
        foreach (var category in Order)
        {
            var count = _comparison.Entries.Count(e => e.Category == category);
            if (count == 0) continue;
            Chips.Add(new BranchCompareChip(category, ChipLabelOf(category), count));
        }
    }

    // Short form for the filter chips: the branch names are already in the selectors above.
    private static string ChipLabelOf(BranchCompareCategory category) => category switch
    {
        BranchCompareCategory.OnlyInA => Loc.Format("Compare.Cat.OnlyIn", Loc.Get("CompareLetterA.Text")),
        BranchCompareCategory.OnlyInB => Loc.Format("Compare.Cat.OnlyIn", Loc.Get("CompareLetterB.Text")),
        BranchCompareCategory.ModifiedInA => Loc.Format("Compare.Chip.ChangedIn", Loc.Get("CompareLetterA.Text")),
        BranchCompareCategory.ModifiedInB => Loc.Format("Compare.Chip.ChangedIn", Loc.Get("CompareLetterB.Text")),
        BranchCompareCategory.ModifiedBothMerges => Loc.Get("Compare.Chip.BothMerges"),
        BranchCompareCategory.ModifiedBothConflicts => Loc.Get("Compare.Chip.BothConflicts"),
        _ => Loc.Get("Compare.Chip.Identical"),
    };

    /// <summary>Shows only this category, or all of them again when it already is the one shown.</summary>
    [RelayCommand]
    private void ToggleChip(BranchCompareChip? chip)
    {
        if (chip is null) return;
        var select = !chip.IsSelected;
        foreach (var other in Chips) other.IsSelected = false;
        chip.IsSelected = select;
        Rebuild();
    }

    private void Rebuild()
    {
        Rows.Clear();
        if (_comparison is null) return;

        var only = Chips.FirstOrDefault(c => c.IsSelected)?.Category;
        var text = Filter.Trim();
        foreach (var category in Order)
        {
            if (only is { } wanted && wanted != category) continue;
            var matching = _comparison.Entries
                .Where(e => e.Category == category && (text.Length == 0 || e.Path.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || e.OldPath?.Contains(text, StringComparison.OrdinalIgnoreCase) == true))
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var label = LabelOf(category);
            var first = true;
            foreach (var entry in matching)
            {
                Rows.Add(new BranchCompareRow(entry, label, first ? $"{label} · {matching.Count}" : null));
                first = false;
            }
        }
        EmptyMessage = Rows.Count == 0 ? Loc.Get(_comparison.Entries.Count == 0 ? "Compare.NoDifference" : "Compare.NoMatch") : null;
    }

    /// <summary>Opens the diff A → B of a file.</summary>
    [RelayCommand]
    private void OpenFile(BranchCompareRow? row)
    {
        if (row?.Entry.ToChangedFile() is { } file && _comparison is { } comparison)
            DiffRequested?.Invoke(this, new BranchCompareDiffRequest(comparison.ShaA, comparison.ShaB, file, comparison.MergeBase, NameA, NameB));
    }

    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }
}
