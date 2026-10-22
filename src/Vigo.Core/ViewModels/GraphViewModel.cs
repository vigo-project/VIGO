using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Graph;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Commit graph of a repository tab: refs, then the log streamed page by page into a <see cref="CommitGraph"/>.
/// </summary>
/// <remarks>
/// The graph is read by the renderer on the UI thread, so pages are appended on the synchronization context that
/// started <see cref="LoadAsync"/> (about 0.5 ms per page of 2,000 commits); git.exe and parsing stay off that thread.
/// </remarks>
public sealed partial class GraphViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<RefBadge> NoBadges = [];

    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposed = new();
    private CommitGraph _graph = new();
    private Dictionary<GitSha, RefBadge[]> _badges = [];
    private HashSet<GitSha> _tips = [];
    private Task? _load;
    private CancellationTokenSource? _reload;
    private Task _reloading = Task.CompletedTask;
    private bool _refsLoaded;
    private bool _completed;
    private readonly Preferences? _preferences;

    /// <param name="hidden">Refs hidden from the graph (kept between sessions); null hides nothing.</param>
    public GraphViewModel(IGitService git, string repositoryPath, TimeProvider time, ILogger logger, HiddenRefs? hidden = null, Preferences? preferences = null)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _time = time;
        _logger = logger;
        _preferences = preferences;
        Hidden = hidden ?? new HiddenRefs(null, repositoryPath);
        Hidden.Changed += OnHiddenChanged;
    }

    /// <summary>Branches, tags and stashes left out of the graph (badges and commits only they reach); the left panel toggles them.</summary>
    public HiddenRefs Hidden { get; }

    // The log is read again without (or with) the commits of the ref: the displayed graph stays until the new one is ready.
    private void OnHiddenChanged(object? sender, EventArgs e)
    {
        if (!_refsLoaded) return;
        _ = ReloadAsync();
    }

    private CommitLogOptions LogOptions() => CommitLogOptions.Default with
    {
        PageSize = _preferences?.Current.GraphInitialCommits ?? UserPreferences.DefaultGraphInitialCommits,
        HiddenRefs = Hidden.ExcludedRefs(),
    };

    private PagePump? _pump;

    private bool Lazy => _preferences?.Current.GraphLazyLoad == true;

    /// <summary>Lazy loading stopped before the end of the history: more commits come when the view nears the last row.</summary>
    public bool IsPartial => _pump is { IsFinished: false };

    /// <summary>
    /// Asks for the next page when the last visible row is within 200 rows of the end of what is loaded (no-op unless
    /// the history is read lazily). Call from the UI thread.
    /// </summary>
    public void EnsureLoaded(int lastVisibleRow)
    {
        if (_pump is { IsFinished: false } pump && RowCount - lastVisibleRow <= LazyLookahead) pump.Demand(_graph.Commits.Count + 1);
    }

    private const int LazyLookahead = 200;

    /// <summary>
    /// <see cref="TrySelect"/> that reads more of a lazily loaded history until the commit shows up; false once the
    /// whole history is loaded without it. Call from the UI thread.
    /// </summary>
    public async Task<bool> TrySelectLoadingAsync(GitSha sha)
    {
        while (!TrySelect(sha))
        {
            if (_pump is not { IsFinished: false } pump || _disposed.IsCancellationRequested) return false;
            var paused = pump.NextPause();
            pump.Demand(_graph.Commits.Count + 1);
            try
            {
                await paused.WaitAsync(pump.Token);
            }
            catch (OperationCanceledException)
            {
                // Closed, or a reload replaced the graph: stop, or look again at the new one.
                if (_disposed.IsCancellationRequested) return false;
            }
        }
        return true;
    }

    /// <summary>Raised on the UI thread after rows were appended or refs were loaded.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread when <see cref="Refs"/> was replaced.</summary>
    public event EventHandler? RefsChanged;

    /// <summary>References read last (left panel); null before they are read or when reading them failed.</summary>
    public RepositoryRefs? Refs { get; private set; }

    /// <summary>Rows shown: the work-in-progress row (if any) followed by the commits loaded so far.</summary>
    [ObservableProperty]
    public partial int RowCount { get; private set; }

    /// <summary>Most commits a search keeps: a word found in a million commits would not be readable anyway.</summary>
    public const int FilterLimit = 5000;

    // The work-in-progress row is not part of a search result: asked for by the status, shown while no filter is set.
    private bool _workInProgressWanted;
    private CommitFilter? _filter;

    /// <summary>The working directory has changes: row 0 is the "// WIP" row, commits start at row 1.</summary>
    public bool HasWorkInProgress => _workInProgressWanted && _filter is null;

    /// <summary>What the graph is narrowed to (search box); null shows the whole history.</summary>
    public CommitFilter? Filter => _filter;

    public bool IsFiltered => _filter is not null;

    /// <summary>A search is being read (the previous rows stay displayed meanwhile).</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>The search found more than <see cref="FilterLimit"/> commits: only the most recent ones are shown.</summary>
    [ObservableProperty]
    public partial bool IsFilterTruncated { get; private set; }

    /// <summary>Why the last search could not run (invalid text for git…); null when it did.</summary>
    [ObservableProperty]
    public partial string? FilterError { get; private set; }

    /// <summary>Commits of the current result while a filter is set; 0 otherwise.</summary>
    public int FilterResultCount => _filter is null ? 0 : _graph.Commits.Count;

    /// <summary>Label of the work-in-progress row ("3 modifications non validées").</summary>
    public string WorkInProgressText { get; private set; } = string.Empty;

    /// <summary>The work-in-progress row shows an operation stopped on conflicts (warning color, mockup).</summary>
    public bool IsWorkInProgressWarning { get; private set; }

    /// <summary>Commit of the selected row; null when nothing or the work-in-progress row is selected.</summary>
    [ObservableProperty]
    public partial GitSha? SelectedSha { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Message shown instead of (or below) the graph when reading the repository failed.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Selected row, or -1.</summary>
    [ObservableProperty]
    public partial int SelectedRow { get; set; } = -1;

    /// <summary>Commit checked out; null for an empty repository or before the refs are loaded.</summary>
    public GitSha? Head { get; private set; }

    /// <summary>Branch checked out according to the refs read last; null for a detached HEAD.</summary>
    public string? HeadBranch { get; private set; }

    /// <summary>The log was fully read and has no commit.</summary>
    public bool IsEmpty => _completed && _graph.Commits.Count == 0;

    /// <summary>Lanes, indexed by commit row (see <see cref="ToCommitRow"/>).</summary>
    public LaneEngine Lanes => _graph.Lanes;

    /// <summary>Number of rows before the first commit (1 when the work-in-progress row is shown).</summary>
    public int CommitRowOffset => HasWorkInProgress ? 1 : 0;

    public bool IsWorkInProgressRow(int row) => HasWorkInProgress && row == 0;

    /// <summary>Row of the commit in <see cref="Lanes"/> for a displayed row.</summary>
    public int ToCommitRow(int row) => row - CommitRowOffset;

    /// <summary>Commit of a displayed row (not the work-in-progress row).</summary>
    public CommitInfo GetCommit(int row) => _graph.Commits[ToCommitRow(row)];

    /// <summary>Lane of a displayed row; the work-in-progress row sits in the lane of HEAD.</summary>
    public int GetNodeLane(int row) => IsWorkInProgressRow(row)
        ? (HeadRow is var head and >= 0 ? _graph.Lanes.GetNodeLane(ToCommitRow(head)) : 0)
        : _graph.Lanes.GetNodeLane(ToCommitRow(row));

    /// <summary>Lane of a loaded commit (its color in the graph); null when it is not loaded.</summary>
    public int? GetLane(GitSha sha) => _graph.TryGetRow(sha, out var row) ? _graph.Lanes.GetNodeLane(row) : null;

    /// <summary>Displayed row of HEAD; -1 when it is not loaded (yet) or the repository has no commit.</summary>
    public int HeadRow => Head is { } head && _graph.TryGetRow(head, out var row) ? row + CommitRowOffset : -1;

    public IReadOnlyList<RefBadge> GetBadges(GitSha sha) => _badges.TryGetValue(sha, out var badges) ? badges : NoBadges;

    public string FormatDate(DateTimeOffset when) => RelativeTime.FormatCommitDate(when, _time.GetUtcNow(), _time.LocalTimeZone);

    /// <summary>Loads refs then streams the log; later calls wait for the same load.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        (_load ??= LoadCoreAsync(SynchronizationContext.Current)).WaitAsync(cancellationToken);

    /// <summary>
    /// Refreshes when HEAD no longer matches what the graph shows (commit, amend, checkout, reset made in VIGO or
    /// elsewhere), see <see cref="RefreshAsync"/>. Does nothing before the first load has read the refs, nor while a
    /// refresh is running (the watcher started it for the same move: restarting it would read everything twice).
    /// Call from the UI thread.
    /// </summary>
    public Task SyncHeadAsync(GitSha? head, string? branch) =>
        _refsLoaded && _reloading.IsCompleted && (head != Head || branch != HeadBranch) ? RefreshAsync() : Task.CompletedTask;

    /// <summary>
    /// Reads the refs again; the history is reread (<see cref="ReloadAsync"/>) only when it may have changed: a ref
    /// points at a commit that is not in the graph, or a commit that was a tip is no longer pointed at (its commits
    /// may have become unreachable). Otherwise (checkout of an existing branch, tag or branch created on a known
    /// commit, fetch that brought nothing) only HEAD, the badges and <see cref="Refs"/> change. Call from the UI thread.
    /// </summary>
    public Task RefreshAsync() => StartReload(force: false);

    /// <summary>
    /// Reads refs and history again into a new graph, off the UI thread, then swaps it in on the UI thread: the
    /// current graph stays displayed meanwhile, and the selection follows its commit. A reload or refresh requested
    /// while one is running cancels it. Call from the UI thread.
    /// </summary>
    public Task ReloadAsync() => StartReload(force: true);

    /// <summary>
    /// Narrows the graph to the commits matching <paramref name="filter"/> (null: the whole history again), read by
    /// <c>git log</c> into a new graph that replaces the displayed one when complete. The work-in-progress row is hidden
    /// meanwhile. Call from the UI thread.
    /// </summary>
    public async Task SetFilterAsync(CommitFilter? filter)
    {
        if (_disposed.IsCancellationRequested || Equals(filter, _filter) || (filter is null && _filter is null)) return;
        // The first load shows the whole history: a search starts after it.
        if (_load is null) await LoadAsync();
        if (_disposed.IsCancellationRequested) return;

        var wasShown = HasWorkInProgress;
        _filter = filter;
        FilterError = null;
        IsSearching = true;
        if (HasWorkInProgress != wasShown)
        {
            // The rows shift by one; the selection waits for the new graph.
            OnPropertyChanged(nameof(HasWorkInProgress));
            RowCount = _graph.Commits.Count + CommitRowOffset;
            SelectedRow = -1;
        }
        OnPropertyChanged(nameof(Filter));
        OnPropertyChanged(nameof(IsFiltered));
        await StartReload(force: true);
    }

    private Task StartReload(bool force)
    {
        if (_disposed.IsCancellationRequested) return Task.CompletedTask;
        if (_load is null) return LoadAsync();

        _reload?.Cancel();
        _reload?.Dispose();
        _reload = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
        return _reloading = ReloadCoreAsync(_load, SynchronizationContext.Current, force, _reload.Token);
    }

    /// <summary>Completes once the last reload requested has been applied or abandoned (tests).</summary>
    internal Task WhenReloadedAsync() => _reloading;

    /// <summary>The loaded commit <paramref name="sha"/>; null when it is not (yet) in the graph.</summary>
    public CommitInfo? FindCommit(GitSha sha) => _graph.TryGetRow(sha, out var row) ? _graph.Commits[row] : null;

    /// <summary>Selects the row of a loaded commit; false when it is not (yet) in the graph.</summary>
    public bool TrySelect(GitSha sha)
    {
        if (!_graph.TryGetRow(sha, out var row)) return false;
        SelectedRow = row + CommitRowOffset;
        return true;
    }

    /// <summary>
    /// Selects the work-in-progress row, so that the right panel shows the working directory (an operation stopped on
    /// conflicts: banner and conflicted files); false when there is none.
    /// </summary>
    public bool SelectWorkInProgress()
    {
        if (!HasWorkInProgress) return false;
        SelectedRow = 0;
        return true;
    }

    /// <summary>
    /// Shows or hides the work-in-progress row for <paramref name="changeCount"/> uncommitted changes, or for an
    /// operation in progress (<paramref name="operationText"/>, e.g. "Fusion de main en cours · 3 conflits", shown even
    /// without changes). The selection stays on the same commit (rows shift by one); a selected work-in-progress row
    /// that disappears is unselected.
    /// </summary>
    public void SetWorkInProgress(int changeCount, string? operationText = null)
    {
        WorkInProgressText = operationText
            ?? (changeCount == 1 ? Loc.Get("Graph.Wip.One") : Loc.Format("Graph.Wip.Many", changeCount));
        IsWorkInProgressWarning = operationText is not null;
        var wanted = changeCount > 0 || operationText is not null;
        var wasShown = HasWorkInProgress;
        _workInProgressWanted = wanted;
        var show = HasWorkInProgress;
        if (show != wasShown)
        {
            var selected = SelectedRow;
            var wasWorkInProgress = IsWorkInProgressRow(selected, wasShown);
            OnPropertyChanged(nameof(HasWorkInProgress));
            RowCount = _graph.Commits.Count + CommitRowOffset;
            SelectedRow = selected < 0 || wasWorkInProgress ? -1 : selected + (show ? 1 : -1);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWorkInProgressRow(int row, bool shown) => shown && row == 0;

    partial void OnSelectedRowChanged(int value) =>
        SelectedSha = value >= 0 && value < RowCount && !IsWorkInProgressRow(value) ? GetCommit(value).Sha : null;

    /// <summary>Moves the selection by <paramref name="delta"/> rows, clamped to the loaded rows.</summary>
    public void MoveSelection(int delta)
    {
        if (RowCount == 0) return;
        SelectedRow = Math.Clamp(SelectedRow < 0 ? 0 : SelectedRow + delta, 0, RowCount - 1);
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        _pump?.Dispose();
        Hidden.Changed -= OnHiddenChanged;
        _reload?.Dispose();
        _graph.Dispose();
    }

    private async Task ReloadCoreAsync(Task initialLoad, SynchronizationContext? context, bool force, CancellationToken cancellationToken)
    {
        // Asked for now, on the UI thread: a later search replaces this reload.
        var filter = _filter;
        try
        {
            await ReloadFilteredCoreAsync(initialLoad, context, force, filter, cancellationToken);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && filter == _filter)
                await RunOnAsync(context, () => IsSearching = false);
        }
    }

    private async Task ReloadFilteredCoreAsync(Task initialLoad, SynchronizationContext? context, bool force, CommitFilter? filter, CancellationToken cancellationToken)
    {
        // The first load fills the displayed graph: let it finish before replacing it.
        await initialLoad.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (cancellationToken.IsCancellationRequested) return;

        RepositoryRefs? refs = null;
        switch (await _git.GetRefsAsync(_repositoryPath, cancellationToken))
        {
            case Success<RepositoryRefs> r:
                refs = r.Value;
                break;
            case Failure f:
                LogRefsFailed(_logger, _repositoryPath, f.Message);
                break;
            case Canceled:
                return;
            case Conflict or AuthenticationFailed:
                break;
        }

        if (!force && refs is not null)
        {
            // Decided on the UI thread, where the displayed graph lives.
            var historyKept = false;
            await RunOnAsync(context, () =>
            {
                if (cancellationToken.IsCancellationRequested || !CanKeepHistory(refs)) return;
                historyKept = true;
                ApplyRefs(refs);
                LogRefsUpdated(_logger, _repositoryPath);
                Changed?.Invoke(this, EventArgs.Empty);
            });
            if (historyKept || cancellationToken.IsCancellationRequested) return;
        }

        // Only this method touches the new graph until it is swapped in: pages are appended on the reading thread.
        var graph = new CommitGraph();
        var truncated = false;
        // Lazy reading (no search): stops once the rows already shown are read again, so the view does not jump.
        PagePump? pump = null;
        var swapped = false;
        try
        {
            if (filter is null && Lazy)
            {
                pump = new PagePump(Math.Max(LogOptions().PageSize, _graph.Commits.Count), _disposed.Token);
                // Until it is swapped in, this reader belongs to the reload; afterwards it belongs to the graph.
                var abandoned = pump;
                cancellationToken.Register(() =>
                {
                    if (!Volatile.Read(ref swapped)) abandoned.Cancel();
                });
            }
            // A search is read whole: its commits are linked to each other only once all are known.
            List<CommitInfo>? found = filter is null ? null : [];
            // A search shows the commits that match, not the stashes.
            if (filter is null && refs is not null) graph.SetStashes(StashCommits(refs));
            var options = filter is null ? LogOptions() : LogOptions() with { Filter = filter, MaxCount = FilterLimit };
            var firstPause = pump?.NextPause();
            var reading = _git.ReadLogAsync(_repositoryPath, options, async (page, _) =>
            {
                if (found is not null)
                {
                    found.AddRange(page);
                    return;
                }
                var loaded = 0;
                if (!Volatile.Read(ref swapped))
                {
                    graph.Append(page);
                    loaded = graph.Commits.Count;
                }
                else
                {
                    // The graph is displayed now: its pages join it on the UI thread.
                    await RunOnAsync(context, () =>
                    {
                        if (_disposed.IsCancellationRequested || !ReferenceEquals(_graph, graph)) return;
                        graph.Append(page);
                        loaded = graph.Commits.Count;
                        RowCount = loaded + CommitRowOffset;
                        Changed?.Invoke(this, EventArgs.Empty);
                    });
                }
                if (pump is not null && page.Count >= options.PageSize) await pump.PauseAsync(loaded);
            }, pump?.Token ?? cancellationToken);

            GitResult result = Done.Instance;
            var partial = pump is not null && await Task.WhenAny(reading, firstPause!) != reading;
            if (!partial) result = await reading;

            switch (result)
            {
                case Done:
                    break;
                case Failure f when filter is not null:
                    // git refuses an id that is unknown or ambiguous: nothing matches, and it says why.
                    LogReloadFailed(_logger, _repositoryPath, f.Message);
                    found!.Clear();
                    await RunOnAsync(context, () => FilterError = filter.Sha is not null ? null : f.Message);
                    break;
                case Failure f:
                    // The previous graph stays: still useful, and the next HEAD change retries.
                    LogReloadFailed(_logger, _repositoryPath, f.Message);
                    return;
                case Canceled or Conflict or AuthenticationFailed:
                    return;
            }

            if (found is not null)
            {
                truncated = found.Count >= FilterLimit;
                // Parents outside the result would leave lanes running to the bottom of the graph.
                var shown = found.Select(c => c.Sha).ToHashSet();
                graph.Append([.. found.Select(c => c with { Parents = [.. c.Parents.Where(shown.Contains)] })]);
            }

            await RunOnAsync(context, () =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                IsFilterTruncated = truncated;
                Swap(graph, refs, pump, partial);
                Volatile.Write(ref swapped, true);
                graph = null;
            });
            if (partial && !cancellationToken.IsCancellationRequested) _ = FinishLoadAsync(reading, pump!);
        }
        finally
        {
            graph?.Dispose();
        }
    }

    /// <summary>Replaces the displayed graph (UI thread); the selected commit stays selected if it still exists.</summary>
    private void Swap(CommitGraph graph, RepositoryRefs? refs, PagePump? pump, bool partial)
    {
        if (!ReferenceEquals(_pump, pump)) _pump?.Cancel();
        _pump = pump;
        if (!partial) pump?.Finish();
        var selectedSha = SelectedSha;
        var workInProgressSelected = IsWorkInProgressRow(SelectedRow);

        var previous = _graph;
        _graph = graph;
        previous.Dispose();
        if (refs is not null) ApplyRefs(refs);

        _completed = !partial;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsPartial));
        RowCount = _graph.Commits.Count + CommitRowOffset;
        SelectedRow = workInProgressSelected ? 0
            : selectedSha is { } sha && _graph.TryGetRow(sha, out var row) ? row + CommitRowOffset
            : -1;
        // Same row number, other commit: the row setter did not notice.
        OnSelectedRowChanged(SelectedRow);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(FilterResultCount));
        LogReloaded(_logger, _repositoryPath, _graph.Commits.Count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadCoreAsync(SynchronizationContext? context)
    {
        // Tied to the tab's lifetime, not to the first caller.
        var cancellationToken = _disposed.Token;
        IsLoading = true;
        try
        {
            switch (await _git.GetRefsAsync(_repositoryPath, cancellationToken))
            {
                case Success<RepositoryRefs> refs:
                    ApplyRefs(refs.Value);
                    _graph.SetStashes(StashCommits(refs.Value));
                    break;
                case Failure f:
                    // Badges are a bonus: the history can still be shown.
                    LogRefsFailed(_logger, _repositoryPath, f.Message);
                    break;
                case Canceled:
                    return;
                case Conflict or AuthenticationFailed:
                    break;
            }

            var options = LogOptions();
            var pump = Lazy ? new PagePump(options.PageSize, cancellationToken) : null;
            _pump = pump;
            var firstPause = pump?.NextPause();
            var reading = _git.ReadLogAsync(_repositoryPath, options, async (page, ct) =>
            {
                var loaded = 0;
                await RunOnAsync(context, () =>
                {
                    if (_disposed.IsCancellationRequested) return;
                    _graph.Append(page);
                    loaded = _graph.Commits.Count;
                    RowCount = loaded + CommitRowOffset;
                    Changed?.Invoke(this, EventArgs.Empty);
                });
                // A short page is the last one: nothing left to wait for.
                if (pump is not null && page.Count >= options.PageSize) await pump.PauseAsync(loaded);
            }, pump?.Token ?? cancellationToken);

            if (pump is not null && await Task.WhenAny(reading, firstPause!) != reading)
            {
                // Stopped after the first page: the history goes on in the background as the view asks for it.
                _ = FinishLoadAsync(reading, pump);
                return;
            }
            CompleteLoad(await reading, pump);
        }
        finally
        {
            if (!_disposed.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task FinishLoadAsync(Task<GitResult> reading, PagePump pump)
    {
        await ((Task)reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (_disposed.IsCancellationRequested || !reading.IsCompletedSuccessfully || !ReferenceEquals(_pump, pump)) return;
        CompleteLoad(reading.Result, pump);
        OnPropertyChanged(nameof(IsPartial));
        OnPropertyChanged(nameof(IsEmpty));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteLoad(GitResult result, PagePump? pump)
    {
        switch (result)
        {
            case Done:
                _completed = true;
                pump?.Finish();
                LogLoaded(_logger, _repositoryPath, RowCount);
                break;
            case Failure f:
                ErrorMessage = Loc.Format("Graph.LoadFailed", f.Message);
                break;
            case Canceled:
                return;
            case Conflict or AuthenticationFailed:
                ErrorMessage = Loc.Get("Graph.HistoryFailed");
                break;
        }
    }

    /// <summary>
    /// True when the displayed history is still exactly what <c>git log</c> would read with <paramref name="refs"/>:
    /// every tip is loaded and no former tip was dropped. Stashes are not part of the history.
    /// </summary>
    private bool CanKeepHistory(RepositoryRefs refs)
    {
        // A search result holds a few commits only: it is read again, with the search.
        if (!_completed || ErrorMessage is not null || _filter is not null) return false;
        var tips = TipsOf(refs);
        // A stash pushed or dropped changes the rows: the history is read again to draw it.
        return tips.All(tip => _graph.TryGetRow(tip, out _)) && _tips.IsSubsetOf(tips)
            && _graph.WantedStashes.SetEquals(StashCommits(refs).Select(s => s.Sha));
    }

    /// <summary>The stashes of <paramref name="refs"/> as rows of the graph: the stash message as subject, the commit it was made on as only parent.</summary>
    private IEnumerable<CommitInfo> StashCommits(RepositoryRefs refs) =>
        refs.OfKind(GitRefKind.Stash).Where(s => s.Stash is not null && !Hidden.IsHidden(s)).Select(s => new CommitInfo(
            s.Target, [s.Stash!.Parent], s.Stash.AuthorName, s.Stash.AuthorEmail, s.Stash.When,
            RefsPanelViewModel.StashSubject(s.Message) is { Length: > 0 } subject ? subject : s.Name) { IsStash = true });

    private HashSet<GitSha> TipsOf(RepositoryRefs refs)
    {
        // A hidden ref is not a tip: its commits are not read.
        var tips = refs.Refs.Where(r => r.Kind != GitRefKind.Stash && !Hidden.IsHidden(r)).Select(r => r.Target).ToHashSet();
        if (refs.Head is { } head) tips.Add(head);
        return tips;
    }

    /// <summary>Shows <paramref name="refs"/>: HEAD, badges and the left panel (UI thread).</summary>
    private void ApplyRefs(RepositoryRefs refs)
    {
        Head = refs.Head;
        HeadBranch = refs.CurrentBranch;
        _badges = RefBadge.FromRefs(refs, Hidden);
        _tips = TipsOf(refs);
        _refsLoaded = true;
        Refs = refs;
        RefsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static Task RunOnAsync(SynchronizationContext? context, Action action)
    {
        if (context is null || SynchronizationContext.Current == context)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        }, null);
        return done.Task;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the refs of {Path}: {Message}")]
    private static partial void LogRefsFailed(ILogger logger, string path, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "History of {Path} loaded: {Count} commits")]
    private static partial void LogLoaded(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "History of {Path} reloaded: {Count} commits")]
    private static partial void LogReloaded(ILogger logger, string path, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refs of {Path} updated, history kept")]
    private static partial void LogRefsUpdated(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not reload the history of {Path}: {Message}")]
    private static partial void LogReloadFailed(ILogger logger, string path, string message);
}
