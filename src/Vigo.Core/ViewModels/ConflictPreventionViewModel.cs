using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>What the check of the current branch against the default remote branch found (J7.11, ADR-078).</summary>
public enum ConflictState
{
    /// <summary>Nothing to check: no working directory, detached HEAD, operation in progress, no target, or the branch is the target.</summary>
    None,

    /// <summary>The merge would go through and no local change touches a file the target changed.</summary>
    UpToDate,

    /// <summary>The merge would go through, but uncommitted changes touch files the target changed.</summary>
    PossibleConflict,

    /// <summary>The merge would conflict.</summary>
    Conflict,

    /// <summary>The check could not run (git too old, unrelated histories…): nothing is shown.</summary>
    Unavailable,
}

/// <summary>
/// Conflict prevention (J7.11, ADR-078): the toolbar's branch button warns of a conflict <em>before</em> a merge. The
/// current branch is merged in a dry run (<c>merge-tree</c>) with the default branch of its remote: a conflict is
/// certain when it would conflict, possible when uncommitted changes touch files the target changed. The dry run reruns
/// when refs move (fetch, commit, checkout), cached by the pair of commits; a working-directory change only redoes the
/// intersection.
/// </summary>
public sealed partial class ConflictPreventionViewModel : ObservableObject, IDisposable
{
    /// <summary>Pause before a check starts: a burst of signals (checkout, fetch) costs one run.</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(500);

    private const int CacheLimit = 8;

    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly WorkingTreeViewModel _tree;
    private readonly GraphViewModel _graph;
    private readonly MergeActionsViewModel _merge;
    private readonly RemoteActionsViewModel? _remote;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pause;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<(GitSha Head, GitSha Target), Prediction> _cache = [];
    private CancellationTokenSource? _checking;
    private Prediction? _current;
    private GitSha? _currentHead;
    private string? _currentBranch;
    private (GitSha? Head, string? Branch, bool InOperation)? _cleared;

    /// <summary>What a dry run and a diff found for a pair of commits.</summary>
    private sealed record Prediction(ConflictTarget Target, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Changed);

