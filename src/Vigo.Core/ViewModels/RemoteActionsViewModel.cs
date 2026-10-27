using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Fetch, pull and push of a repository tab (toolbar buttons, mockup "startOp"): one network operation at a time,
/// progress (phase, percentage, throughput) and cancellation, then the status and the graph are read again.
/// </summary>
/// <remarks>
/// Progress reports arrive on the thread reading git's output and are posted to the synchronization context that
/// started the operation (the UI thread).
/// </remarks>
public sealed partial class RemoteActionsViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly WorkingTreeViewModel _tree;
    private readonly GraphViewModel _graph;
    private readonly IUserNotifier? _notifier;
    private readonly IAuthenticationPrompt? _authentication;
    private readonly ILogger _logger;
    private readonly CancellationToken _lifetime;
    private readonly TimeProvider _time;
    private readonly Preferences? _preferences;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _autoFetchCts;
    private Task _autoFetch = Task.CompletedTask;
    private bool _autoFetchFailing;

    public RemoteActionsViewModel(
        IGitService git, string repositoryPath, WorkingTreeViewModel tree, GraphViewModel graph,
        IUserNotifier? notifier, ILogger logger, CancellationToken lifetime, IAuthenticationPrompt? authentication = null,
        TimeProvider? time = null, Preferences? preferences = null)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _tree = tree;
        _graph = graph;
        _notifier = notifier;
        _authentication = authentication;
        _logger = logger;
        _lifetime = lifetime;
        _time = time ?? TimeProvider.System;
        _preferences = preferences;
        _tree.StatusChanged += OnStatusChanged;
    }

    private bool AutoPrune => _preferences?.Current.AutoPrune ?? true;

    /// <summary>Start of the last fetch (manual, pull or automatic); null before the first one.</summary>
    public DateTimeOffset? LastFetchStarted { get; private set; }

    /// <summary>An automatic fetch is running (no progress card: it is silent, only the toolbar bar shows it).</summary>
    public bool IsAutoFetching => !_autoFetch.IsCompleted;

    /// <summary>Toolbar bar shown: an operation or an automatic fetch is running.</summary>
    public bool ShowsProgress => IsRunning || _autoFetchActive;

    /// <summary>The bar has nothing to measure: a measurable operation is not running (an automatic fetch never is).</summary>
    public bool IsProgressIndeterminate => IsIndeterminate || !IsRunning;

    private bool _autoFetchActive;

    private void SetAutoFetchActive(bool active)
    {
        _autoFetchActive = active;
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    /// <summary>A network operation is running (progress card shown, buttons disabled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetch), nameof(CanPull), nameof(CanPush), nameof(ShowsProgress), nameof(IsProgressIndeterminate))]
    public partial bool IsRunning { get; private set; }

    /// <summary>"Pull origin/main", "Push vers origin/main", "Fetch de tous les distants".</summary>
    [ObservableProperty]
    public partial string OperationTitle { get; private set; } = string.Empty;

    /// <summary>"Réception des objets : 42 % (123 / 456) · 4.20 MiB/s".</summary>
    [ObservableProperty]
    public partial string OperationDetail { get; private set; } = string.Empty;

    /// <summary>Overall completion, 0–100 (meaningful when <see cref="IsIndeterminate"/> is false).</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    /// <summary>No measurable phase yet (connection, counting on the server…).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    public partial bool IsIndeterminate { get; private set; } = true;

    private WorkingTreeStatus? Status => _tree.Status;

    public bool CanFetch => !IsRunning && _tree.IsAvailable;

    /// <summary>The current branch has an upstream to pull from.</summary>
    public bool CanPull => !IsRunning && Status is { Branch: not null, Upstream: not null };

    /// <summary>A branch is checked out (its upstream is created by the first push).</summary>
    public bool CanPush => !IsRunning && Status is { Branch: not null };

    private static string Hint(string text, string shortcutId) => ShortcutMap.Current.WithHint(text, shortcutId);

    /// <summary>Badge of the Pull button: the commits the upstream has that the branch lacks; empty when none.</summary>
    public string PullBadge => Status is { Upstream: not null, Behind: > 0 and var behind } ? Count(behind) : string.Empty;

    /// <summary>Badge of the Push button: the commits the branch has that its upstream lacks; empty when none.</summary>
    public string PushBadge => Status is { Upstream: not null, Ahead: > 0 and var ahead } ? Count(ahead) : string.Empty;

    private static string Count(int n) => n > 99 ? "99+" : n.ToString(CultureInfo.CurrentCulture);

    public string PullTooltip => Status switch
    {
        { Branch: null } => Loc.Get("Toolbar.Pull.Detached"),
        { Upstream: null } => Loc.Get("Toolbar.Pull.NoUpstream"),
        { Upstream: { } upstream } => Loc.Format("Toolbar.Pull.WithMenu", Hint(Loc.Format("Toolbar.Pull.From", upstream), ShortcutIds.Pull)),
        _ => Hint("Pull", ShortcutIds.Pull),
    };

    public string PushTooltip => Status switch
    {
        { Branch: null } => Loc.Get("Toolbar.Push.Detached"),
        { Upstream: null, Branch: { } branch } => Hint(Loc.Format("Toolbar.Push.Publish", branch), ShortcutIds.Push),
        { Upstream: { } upstream } => Loc.Format("Toolbar.Push.WithMenu", Hint(Loc.Format("Toolbar.Push.To", upstream), ShortcutIds.Push)),
        _ => Hint("Push", ShortcutIds.Push),
    };

    /// <summary>Completes once the graph reload requested by the last operation is applied (tests).</summary>
    internal Task WhenGraphReloadedAsync() => _graph.WhenReloadedAsync();

    /// <summary>Completes once the running automatic fetch (if any) is over (tests).</summary>
    internal Task WhenAutoFetchedAsync() => _autoFetch;

    public void Dispose()
    {
        _tree.StatusChanged -= OnStatusChanged;
        _operation?.Cancel();
        _autoFetchCts?.Cancel();
    }

    /// <summary>
    /// True when an automatic fetch should start: nothing running and no fetch for at least <paramref name="interval"/>
    /// (or none yet).
    /// </summary>
    public bool IsAutoFetchDue(TimeSpan interval) =>
        !IsRunning && !IsAutoFetching && _tree.IsAvailable && !_lifetime.IsCancellationRequested
        && (LastFetchStarted is not { } last || _time.GetUtcNow() - last >= interval);

    /// <summary>
    /// Fetches every remote silently (automatic fetch of the visible tab): no progress card, no dialog (a credential
    /// manager may not show a window), failures only logged. The status and the graph follow; a toast says so only
    /// when new commits arrived on the upstream of the current branch. A manual operation cancels it.
    /// </summary>
    public Task AutoFetchAsync()
    {
        if (IsRunning || IsAutoFetching || _lifetime.IsCancellationRequested) return _autoFetch;
        LastFetchStarted = _time.GetUtcNow();
        _autoFetch = AutoFetchCoreAsync();
        return _autoFetch;
    }

    private async Task AutoFetchCoreAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _autoFetchCts = cts;
        SetAutoFetchActive(true);
        try
        {
            var remotes = await _git.GetRemotesAsync(_repositoryPath, cts.Token);
            if (remotes is not { Value: Success<IReadOnlyList<GitRemote>> { Value.Count: > 0 } }) return;

            var before = Status;
            var result = await _git.FetchInBackgroundAsync(_repositoryPath, AutoPrune, cts.Token);
            if (cts.IsCancellationRequested) return;
            switch (result)
            {
                case Done:
                    if (_autoFetchFailing) LogAutoFetchRecovered(_logger, _repositoryPath);
                    _autoFetchFailing = false;
                    await _tree.RefreshAsync();
                    _ = _graph.RefreshAsync();
                    if (Status is { Upstream: { } upstream, Behind: var behind } && before?.Upstream == upstream && behind > before.Behind)
                    {
                        var arrived = behind - before.Behind;
                        _notifier?.Notify(NotificationSeverity.Info, Loc.Format("Remote.AutoFetch.Title", upstream),
                            Loc.Format("Remote.AutoFetch.Detail",
                                arrived == 1 ? Loc.Get("Remote.AutoFetch.ArrivedOne") : Loc.Format("Remote.AutoFetch.ArrivedMany", arrived), Commits(behind)));
                    }
                    break;
                case Failure or AuthenticationFailed or Conflict:
                    // Offline, refused credentials…: logged once until it works again (one attempt per minute).
                    if (!_autoFetchFailing) LogAutoFetchFailed(_logger, _repositoryPath, Describe(result));
                    _autoFetchFailing = true;
                    break;
                case Canceled:
                    break;
            }
        }
        finally
        {
            _autoFetchCts = null;
            SetAutoFetchActive(false);
        }
    }

    private static string Describe(GitResult result) => result switch
    {
        Failure f => f.Message,
        AuthenticationFailed a => "authentication: " + a.Message,
        Conflict c => c.Operation,
        Done => "done",
        Canceled => "canceled",
    };

    /// <summary>Stops an automatic fetch before a manual operation (two git processes would contend for the refs).</summary>
    private async Task StopAutoFetchAsync()
    {
        if (!IsAutoFetching) return;
        _autoFetchCts?.Cancel();
        await _autoFetch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    /// <summary>Called after a successful pull (J7.26: keeps the submodules up to date).</summary>
    public Func<Task>? HeadMoved { get; set; }

    /// <summary>A fetch asked by the user (toolbar, palette, badge) finished successfully.</summary>
    public event EventHandler? FetchCompleted;

    [RelayCommand(CanExecute = nameof(CanFetch))]
    private Task FetchAsync() => RunAsync(
        Loc.Get("Remote.Fetch.AllTitle"), "git fetch --all", Loc.Get("Remote.Fetch.Failed"),
        (progress, ct) => _git.FetchAsync(_repositoryPath, progress, AutoPrune, ct),
        status => status switch
        {
            { Upstream: { } upstream, Behind: > 0 and var n } => (Loc.Get("Remote.Fetch.Done"), Loc.Format("Remote.Fetch.Behind", Commits(n), upstream)),
            { Upstream: { } upstream } => (Loc.Get("Remote.Fetch.Done"), Loc.Format("Remote.Fetch.UpToDate", upstream)),
            _ => (Loc.Get("Remote.Fetch.Done"), Loc.Get("Remote.Fetch.AllDone")),
        }, fetches: true);

    /// <summary>
    /// Fetches the remote behind a branch badge of the graph (double click): the remote of a remote branch, the remote of
    /// the upstream of a local one. False when the reference has none or another operation is running.
    /// </summary>
    public bool TryFetchRemoteOf(GitRef reference)
    {
        var remote = reference.Kind switch
        {
            GitRefKind.RemoteBranch => reference.Remote,
            GitRefKind.LocalBranch when reference.Upstream is { } upstream =>
                _graph.Refs?.OfKind(GitRefKind.RemoteBranch).FirstOrDefault(r => r.Name == upstream)?.Remote,
            _ => null,
        };
        if (remote is null || !CanFetch) return false;
        _ = FetchRemoteAsync(remote, AutoPrune);
        return true;
    }

    /// <summary>Fetches <paramref name="remote"/> (with <c>--prune</c> when asked); does nothing while another operation is running.</summary>
    public Task FetchRemoteAsync(string remote, bool prune)
    {
        if (!CanFetch) return Task.CompletedTask;

        return RunAsync(
            Loc.Format("Remote.Fetch.RemoteTitle", remote), "git fetch " + remote, Loc.Get("Remote.Fetch.Failed"),
            (progress, ct) => _git.FetchRemoteAsync(_repositoryPath, remote, progress, prune, ct),
            status => status switch
            {
                { Upstream: { } upstream, Behind: > 0 and var n } => (Loc.Get("Remote.Fetch.Done"), Loc.Format("Remote.Fetch.Behind", Commits(n), upstream)),
                _ => (Loc.Get("Remote.Fetch.Done"), Loc.Format("Remote.Fetch.RemoteUpToDate", remote)),
            }, fetches: true);
    }

    [RelayCommand(CanExecute = nameof(CanPull))]
    private Task PullAsync() => PullCoreAsync(PullMode.Default);

    [RelayCommand(CanExecute = nameof(CanPull))]
    private Task PullMergeAsync() => PullCoreAsync(PullMode.Merge);

    [RelayCommand(CanExecute = nameof(CanPull))]
    private Task PullRebaseAsync() => PullCoreAsync(PullMode.Rebase);

    [RelayCommand(CanExecute = nameof(CanPull))]
    private Task PullFastForwardAsync() => PullCoreAsync(PullMode.FastForwardOnly);

    [RelayCommand(CanExecute = nameof(CanPush))]
    private Task PushAsync() => PushCoreAsync(force: false);

    /// <summary>Push with <c>--force-with-lease</c>, after confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanPush))]
    private async Task ForcePushAsync()
    {
        if (!CanPush) return;
        var target = Status?.Upstream ?? Loc.Get("Remote.Force.TargetFallback");
        var message = Loc.Format("Remote.Force.Message", target);
        if (!await _tree.Actions.ConfirmAsync(Loc.Get("Remote.Force.Title"), message, Loc.Get("Remote.Force.Confirm"), isDestructive: true)) return;
        await PushCoreAsync(force: true);
    }

    /// <summary>
    /// Pushes or deletes one branch or tag of a remote (left panel menus), with the progress card, the authentication
    /// dialog and the toasts of the other network operations. False when another one is running or it failed.
    /// </summary>
    public async Task<bool> PushRefAsync(RefPush push, string title, string failureTitle, string doneTitle, string doneMessage)
    {
        var succeeded = false;
        await RunAsync(title, push.CommandLine, failureTitle,
            async (progress, ct) =>
            {
                var result = await _git.PushRefAsync(_repositoryPath, push, progress, ct);
                succeeded = result.Value is Done;
                return result;
            },
            _ => (doneTitle, doneMessage));
        return succeeded;
    }

    /// <summary>Kills the running git process; refs already written by git stay as they are.</summary>
    /// <summary>Off once a pull started merging: git is no longer killed there (it would leave index.lock and a half-updated tree).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; private set; } = true;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _operation?.Cancel();

    private Task PullCoreAsync(PullMode mode)
    {
        var upstream = Status?.Upstream ?? Loc.Get("Remote.Pull.UpstreamFallback");
        var (how, option) = mode switch
        {
            PullMode.Merge => (Loc.Get("Remote.Pull.HowMerge"), " --no-rebase"),
            PullMode.Rebase => (Loc.Get("Remote.Pull.HowRebase"), " --rebase"),
            PullMode.FastForwardOnly => (Loc.Get("Remote.Pull.HowFastForward"), " --ff-only"),
            _ => (string.Empty, string.Empty),
        };
        return RunAsync(Loc.Format("Remote.Pull.Title", upstream, how), "git pull" + option, Loc.Get("Remote.Pull.Failed"),
            (progress, ct) => _git.PullAsync(_repositoryPath, mode, progress, ct),
            status => (Loc.Get("Remote.Pull.Done"), status is { Branch: { } b, Upstream: { } u, Behind: 0 }
                ? Loc.Format("Remote.Pull.UpToDate", b, u)
                : Loc.Get("Remote.Pull.Updated")), fetches: true,
            undo: new UndoStep(Loc.Format("Remote.Pull.Title", upstream, how), IgnoreNewTags: true), movesHead: true);
    }

    /// <summary>
    /// The remote refused a plain push because history was rewritten (amend, rebase): offers to push again
    /// with <c>--force-with-lease</c> instead of only reporting the error. True when the dialog took over the failure.
    /// </summary>
    private async Task<bool> OfferForcePushAsync(Failure failure)
    {
        if (failure.Reason != FailureReason.PushNeedsForce) return false;
        var target = Status?.Upstream ?? Loc.Get("Remote.Force.TargetFallback");
        var message = Loc.Format("Remote.Force.RejectedMessage", target);
        if (!await _tree.Actions.ConfirmAsync(Loc.Get("Remote.Force.RejectedTitle"), message, Loc.Get("Remote.Force.Confirm"), isDestructive: true))
        {
            _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Remote.Push.Failed"), failure.Message);
            return true;
        }
        await PushCoreAsync(force: true);
        return true;
    }

    private Task PushCoreAsync(bool force)
    {
        var before = Status;
        var title = before?.Upstream is { } upstream ? Loc.Format("Remote.Push.Title", upstream) : Loc.Format("Remote.Push.PublishTitle", before?.Branch);
        return RunAsync(force ? Loc.Format("Remote.Push.Forced", title) : title, force ? "git push --force-with-lease" : "git push",
            force ? Loc.Get("Remote.Push.ForcedFailed") : Loc.Get("Remote.Push.Failed"),
            (progress, ct) => _git.PushAsync(_repositoryPath, new PushRequest(force), progress, ct),
            status => (force ? Loc.Get("Remote.Push.ForcedDone") : Loc.Get("Remote.Push.Done"), (before, status) switch
            {
                ({ Upstream: null }, { Branch: { } b, Upstream: { } u }) => Loc.Format("Remote.Push.Published", b, u),
                ({ Ahead: 1 }, { Upstream: { } u }) => Loc.Format("Remote.Push.PushedOne", u),
                ({ Ahead: > 0 and var n }, { Upstream: { } u }) => Loc.Format("Remote.Push.PushedMany", n, u),
                (_, { Upstream: { } u }) => Loc.Format("Remote.Push.UpToDate", u),
                _ => Loc.Get("Remote.Push.BranchPushed"),
            }),
            onFailure: force ? null : OfferForcePushAsync);
    }

    private static string Commits(int n) => n == 1 ? Loc.Get("Common.Commits.One") : Loc.Format("Common.Commits.Many", n);

    /// <summary>
    /// Runs one network operation with progress and cancellation, then rereads the status and the graph (also after
    /// a failure or a cancellation: git may have updated some refs) and reports the outcome as a toast. <paramref name="fetches"/>:
    /// the operation fetches (fetch, pull), so the automatic fetch waits a whole interval again.
    /// </summary>
    private async Task RunAsync(
        string title,
        string command,
        string failureTitle,
        Func<IProgress<GitProgress>, CancellationToken, Task<GitResult>> operation,
        Func<WorkingTreeStatus?, (string Title, string Message)> success,
        bool fetches = false,
        UndoStep? undo = null,
        Func<Failure, Task<bool>>? onFailure = null,
        bool movesHead = false)
    {
        if (IsRunning || _lifetime.IsCancellationRequested) return;

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _operation = operationCts;
        OperationTitle = title;
        OperationDetail = Loc.Get("Remote.Progress.Connecting");
        ProgressValue = 0;
        CanCancel = true;
        IsIndeterminate = true;
        IsRunning = true;
        NotifyCommands();
        var recording = undo is not null && _tree.Actions.Undo is { } history ? await history.BeginAsync(undo) : null;

        GitResult result;
        var context = SynchronizationContext.Current;
        try
        {
            await StopAutoFetchAsync();
            if (fetches) LastFetchStarted = _time.GetUtcNow();
            for (var attempt = 1; ; attempt++)
            {
                result = await operation(new ContextProgress(context, OnProgress), operationCts.Token);
                // Refused: the dialog offers ways out; each one retries the operation, until it passes or is canceled.
                if (result.Value is not AuthenticationFailed refused || _authentication is null || attempt > MaxAuthenticationAttempts) break;
                OperationDetail = Loc.Get("Remote.Progress.AuthRequired");
                if (!await ResolveAuthenticationAsync(refused, command, operationCts.Token)) break;
                OperationDetail = Loc.Get("Remote.Progress.Retrying");
                IsIndeterminate = true;
            }
        }
        finally
        {
            _operation = null;
            IsRunning = false;
        }

        if (_lifetime.IsCancellationRequested) return;
        await _tree.RefreshAsync();
        if (recording is not null) await _tree.Actions.Undo!.EndAsync(recording);
        // Remote-tracking branches moved even when HEAD did not (fetch, push): the badges need the new refs (the
        // history is reread only if new commits arrived).
        _ = _graph.RefreshAsync();
        NotifyCommands();
        // A manual fetch also reads the pull requests and issues of the hosting service again.
        if (fetches && result is Done) FetchCompleted?.Invoke(this, EventArgs.Empty);

        switch (result)
        {
            case Done:
                var (doneTitle, doneMessage) = success(Status);
                _notifier?.Notify(NotificationSeverity.Success, doneTitle, doneMessage);
                if (movesHead && HeadMoved is { } headMoved) await headMoved();
                break;
            case Failure f:
                LogOperationFailed(_logger, title, f.Details ?? f.Message);
                // The handler (e.g. the force-push dialog) may take over the failure instead of the error toast.
                if (onFailure is null || !await onFailure(f)) _notifier?.Notify(NotificationSeverity.Error, failureTitle, f.Message);
                break;
            case Conflict c:
                _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Remote.Conflict.Title"),
                    Loc.Format("Remote.Conflict.Detail",
                        c.Paths.Count == 1 ? Loc.Get("Common.FileConflicts.One") : Loc.Format("Common.FileConflicts.Many", c.Paths.Count),
                        string.Join(", ", c.Paths.Take(3)) + (c.Paths.Count > 3 ? "…" : string.Empty)));
                break;
            case AuthenticationFailed a:
                // Dialog canceled (or unavailable): a toast keeps a trace of the refusal.
                LogOperationFailed(_logger, title, a.Details ?? a.Message);
                _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Remote.Auth.Failed"), a.Remote is { } remote ? Loc.Format("Remote.Auth.FailedDetail", remote, a.Message) : a.Message);
                break;
            case Canceled:
                _notifier?.Notify(NotificationSeverity.Info, Loc.Get("Remote.Canceled.Title"), Loc.Format("Remote.Canceled.Detail", title));
                break;
        }
    }

    /// <summary>Dialogs shown in a row for one operation before giving up (a helper that keeps failing…).</summary>
    private const int MaxAuthenticationAttempts = 5;

    /// <summary>
    /// Shows the authentication dialog for a refused operation and applies the choice (store the token, switch the
    /// remote URL). True when the operation should be tried again.
    /// </summary>
    private async Task<bool> ResolveAuthenticationAsync(AuthenticationFailed refused, string command, CancellationToken cancellationToken)
    {
        var problem = await DescribeAsync(refused, command, cancellationToken);
        LogAuthenticationRefused(_logger, command, problem.Cause, problem.Url?.HostAndPort);
        var choice = await _authentication!.ResolveAsync(problem, cancellationToken);
        if (choice is not { } chosen || cancellationToken.IsCancellationRequested) return false;

        switch (chosen)
        {
            case RetryAuthentication:
                return true;
            case UseAccessToken token when problem.Url is { } url:
                var stored = await _git.StoreCredentialAsync(_repositoryPath, url, token.UserName, token.Token, cancellationToken);
                if (stored.Value is Failure f)
                {
                    _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Remote.Token.NotStored"), f.Message);
                    return false;
                }
                return stored.Value is Done;
            case UseAccessToken:
                return false;
            case SwitchRemoteUrl switchUrl:
                var changed = await _git.SetRemoteUrlAsync(_repositoryPath, switchUrl.RemoteName, switchUrl.NewUrl, cancellationToken);
                if (changed.Value is Failure failure)
                {
                    _notifier?.Notify(NotificationSeverity.Error, Loc.Get("Remote.Url.Unchanged"), failure.Message);
                    return false;
                }
                _notifier?.Notify(NotificationSeverity.Info, Loc.Format("Remote.Url.Changed", switchUrl.RemoteName), Loc.Format("Remote.Url.ChangedDetail", switchUrl.NewUrl));
                return changed.Value is Done;
        }
        return false;
    }

    /// <summary>
    /// Completes what git said with the remote (name and URL: <c>fetch --all</c>, <c>pull</c> and <c>push</c> do not
    /// always name it) and the credential helper.
    /// </summary>
    private async Task<AuthenticationProblem> DescribeAsync(AuthenticationFailed refused, string command, CancellationToken cancellationToken)
    {
        var remotes = (await _git.GetRemotesAsync(_repositoryPath, cancellationToken)).Value is Success<IReadOnlyList<GitRemote>> r ? r.Value : [];
        var failedUrl = RemoteUrl.TryParse(refused.Url) is { Protocol: not RemoteProtocol.Local } parsed ? parsed : null;
        var upstreamRemote = Status?.Upstream is { } upstream ? remotes.FirstOrDefault(rm => upstream.StartsWith(rm.Name + "/", StringComparison.Ordinal)) : null;

        var remote = remotes.FirstOrDefault(rm => rm.Name == refused.Remote)
            ?? (failedUrl is null ? null : remotes.FirstOrDefault(rm => RemoteUrl.TryParse(rm.PushUrl ?? rm.FetchUrl) is { } u && u.IsSameRepository(failedUrl)
                                                               || RemoteUrl.TryParse(rm.FetchUrl) is { } f && f.IsSameRepository(failedUrl)))
            ?? upstreamRemote
            ?? (remotes.Count == 1 ? remotes[0] : null);

        // git's URL may lack the path (SSH: "git@github.com"): prefer the remote's own when it is the same host.
        var url = remote is not null && RemoteUrl.TryParse(remote.FetchUrl) is { } remoteUrl && (failedUrl is null || failedUrl.DisplayPath.Length == 0)
            ? remoteUrl
            : failedUrl ?? (remote is null ? null : RemoteUrl.TryParse(remote.FetchUrl));
        var helper = (await _git.GetConfigValueAsync(_repositoryPath, "credential.helper", cancellationToken)).Value is Success<string?> h ? h.Value : null;
        var details = refused.Details ?? refused.Message;
        return new AuthenticationProblem(command, remote?.Name ?? refused.Remote, url, AuthenticationProblem.CauseOf(details), refused.Message, details, helper);
    }

    partial void OnIsRunningChanged(bool value) => NotifyCommands();

    private void OnProgress(GitProgress progress)
    {
        if (!IsRunning) return;
        if (progress.Phase == GitProgressPhase.Merging)
        {
            CanCancel = false;
            IsIndeterminate = true;
            OperationDetail = Loc.Get("Remote.Progress.Merging");
            return;
        }
        OperationDetail = Describe(progress);
        if (progress.Fraction is { } fraction)
        {
            IsIndeterminate = false;
            ProgressValue = Math.Round(fraction * 100, 1);
        }
    }

    /// <summary>"Réception des objets : 42 % (123 / 456) · 4.20 MiB/s", "Serveur : comptage des objets 100 %".</summary>
    internal static string Describe(GitProgress progress)
    {
        var phase = progress.Phase switch
        {
            GitProgressPhase.Enumerating => Loc.Get("Remote.Phase.Enumerating"),
            GitProgressPhase.Counting => Loc.Get("Remote.Phase.Counting"),
            GitProgressPhase.Compressing => Loc.Get("Remote.Phase.Compressing"),
            GitProgressPhase.Receiving => Loc.Get("Remote.Phase.Receiving"),
            GitProgressPhase.Unpacking => Loc.Get("Remote.Phase.Unpacking"),
            GitProgressPhase.Resolving => Loc.Get("Remote.Phase.Resolving"),
            GitProgressPhase.Writing => Loc.Get("Remote.Phase.Writing"),
            GitProgressPhase.CheckingOut => Loc.Get("Remote.Phase.CheckingOut"),
            _ => null,
        };
        if (phase is null)
        {
            return progress.Text.StartsWith("Fetching ", StringComparison.Ordinal)
                ? Loc.Format("Remote.Progress.Fetching", progress.Text[9..])
                : progress.Text;
        }

        var text = progress.IsRemote ? Loc.Format("Remote.Progress.Server", $"{char.ToLowerInvariant(phase[0])}{phase[1..]}") : phase;
        if (progress.Percent is { } percent)
        {
            text += Loc.Format("Remote.Progress.Percent", percent);
            if (progress is { Current: { } current, Total: { } total })
                text += Loc.Format("Remote.Progress.Counts", current, total);
        }
        if (progress.Throughput is { } rate) text += Loc.Format("Remote.Progress.Rate", rate);
        return text;
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PullTooltip));
        OnPropertyChanged(nameof(PushTooltip));
        OnPropertyChanged(nameof(PullBadge));
        OnPropertyChanged(nameof(PushBadge));
        OnPropertyChanged(nameof(CanFetch));
        OnPropertyChanged(nameof(CanPull));
        OnPropertyChanged(nameof(CanPush));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        FetchCommand.NotifyCanExecuteChanged();
        PullCommand.NotifyCanExecuteChanged();
        PullMergeCommand.NotifyCanExecuteChanged();
        PullRebaseCommand.NotifyCanExecuteChanged();
        PullFastForwardCommand.NotifyCanExecuteChanged();
        PushCommand.NotifyCanExecuteChanged();
        ForcePushCommand.NotifyCanExecuteChanged();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Operation} failed: {Message}")]
    private static partial void LogOperationFailed(ILogger logger, string operation, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic fetch of {Repository} failed (not logged again until it works): {Message}")]
    private static partial void LogAutoFetchFailed(ILogger logger, string repository, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic fetch of {Repository} works again")]
    private static partial void LogAutoFetchRecovered(ILogger logger, string repository);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Command} refused by {Host}: {Cause}, asking the user")]
    private static partial void LogAuthenticationRefused(ILogger logger, string command, AuthenticationCause cause, string? host);
}
