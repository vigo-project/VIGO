using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// What the issue and the pull request screens share: the conversation (<see cref="Timeline"/>), the new comment, the
/// edition, deletion and reactions of comments, and the one-write-at-a-time rule (<see cref="IsBusy"/>, J9.18).
/// </summary>
public abstract partial class HostedDiscussionViewModel : ObservableObject
{
    protected HostAccounts? Accounts { get; }

    protected HostingPanelViewModel Hosting { get; }

    protected IUserNotifier? Notifier { get; }

    protected TimeProvider Time { get; }

    protected CancellationToken Lifetime { get; }

    protected Func<string?>? ProfileId { get; }

    protected IConfirmationService? Confirmations { get; }

    protected HostedDiscussionViewModel(
        HostAccounts? accounts, HostingPanelViewModel hosting, IUserNotifier? notifier, TimeProvider time,
        Func<string?>? profileId, IConfirmationService? confirmations, CancellationToken lifetime)
    {
        Accounts = accounts;
        Hosting = hosting;
        Notifier = notifier;
        Time = time;
        Lifetime = lifetime;
        ProfileId = profileId;
        Confirmations = confirmations;
    }

    /// <summary>Comments and events, oldest first.</summary>
    public ObservableCollection<TimelineRow> Timeline { get; } = [];

    /// <summary>The service lets the app comment here.</summary>
    public abstract bool CanComment { get; }

    /// <summary>The service lets the app edit, delete and react to comments (the buttons of <see cref="TimelineRow"/>).</summary>
    protected abstract bool CanModifyComments { get; }

    /// <summary>A write is in progress: the buttons wait for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(AddCommentCommand))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    /// <summary>Text of the new comment.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommentCommand))]
    public partial string CommentDraft { get; set; } = string.Empty;

    partial void OnIsBusyChanged(bool value) => OnBusyChanged();

    /// <summary>The buttons of the screen that wait for a write follow <see cref="IsBusy"/>.</summary>
    protected virtual void OnBusyChanged()
    {
    }

    /// <summary>The comments were added or removed (the count shown by the screen follows).</summary>
    protected virtual void OnCommentsChanged()
    {
    }

    protected HostRepository? Repository => Hosting.Repository;

    /// <summary>What <c>#n</c>, <c>!n</c> and <c>@name</c> in the Markdown link to, and the names of the people mentioned by identifier.</summary>
    public Markdown.MarkdownReferences References => new(Repository?.ReferenceLinks ?? new ReferenceLinks(null, null, null), MentionNames);

    /// <summary>Names of the people mentioned by identifier (Azure DevOps), filled as they are read; null when there are none.</summary>
    protected virtual IReadOnlyDictionary<string, string>? MentionNames => null;

    /// <summary>Sends the new comment; <see cref="Canceled"/> when nothing is open.</summary>
    protected abstract Task<GitResult<TimelineEntry>> PostCommentAsync(HostRepository repository, string body, CancellationToken cancellationToken);

    private bool CanAddComment() => CanComment && !IsBusy && !string.IsNullOrWhiteSpace(CommentDraft);

    [RelayCommand(CanExecute = nameof(CanAddComment))]
    private async Task AddCommentAsync()
    {
        if (Repository is not { } repository || Accounts is null) return;
        var body = CommentDraft.Trim();
        var entry = await WriteAsync(Loc.Get("Issue.Failure.CommentNotPosted"), ct => PostCommentAsync(repository, body, ct));
        if (entry is null) return;
        Timeline.Add(NewRow(entry));
        OnCommentsChanged();
        CommentDraft = string.Empty;
    }

    [RelayCommand]
    private static void BeginEdit(TimelineRow row)
    {
        row.EditText = row.Entry.Body;
        row.IsEditing = true;
    }

    [RelayCommand]
    private static void CancelEdit(TimelineRow row) => row.IsEditing = false;

    [RelayCommand]
    private async Task SaveEditAsync(TimelineRow row)
    {
        if (Repository is not { } repository || Accounts is null || string.IsNullOrWhiteSpace(row.EditText)) return;
        var text = row.EditText.Trim();
        var entry = await WriteAsync(Loc.Get("Issue.Failure.CommentNotEdited"),
            ct => Accounts.EditPullRequestCommentAsync(repository, row.Entry.Id, text, ProfileId?.Invoke(), ct));
        if (entry is null) return;
        var index = Timeline.IndexOf(row);
        if (index >= 0) Timeline[index] = NewRow(entry);
    }

    [RelayCommand]
    private async Task DeleteCommentAsync(TimelineRow row)
    {
        if (Repository is not { } repository || Accounts is null) return;
        if (Confirmations is null || !(await Confirmations.ConfirmAsync(new ConfirmationRequest(
                Loc.Get("Issue.DeleteComment.Title"), Loc.Get("Issue.DeleteComment.Message"), Loc.Get("Issue.DeleteComment.Confirm"), IsDestructive: true), Lifetime)).Confirmed) return;
        var done = await WriteAsync(Loc.Get("Issue.Failure.CommentNotDeleted"),
            ct => Accounts.DeletePullRequestCommentAsync(repository, row.Entry.Id, ProfileId?.Invoke(), ct));
        if (done is null) return;
        Timeline.Remove(row);
        OnCommentsChanged();
    }

    /// <summary>Adds the signed-in account's reaction to a comment, or removes it when it is already there.</summary>
    public async Task ToggleReactionAsync(long commentId, ReactionKind kind)
    {
        if (Repository is not { } repository || Accounts is null) return;
        var entry = await WriteAsync(Loc.Get("Issue.Failure.ReactionNotSaved"),
            ct => Accounts.ToggleCommentReactionAsync(repository, commentId, kind, ProfileId?.Invoke(), ct));
        if (entry is null) return;
        var index = Timeline.ToList().FindIndex(r => r.Entry.Id == commentId && r.Entry.Kind == TimelineKind.Comment);
        if (index >= 0) Timeline[index] = NewRow(entry);
    }

    protected TimelineRow NewRow(TimelineEntry entry) =>
        new(entry, RelativeTime.Format(entry.CreatedAt, Time.GetUtcNow(), Time.LocalTimeZone), CanModifyComments && IsOwn(entry), CanModifyComments, References);

    // The service refuses to change someone else's comment: the buttons are only offered on the signed-in account's own.
    protected bool IsOwn(TimelineEntry entry) =>
        Repository is { } r && Accounts?.AccountOf(r.Provider, ProfileId?.Invoke()) is { } account
        && string.Equals(account.Login, entry.Author, StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs one write: the result when it succeeded, null otherwise (the reason is shown in a toast).</summary>
    protected async Task<T?> WriteAsync<T>(string failureTitle, Func<CancellationToken, Task<GitResult<T>>> write) where T : class
    {
        if (IsBusy) return null;
        IsBusy = true;
        try
        {
            switch (await write(Lifetime))
            {
                case Success<T> { Value: var value }:
                    return value;
                case AuthenticationFailed a:
                    Notifier?.Notify(NotificationSeverity.Warning, failureTitle, a.Message);
                    break;
                case Failure f:
                    Notifier?.Notify(NotificationSeverity.Error, failureTitle, f.Message);
                    break;
                case Canceled or Conflict:
                    break;
            }
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
