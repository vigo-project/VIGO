using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A titled group of fields, ready to draw.</summary>
/// <remarks>An <see cref="ObservableCollection{T}"/>: an <c>ItemsSource</c> bound to an <c>IReadOnlyList</c> fails under AOT.</remarks>
public sealed record WorkItemSectionRow(string Title, ObservableCollection<WorkItemField> Fields);

/// <summary>A rich text field, parsed.</summary>
public sealed record WorkItemTextRow(string Title, IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>One link, ready to draw: its title, what it is (type and state of a work item) and whether the app can show it itself.</summary>
public sealed record WorkItemLinkRow(WorkItemLink Link, string Detail, bool OpensInApp)
{
    public string Title => Link.Title;

    public WorkItemLinkKind Kind => Link.Kind;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>State of the linked work item or pull request; empty when unknown.</summary>
    public string StateText => Link.State ?? string.Empty;

    public bool HasState => StateText.Length > 0;

    public WorkItemStateKind StateKind => Link.StateKind ?? WorkItemStateKind.InProgress;
}

/// <summary>Links of one kind ("Parent", "Children", "Pull requests"…).</summary>
public sealed record WorkItemLinkGroup(string Title, ObservableCollection<WorkItemLinkRow> Items);

/// <summary>A file attached to the work item.</summary>
public sealed record WorkItemAttachmentRow(WorkItemAttachment Attachment, string SizeText)
{
    public string Name => Attachment.Name;
}

/// <summary>
/// Work item screen (J6.14, read only), shown in place of the graph: header (type, number, title, state, assignee, tags), then the
/// planning, people, dates and custom fields, the rich text fields, the links (a linked work item opens in this same screen, and the
/// back button returns to the previous one), the attachments, and the comments and changes in one timeline. Azure DevOps first.
/// </summary>
public sealed partial class WorkItemViewModel : ObservableObject, IDisposable
{
    private readonly HostAccounts? _accounts;
    private readonly HostingPanelViewModel _hosting;
    private readonly IUrlLauncher? _launcher;
    private readonly IUserNotifier? _notifier;
    private readonly TimeProvider _time;
    private readonly CancellationToken _lifetime;
    private readonly Func<string?>? _profileId;
    private readonly Stack<Issue> _history = new();
    private CancellationTokenSource? _loading;

    public WorkItemViewModel(
        HostAccounts? accounts, HostingPanelViewModel hosting, IUrlLauncher? launcher, IUserNotifier? notifier, TimeProvider time,
        CancellationToken lifetime, Func<string?>? profileId = null)
    {
        _accounts = accounts;
        _hosting = hosting;
        _launcher = launcher;
        _notifier = notifier;
        _time = time;
        _lifetime = lifetime;
        _profileId = profileId;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why nothing is shown (read error); null otherwise.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>The work item as listed; null while closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(NumberText), nameof(TypeText), nameof(Author))]
    public partial Issue? Issue { get; private set; }

    public string Title => Issue?.Title ?? string.Empty;

    public string NumberText => Issue is { } i ? $"#{i.Number}" : string.Empty;

    public string TypeText => Issue?.Type ?? string.Empty;

    public string Author => Issue?.Author ?? string.Empty;

    /// <summary>What was read; null while loading or after an error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(StateKind), nameof(TypeColor), nameof(AssignedToText), nameof(HasAssignedTo),
        nameof(SummaryText), nameof(IsTruncated), nameof(HasDetail))]
    public partial WorkItemDetail? Detail { get; private set; }

    public bool HasDetail => Detail is not null;

    public string StateText => Detail?.State ?? string.Empty;

    public WorkItemStateKind StateKind => Detail?.StateKind ?? WorkItemStateKind.Proposed;

    /// <summary>"RRGGBB" color of the work item type; empty when unknown.</summary>
    public string TypeColor => Detail?.TypeColor ?? string.Empty;

    /// <summary>"Assigné à : Ada"; empty without assignee.</summary>
    public string AssignedToText => Detail?.AssignedTo is { Length: > 0 } who ? Loc.Format("WorkItem.AssignedTo", who) : Loc.Get("WorkItem.Unassigned");

    public bool HasAssignedTo => Detail?.AssignedTo is { Length: > 0 };

    /// <summary>"3 commentaires".</summary>
    public string SummaryText => Detail is null ? string.Empty : Detail.CommentCount switch
    {
        0 => Loc.Get("WorkItem.Comments.None"),
        1 => Loc.Get("WorkItem.Comments.One"),
        var count => Loc.Format("WorkItem.Comments.Many", count),
    };

    /// <summary>A list was cut at the page limit: the rest is on the service.</summary>
    public bool IsTruncated => Detail?.IsTruncated == true;

    public ObservableCollection<IssueTagRow> Tags { get; } = [];

    public bool HasTags => Tags.Count > 0;

    public ObservableCollection<WorkItemSectionRow> Sections { get; } = [];

    public ObservableCollection<WorkItemTextRow> Texts { get; } = [];

    public bool HasTexts => Texts.Count > 0;

    public ObservableCollection<WorkItemLinkGroup> LinkGroups { get; } = [];

    public bool HasLinks => LinkGroups.Count > 0;

    public ObservableCollection<WorkItemAttachmentRow> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Comments and changes, oldest first.</summary>
    public ObservableCollection<TimelineRow> Timeline { get; } = [];

    public bool HasTimeline => Timeline.Count > 0;

    /// <summary>Another work item was opened from this screen: the back button returns to it instead of closing the screen.</summary>
    public bool CanGoBackToItem => _history.Count > 0;

    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
    }

    /// <summary>Shows <paramref name="item"/>: the screen opens at once, its content follows. A work item opened from the list starts a new trail.</summary>
    public Task OpenAsync(Issue item)
    {
        _history.Clear();
        OnPropertyChanged(nameof(CanGoBackToItem));
        return LoadAsync(item);
    }

    private async Task LoadAsync(Issue item)
    {
        if (_accounts is null || _hosting.Repository is not { } repository) return;

        _loading?.Cancel();
        _loading?.Dispose();
        var source = _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Reset();
        Issue = item;
        IsOpen = true;
        IsLoading = true;
        try
        {
            var result = await _accounts.GetWorkItemDetailAsync(repository, item, _profileId?.Invoke(), source.Token);
            if (source.IsCancellationRequested) return;
            switch (result)
            {
                case Success<WorkItemDetail> { Value: var detail }:
                    Apply(detail);
                    break;
                case AuthenticationFailed a:
                    Message = a.Message;
                    break;
                case Failure f:
                    Message = Loc.Format("WorkItem.ReadFailed", f.Message);
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
    private Task RefreshAsync() => Issue is { } item ? LoadAsync(item) : Task.CompletedTask;

    /// <summary>Back to the previous work item of the trail, or to the graph when there is none.</summary>
    [RelayCommand]
    public async Task BackAsync()
    {
        if (_history.Count == 0)
        {
            Close();
            return;
        }
        var previous = _history.Pop();
        OnPropertyChanged(nameof(CanGoBackToItem));
        await LoadAsync(previous);
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        _loading?.Cancel();
        IsOpen = false;
        IsLoading = false;
        Reset();
        _history.Clear();
        OnPropertyChanged(nameof(CanGoBackToItem));
        Issue = null;
    }

    /// <summary>Opens a link: another work item in this screen (the current one joins the trail), anything else in the browser.</summary>
    [RelayCommand]
    private async Task OpenLinkAsync(WorkItemLinkRow? row)
    {
        if (row is null) return;
        if (row.OpensInApp && row.Link.WorkItemId is { } id && Issue is { } current)
        {
            if (id == current.Number) return;
            _history.Push(current);
            OnPropertyChanged(nameof(CanGoBackToItem));
            await LoadAsync(new Issue(id, row.Link.Title, string.Empty, default, row.Link.Url, row.Link.Type));
            return;
        }
        await OpenUrlAsync(row.Link.Url);
    }

    [RelayCommand]
    private Task OpenAttachmentAsync(WorkItemAttachmentRow? row) => row is null ? Task.CompletedTask : OpenUrlAsync(row.Attachment.Url);

    /// <summary>Opens the work item in the browser.</summary>
    [RelayCommand]
    private Task OpenInBrowserAsync() => Issue is { } item ? OpenUrlAsync(item.Url) : Task.CompletedTask;

    private async Task OpenUrlAsync(string url)
    {
        if (_launcher is null) return;
        if (!await _launcher.OpenAsync(url)) _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Issue.Failure.BrowserFailed"), url);
    }

    private void Reset()
    {
        Message = null;
        Detail = null;
        Tags.Clear();
        Sections.Clear();
        Texts.Clear();
        LinkGroups.Clear();
        Attachments.Clear();
        Timeline.Clear();
        NotifyCollections();
    }

    private void NotifyCollections()
    {
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasTexts));
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasTimeline));
    }

    private void Apply(WorkItemDetail detail)
    {
        // The list only knew the title and the type: the screen now has the real author, date and web address.
        Issue = detail.Summary;
        Detail = detail;
        foreach (var tag in detail.Tags) Tags.Add(new IssueTagRow(tag));
        foreach (var section in detail.Sections) Sections.Add(new WorkItemSectionRow(section.Title, [.. section.Fields]));
        var references = _hosting.Repository is { } repository ? new MarkdownReferences(repository.ReferenceLinks) : null;
        foreach (var text in detail.Texts) Texts.Add(new WorkItemTextRow(text.Title, MarkdownParser.Parse(text.Markdown, references)));

        foreach (var group in detail.Links.GroupBy(l => l.Kind))
        {
            var rows = group.Select(l => new WorkItemLinkRow(l, DetailOf(l), l.WorkItemId is not null));
            LinkGroups.Add(new WorkItemLinkGroup(Loc.Get($"WorkItem.LinkGroup.{group.Key}"), [.. rows]));
        }
        foreach (var file in detail.Attachments) Attachments.Add(new WorkItemAttachmentRow(file, SizeText(file.Size)));
        foreach (var entry in detail.Timeline)
            Timeline.Add(new TimelineRow(entry, RelativeTime.Format(entry.CreatedAt, _time.GetUtcNow(), _time.LocalTimeZone), references: references));
        NotifyCollections();
    }

    // "Bug" for a work item (its state is a badge of its own); the comment of a hyperlink.
    private static string DetailOf(WorkItemLink link) =>
        string.Join(" · ", new[] { link.Type, link.Comment }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string SizeText(long bytes) => bytes <= 0 ? string.Empty : ByteSize.Format(bytes);
}