    public ConflictPreventionViewModel(
        IGitService git, string repositoryPath, WorkingTreeViewModel tree, GraphViewModel graph, MergeActionsViewModel merge,
        RemoteActionsViewModel? remote, TimeProvider time, CancellationToken lifetime, TimeSpan? pause = null)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _tree = tree;
        _graph = graph;
        _merge = merge;
        _remote = remote;
        _time = time;
        _lifetime = lifetime;
        _pause = pause ?? DefaultPause;
        _tree.StatusChanged += OnStatusChanged;
        _tree.RefsChanged += OnRefsChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssue), nameof(IsConflict), nameof(IsPossible), nameof(IsUpToDate), nameof(Summary))]
    public partial ConflictState State { get; private set; }

    /// <summary>Branch the current one is checked against ("origin/main"); empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(RebaseText), nameof(MergeText), nameof(CanIntegrate))]
    public partial string Target { get; private set; } = string.Empty;

    /// <summary>The check is running.</summary>
    [ObservableProperty]
    public partial bool IsChecking { get; private set; }

    /// <summary>Files that would conflict (certain).</summary>
    public ObservableCollection<string> ConflictingFiles { get; } = [];

    /// <summary>Files with uncommitted changes that the target changed too (possible).</summary>
    public ObservableCollection<string> PossibleFiles { get; } = [];

    /// <summary>The icon is shown: a conflict is certain or possible.</summary>
    public bool HasIssue => State is ConflictState.Conflict or ConflictState.PossibleConflict;

    public bool IsConflict => State == ConflictState.Conflict;

    public bool IsPossible => State == ConflictState.PossibleConflict;

    public bool IsUpToDate => State == ConflictState.UpToDate;

    public bool HasConflictingFiles => ConflictingFiles.Count > 0;

    public bool HasPossibleFiles => PossibleFiles.Count > 0;

    /// <summary>"Conflit avec origin/main : 3 fichiers" (tooltip and accessible name of the icon); empty when nothing is shown.</summary>
    public string Summary => State switch
    {
        ConflictState.Conflict => Loc.Format(ConflictingFiles.Count == 1 ? "Prevention.Conflict.One" : "Prevention.Conflict.Other", Target, ConflictingFiles.Count),
        ConflictState.PossibleConflict => Loc.Format(PossibleFiles.Count == 1 ? "Prevention.Possible.One" : "Prevention.Possible.Other", Target, PossibleFiles.Count),
        ConflictState.UpToDate => Loc.Format("Prevention.UpToDate", Target),
        _ => string.Empty,
    };

    public string RebaseText => Loc.Format("Prevention.Rebase", Target);

    public string MergeText => Loc.Format("Prevention.Merge", Target);

    /// <summary>The list of files, one per line (for the clipboard).</summary>
    public string FilesText => string.Join(Environment.NewLine, ConflictingFiles.Concat(PossibleFiles.Where(p => !ConflictingFiles.Contains(p))));

    /// <summary>Rebase and merge on the target are offered: it is a branch of the graph and nothing else is in progress.</summary>
    public bool CanIntegrate => TargetRef is not null && _merge.CanStart;

    private GitRef? TargetRef => Target.Length == 0
        ? null
        : _graph.Refs?.OfKind(GitRefKind.RemoteBranch).FirstOrDefault(r => r.Name == Target);

    /// <summary>How many dry runs git was asked for (the cache answers the others); tests count them.</summary>
    internal int DryRuns { get; private set; }

    /// <summary>The user asked for the history of a file of the list.</summary>
    public event EventHandler<FileInspection>? InspectionRequested;

    /// <summary>Checks the branch now (after the pause); a check still waiting or running is dropped.</summary>
    public Task RefreshAsync() => CheckAsync();

    private void OnRefsChanged(object? sender, EventArgs e) => _ = CheckAsync();

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        // The commits are the same: only the uncommitted files changed, no git.exe needed. Anything else is a new check.
        if (_current is { } prediction && _tree.Status is { } status && status.Head == _currentHead && status.Branch == _currentBranch
            && status.Operation is null && State != ConflictState.Unavailable)
        {
            Show(prediction, status);
            return;
        }

        // Nothing to check for this HEAD and branch (no target, branch = target…): the files changing says nothing new.
        if (_cleared is { } cleared && _tree.Status is { } now && (now.Head, now.Branch, now.Operation is not null) == cleared) return;
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        _checking?.Cancel();
        _checking?.Dispose();
        var source = _checking = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var token = source.Token;
        try
        {
            if (_pause > TimeSpan.Zero) await Task.Delay(_pause, _time, token);
            IsChecking = true;
            await RunAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_checking == source) IsChecking = false;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        if (!_tree.IsAvailable || _tree.Status is not { Head: { } head, Branch: { } branch, Operation: null } status)
        {
            Clear();
            return;
        }

        var targetResult = await _git.GetConflictTargetAsync(_repositoryPath, token);
        if (token.IsCancellationRequested) return;
        if (targetResult.Value is not Success<ConflictTarget> { Value: var target } || IsBranchOfTarget(branch, target))
        {
            Clear();
            return;
        }

        if (!_cache.TryGetValue((head, target.Sha), out var prediction))
        {
            DryRuns++;
            var conflicts = await _git.PredictConflictsAsync(_repositoryPath, head, target.Sha, token);
            if (token.IsCancellationRequested) return;
            if (conflicts.Value is not Success<IReadOnlyList<string>> { Value: var conflicting })
            {
                Clear(ConflictState.Unavailable);
                return;
            }

            var changed = await _git.GetChangedFilesSinceMergeBaseAsync(_repositoryPath, head, target.Sha, token);
            if (token.IsCancellationRequested) return;
            prediction = new Prediction(target, conflicting, changed.Value is Success<IReadOnlyList<string>> { Value: var files } ? files : []);
            if (_cache.Count >= CacheLimit) _cache.Clear();
            _cache[(head, target.Sha)] = prediction;
        }

        // Read again: the status may have moved while git ran.
        var current = _tree.Status;
        if (current is null || current.Head != head || current.Branch != branch)
        {
            _ = CheckAsync();
            return;
        }
        _cleared = null;
        _current = prediction;
        _currentHead = head;
        _currentBranch = branch;
        Show(prediction, status with { Entries = current.Entries });
    }

    // "main" tracked as "origin/main": the branch is its own target.
    private static bool IsBranchOfTarget(string branch, ConflictTarget target) =>
        target.Name.IndexOf('/', StringComparison.Ordinal) is var slash and >= 0 && target.Name[(slash + 1)..] == branch;

    private void Show(Prediction prediction, WorkingTreeStatus status)
    {
        Target = prediction.Target.Name;
        Replace(ConflictingFiles, prediction.Conflicts);

        // A file with uncommitted changes (or an untracked one: the merge would refuse to overwrite it) that the target changed too.
        var local = status.Entries.Where(e => !e.IsConflicted).SelectMany(e => e.OldPath is null ? [e.Path] : new[] { e.Path, e.OldPath }).ToHashSet(StringComparer.Ordinal);
        var already = prediction.Conflicts.ToHashSet(StringComparer.Ordinal);
        Replace(PossibleFiles, prediction.Changed.Where(p => local.Contains(p) && !already.Contains(p)).OrderBy(p => p, StringComparer.Ordinal));

        State = ConflictingFiles.Count > 0 ? ConflictState.Conflict
            : PossibleFiles.Count > 0 ? ConflictState.PossibleConflict
            : ConflictState.UpToDate;
        Notify();
    }

    private void Clear(ConflictState state = ConflictState.None)
    {
        _cleared = _tree.Status is { } at ? (at.Head, at.Branch, at.Operation is not null) : null;
        _current = null;
        _currentHead = null;
        _currentBranch = null;
        Target = string.Empty;
        ConflictingFiles.Clear();
        PossibleFiles.Clear();
        State = state;
        Notify();
    }

    private static void Replace(ObservableCollection<string> collection, IEnumerable<string> items)
    {
        var next = items.ToList();
        if (collection.SequenceEqual(next)) return;
        collection.Clear();
        foreach (var item in next) collection.Add(item);
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasConflictingFiles));
        OnPropertyChanged(nameof(HasPossibleFiles));
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(CanIntegrate));
    }

    [RelayCommand]
    private void ShowHistory(string path) => InspectionRequested?.Invoke(this, new FileInspection(FileInspectionKind.History, path));

    [RelayCommand]
    private Task FetchAsync() => _remote is { CanFetch: true } remote ? remote.FetchCommand.ExecuteAsync(null) : Task.CompletedTask;

    /// <summary>Rebases the current branch on the target (the existing rebase, with its own messages and conflict handling).</summary>
    [RelayCommand]
    private Task RebaseAsync() => TargetRef is { } target && _merge.HeadMenuFor(target) is { } menu
        ? _merge.RebaseCommand.ExecuteAsync(menu.Rebase)
        : Task.CompletedTask;

    /// <summary>Merges the target into the current branch.</summary>
    [RelayCommand]
    private Task MergeAsync() => TargetRef is { } target && _merge.HeadMenuFor(target) is { } menu
        ? _merge.MergeCommand.ExecuteAsync(menu.Merge)
        : Task.CompletedTask;

    public void Dispose()
    {
        _tree.StatusChanged -= OnStatusChanged;
        _tree.RefsChanged -= OnRefsChanged;
        _checking?.Cancel();
        _checking?.Dispose();
    }
}
