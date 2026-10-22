using CommunityToolkit.Mvvm.ComponentModel;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Runs the index / working-tree operations of a repository tab (stage, unstage, discard, partial patches) one at a
/// time, reports failures as toasts and rereads the status after each one. Shared by the working directory panel
/// and the diff view.
/// </summary>
public sealed partial class StagingActions(
    IUserNotifier? notifier, IConfirmationService? confirmations, Func<Task> refresh, CancellationToken lifetime) : ObservableObject
{
    private readonly IConfirmationService _confirmations = confirmations ?? RefuseConfirmations.Instance;

    /// <summary>An operation is running: further requests are ignored until it completes.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Undo / Redo of the tab: operations run with an <see cref="UndoStep"/> are recorded there (J5.2).</summary>
    public UndoHistory? Undo { get; set; }

    /// <summary>Asks before an irreversible action.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false) =>
        isDestructive
            ? (await _confirmations.ConfirmAsync(new ConfirmationRequest(title, message, confirmText, IsDestructive: true), lifetime)).Confirmed
            : await _confirmations.ConfirmAsync(title, message, confirmText, lifetime);

    /// <summary>Asks before an irreversible action that comes with an option (check box).</summary>
    public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request) => _confirmations.ConfirmAsync(request, lifetime);

    /// <summary>
    /// Runs <paramref name="operation"/> unless another one is running, then refreshes the status (also after a
    /// failure: part of the operation may have been applied). Returns true on success.
    /// </summary>
    public async Task<bool> RunAsync(string failureTitle, Func<CancellationToken, Task<GitResult>> operation, UndoStep? undo = null)
    {
        if (IsBusy || lifetime.IsCancellationRequested) return false;
        IsBusy = true;
        var recording = undo is not null && Undo is { } history ? await history.BeginAsync(undo) : null;
        try
        {
            // Not canceled with the tab: killing git mid-write leaves .git/index.lock and a half-modified tree (J9.2).
            var result = await operation(CancellationToken.None);
            switch (result)
            {
                case Done:
                    return true;
                case Failure f:
                    notifier?.Notify(NotificationSeverity.Error, failureTitle, f.Message);
                    return false;
                case Conflict c:
                    notifier?.Notify(NotificationSeverity.Error, failureTitle, string.Join(", ", c.Paths));
                    return false;
                case AuthenticationFailed a:
                    notifier?.Notify(NotificationSeverity.Error, failureTitle, a.Message);
                    return false;
                case Canceled:
                    return false;
            }
            return false;
        }
        finally
        {
            IsBusy = false;
            if (!lifetime.IsCancellationRequested) await refresh();
            if (recording is not null) await Undo!.EndAsync(recording);
        }
    }

    /// <summary>
    /// Same as <see cref="RunAsync(string, Func{CancellationToken, Task{GitResult}})"/> for an operation that produces
    /// a value (<paramref name="succeeded"/>) or may stop on conflicts on purpose (<paramref name="conflicted"/>: merge,
    /// rebase; the status, reread afterwards, shows the operation in progress).
    /// </summary>
    public async Task<bool> RunAsync<T>(
        string failureTitle, Func<CancellationToken, Task<GitResult<T>>> operation, Action<T> succeeded, Action<Conflict> conflicted, UndoStep? undo = null)
    {
        if (IsBusy || lifetime.IsCancellationRequested) return false;
        IsBusy = true;
        var recording = undo is not null && Undo is { } history ? await history.BeginAsync(undo) : null;
        try
        {
            // Not canceled with the tab: killing git mid-write leaves .git/index.lock and a half-modified tree (J9.2).
            var result = await operation(CancellationToken.None);
            switch (result)
            {
                case Success<T> s:
                    succeeded(s.Value);
                    return true;
                case Conflict c:
                    conflicted(c);
                    return false;
                case Failure f:
                    notifier?.Notify(NotificationSeverity.Error, failureTitle, f.Message);
                    return false;
                case AuthenticationFailed a:
                    notifier?.Notify(NotificationSeverity.Error, failureTitle, a.Message);
                    return false;
                case Canceled:
                    return false;
            }
            return false;
        }
        finally
        {
            IsBusy = false;
            if (!lifetime.IsCancellationRequested) await refresh();
            if (recording is not null) await Undo!.EndAsync(recording);
        }
    }
}
