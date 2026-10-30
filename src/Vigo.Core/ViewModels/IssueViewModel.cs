using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Label, assignee or milestone of the issue, ready to draw.</summary>
public sealed record IssueTagRow(string Name);

/// <summary>
/// Issue screen (J6.10a, read only), shown in place of the graph: header (title, state, author, labels, assignees, milestone),
/// then the description and the conversation (comments and events). Opened from the Issues section of the left panel; GitHub first.
/// </summary>
public sealed partial class IssueViewModel : HostedDiscussionViewModel, IDisposable
{
    private readonly IUrlLauncher? _launcher;
    private readonly IInputDialogService? _inputs;
    private CancellationTokenSource? _loading;

    public IssueViewModel(
        HostAccounts? accounts, HostingPanelViewModel hosting, IUrlLauncher? launcher, IUserNotifier? notifier, TimeProvider time,
        CancellationToken lifetime, Func<string?>? profileId = null, IConfirmationService? confirmations = null, IInputDialogService? inputs = null)
        : base(accounts, hosting, notifier, time, profileId, confirmations, lifetime)
    {
        _inputs = inputs;
        _launcher = launcher;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why nothing is shown (read error); null otherwise.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>The issue as listed; null while closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(NumberText), nameof(Author))]
    public partial Issue? Issue { get; private set; }

    public string Title => Issue?.Title ?? string.Empty;

    public string NumberText => Issue is { } i ? $"#{i.Number}" : string.Empty;

    public string Author => Issue?.Author ?? string.Empty;

    /// <summary>What was read; null while loading or after an error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(StateText), nameof(SummaryText), nameof(IsTruncated), nameof(MilestoneText), nameof(HasMilestone),
        nameof(CanCloseIssue), nameof(CanReopenIssue), nameof(CanEditIssue))]
    [NotifyCanExecuteChangedFor(nameof(EditTextCommand), nameof(EditMetadataCommand))]
    public partial IssueDetail? Detail { get; private set; }

    public IssueState State => Detail?.State ?? IssueState.Open;

    /// <summary>"Ouvert", "Fermé", "Fermé (non planifié)".</summary>
    public string StateText => Detail is not { } d ? string.Empty : d.State switch
    {
        IssueState.Closed when d.NotPlanned => Loc.Get("Issue.State.NotPlanned"),
        IssueState.Closed => Loc.Get("Issue.State.Closed"),
        _ => Loc.Get("Issue.State.Open"),
    };

    /// <summary>"3 commentaires" (events are not counted).</summary>
    public string SummaryText
    {
        get
        {
            if (Detail is null) return string.Empty;
            var count = Timeline.Count(t => t.Entry.Kind == TimelineKind.Comment);
            return count switch
            {
                0 => Loc.Get("Issue.Comments.None"),
                1 => Loc.Get("Issue.Comments.One"),
                _ => Loc.Format("Issue.Comments.Many", count),
            };
        }
    }

    /// <summary>A list was cut at the page limit: the rest is on the hosting service.</summary>
    public bool IsTruncated => Detail?.IsTruncated == true;

    public string MilestoneText => Detail?.Milestone is { Length: > 0 } m ? Loc.Format("Issue.Milestone", m) : string.Empty;

    public bool HasMilestone => MilestoneText.Length > 0;

    public ObservableCollection<IssueTagRow> Labels { get; } = [];

    public bool HasLabels => Labels.Count > 0;

    /// <summary>"Assigné à : ada, linus"; empty without assignee.</summary>
    public string AssigneesText => Detail?.Assignees is { Count: > 0 } a ? Loc.Format("Issue.Assignees", string.Join(", ", a)) : string.Empty;

    public bool HasAssignees => AssigneesText.Length > 0;

    /// <summary>Description of the issue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial IReadOnlyList<MarkdownBlock> Description { get; private set; } = [];

    public bool HasDescription => Description.Count > 0;

    /// <summary>The service lets the app comment on this issue, and edit, delete and react to comments.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComment), nameof(CanCloseIssue), nameof(CanReopenIssue), nameof(CanEditIssue))]
    [NotifyCanExecuteChangedFor(nameof(AddCommentCommand), nameof(EditTextCommand), nameof(EditMetadataCommand))]
    public partial bool CanWrite { get; private set; }

    public override bool CanComment => CanWrite;

    protected override bool CanModifyComments => CanWrite;

    protected override void OnBusyChanged()
    {
        CloseIssueCommand.NotifyCanExecuteChanged();
        CloseIssueAsNotPlannedCommand.NotifyCanExecuteChanged();
        ReopenIssueCommand.NotifyCanExecuteChanged();
        EditTextCommand.NotifyCanExecuteChanged();
        EditMetadataCommand.NotifyCanExecuteChanged();
    }

    protected override void OnCommentsChanged() => OnPropertyChanged(nameof(SummaryText));

    protected override Task<GitResult<TimelineEntry>> PostCommentAsync(HostRepository repository, string body, CancellationToken cancellationToken) =>
        Issue is { } issue && Accounts is not null
            ? Accounts.AddIssueCommentAsync(repository, issue, body, ProfileId?.Invoke(), cancellationToken)
            : Task.FromResult<GitResult<TimelineEntry>>(Canceled.Instance);

    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
    }

    /// <summary>Shows <paramref name="issue"/>: the screen opens at once, its content follows.</summary>
    public async Task OpenAsync(Issue issue)
    {
        if (Accounts is null || Hosting.Repository is not { } repository) return;

        _loading?.Cancel();
        _loading?.Dispose();
        var source = _loading = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        Reset();
        Issue = issue;
        IsOpen = true;
        IsLoading = true;
        try
        {
            var result = await Accounts.GetIssueDetailAsync(repository, issue, ProfileId?.Invoke(), source.Token);
            if (source.IsCancellationRequested) return;
            switch (result)
            {
                case Success<IssueDetail> { Value: var detail }:
                    Apply(detail);
                    break;
                case AuthenticationFailed a:
                    Message = a.Message;
                    break;
                case Failure f:
                    Message = Loc.Format("Issue.ReadFailed", f.Message);
                    break;
                case Canceled or Conflict:
                    break;
            }
        }
        finally
        {
            if (_loading == source) IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => Issue is { } issue ? OpenAsync(issue) : Task.CompletedTask;

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        _loading?.Cancel();
        IsOpen = false;
        IsLoading = false;
        Reset();
        CommentDraft = string.Empty;
        Issue = null;
    }

    /// <summary>The issue is open and the service lets the app close it (J6.10c).</summary>
    public bool CanCloseIssue => CanWrite && Detail is { State: IssueState.Open };

    /// <summary>The issue is closed and the service lets the app reopen it (J6.10c).</summary>
    public bool CanReopenIssue => CanWrite && Detail is { State: IssueState.Closed };

    [RelayCommand(CanExecute = nameof(CanChangeState))]
    private Task CloseIssueAsync() => ChangeStateAsync(false, IssueCloseReason.Completed, Loc.Get("Issue.Failure.NotClosed"));

    [RelayCommand(CanExecute = nameof(CanChangeState))]
    private Task CloseIssueAsNotPlannedAsync() => ChangeStateAsync(false, IssueCloseReason.NotPlanned, Loc.Get("Issue.Failure.NotClosed"));

    [RelayCommand(CanExecute = nameof(CanChangeState))]
    private Task ReopenIssueAsync() => ChangeStateAsync(true, IssueCloseReason.Completed, Loc.Get("Issue.Failure.NotReopened"));

    private bool CanChangeState() => !IsBusy;

    // Writes the new state, then reads the issue again: the screen shows the state, the closing event and the list refreshes its count.
    private async Task ChangeStateAsync(bool open, IssueCloseReason reason, string failureTitle)
    {
        if (Issue is not { } issue || Hosting.Repository is not { } repository || Accounts is null) return;
        if (open ? !CanReopenIssue : !CanCloseIssue) return;

        var done = await WriteAsync(failureTitle, ct => Accounts.SetIssueOpenAsync(repository, issue, open, reason, ProfileId?.Invoke(), ct));
        if (done is null) return;

        await OpenAsync(issue);
        await Hosting.RefreshAsync();
    }

    /// <summary>The service lets the app edit the issue and the app can ask for the new values (J6.10d).</summary>
    public bool CanEditIssue => CanWrite && Detail is not null && _inputs is not null;

    private bool CanEdit() => CanEditIssue && !IsBusy;

    /// <summary>Asks for a new title and description, writes them, then reads the issue again (J6.10d).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditTextAsync()
    {
        if (Issue is not { } issue || Detail is not { } detail || _inputs is null || Hosting.Repository is not { } repository || Accounts is null) return;

        var dialog = new InputDialogViewModel(Loc.Get("Issue.Edit.Title"), Loc.Get("Issue.Edit.Confirm"), Loc.Get("Issue.Edit.TitleHeader"), name => name.Trim().Length == 0 ? string.Empty : null, detail.Summary.Title)
        {
            Description = $"{repository.FullName} · #{issue.Number}",
            SecondHeader = Loc.Get("Issue.Edit.BodyHeader"),
            IsSecondMultiline = true,
            SecondText = detail.Body,
        };
        if (!await _inputs.ShowAsync(dialog, Lifetime)) return;

        var title = dialog.Value;
        var body = dialog.SecondValue;
        if (title == detail.Summary.Title && body == detail.Body.Trim()) return;

        var done = await WriteAsync(Loc.Get("Issue.Failure.NotEdited"), ct => Accounts.EditIssueTextAsync(repository, issue, title, body, ProfileId?.Invoke(), ct));
        if (done is null) return;
        await ReloadAfterEditAsync(issue);
    }

    /// <summary>Reads what the repository offers, asks which labels, assignees and milestone the issue gets, then writes them (J6.10d).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditMetadataAsync()
    {
        if (Issue is not { } issue || Detail is not { } detail || _inputs is null || Hosting.Repository is not { } repository || Accounts is null) return;

        var options = await WriteAsync(Loc.Get("Issue.Failure.OptionsUnavailable"), ct => Accounts.GetIssueMetadataOptionsAsync(repository, ProfileId?.Invoke(), ct));
        if (options is null) return;

        var current = new IssueMetadata(detail.Labels, detail.Assignees, null);
        var dialog = new IssueMetadataDialogViewModel($"{repository.FullName} · #{issue.Number}", options, current, detail.Milestone);
        if (!await _inputs.ShowAsync(dialog, Lifetime)) return;

        var chosen = dialog.Result;
        var before = dialog.InitialResult;
        if (SameNames(chosen.Labels, before.Labels) && SameNames(chosen.Assignees, before.Assignees) && chosen.MilestoneNumber == before.MilestoneNumber) return;

        var done = await WriteAsync(Loc.Get("Issue.Failure.NotEdited"), ct => Accounts.SetIssueMetadataAsync(repository, issue, chosen, ProfileId?.Invoke(), ct));
        if (done is null) return;
        await ReloadAfterEditAsync(issue);
    }

    private static bool SameNames(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    // The screen shows what the service stored (title, labels, new events); the list of the left panel shows the new title.
    private async Task ReloadAfterEditAsync(Issue issue)
    {
        await OpenAsync(issue);
        await Hosting.RefreshAsync();
    }

    /// <summary>Opens the issue in the browser.</summary>
    [RelayCommand]
    private async Task OpenInBrowserAsync()
    {
        if (Issue is not { } issue || _launcher is null) return;
        if (!await _launcher.OpenAsync(issue.Url)) Notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Issue.Failure.BrowserFailed"), issue.Url);
    }

    private void Reset()
    {
        Message = null;
        Detail = null;
        CanWrite = false;
        Description = [];
        Timeline.Clear();
        Labels.Clear();
        OnPropertyChanged(nameof(HasLabels));
        OnPropertyChanged(nameof(AssigneesText));
        OnPropertyChanged(nameof(HasAssignees));
    }

    private void Apply(IssueDetail detail)
    {
        Detail = detail;
        Description = MarkdownParser.Parse(detail.Body, References);
        foreach (var label in detail.Labels) Labels.Add(new IssueTagRow(label));
        OnPropertyChanged(nameof(HasLabels));
        OnPropertyChanged(nameof(AssigneesText));
        OnPropertyChanged(nameof(HasAssignees));
        CanWrite = Accounts is not null && Hosting.Repository is { } r && Accounts.SupportsIssueWrite(r.Provider);
        foreach (var entry in detail.Timeline) Timeline.Add(NewRow(entry));
        OnPropertyChanged(nameof(SummaryText));
    }
}
