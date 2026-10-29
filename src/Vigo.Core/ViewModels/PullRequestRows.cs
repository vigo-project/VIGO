using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Part of the pull request screen that is shown.</summary>
public enum PullRequestTab
{
    Conversation,
    Commits,
    Files,
}

/// <summary>Reaction already given to a comment, ready to draw: the emoji and how many people used it.</summary>
public sealed class ReactionChip(long commentId, ReactionKind kind, int count)
{
    /// <summary>Reactions in the order of the picker.</summary>
    public static IReadOnlyList<ReactionKind> All { get; } = Enum.GetValues<ReactionKind>();

    public long CommentId { get; } = commentId;

    public ReactionKind Kind { get; } = kind;

    public string Text => string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{EmojiOf(Kind)} {count}");

    public string ToolTip => Loc.Format("Pr.Reaction.ToolTip", LabelOf(Kind), count);

    public static string EmojiOf(ReactionKind kind) => kind switch
    {
        ReactionKind.ThumbsUp => "\U0001F44D",
        ReactionKind.ThumbsDown => "\U0001F44E",
        ReactionKind.Laugh => "\U0001F604",
        ReactionKind.Hooray => "\U0001F389",
        ReactionKind.Confused => "\U0001F615",
        ReactionKind.Heart => "❤️",
        ReactionKind.Rocket => "\U0001F680",
        _ => "\U0001F440",
    };

    public static string LabelOf(ReactionKind kind) => kind switch
    {
        ReactionKind.ThumbsUp => Loc.Get("Pr.Reaction.ThumbsUp"),
        ReactionKind.ThumbsDown => Loc.Get("Pr.Reaction.ThumbsDown"),
        ReactionKind.Laugh => Loc.Get("Pr.Reaction.Laugh"),
        ReactionKind.Hooray => Loc.Get("Pr.Reaction.Hooray"),
        ReactionKind.Confused => Loc.Get("Pr.Reaction.Confused"),
        ReactionKind.Heart => Loc.Get("Pr.Reaction.Heart"),
        ReactionKind.Rocket => Loc.Get("Pr.Reaction.Rocket"),
        _ => Loc.Get("Pr.Reaction.Eyes"),
    };

    public override string ToString() => ToolTip;
}

/// <summary>Entry of the conversation, ready to draw: author, what they did, when, and the Markdown of their text.</summary>
public sealed partial class TimelineRow(TimelineEntry entry, string date, bool canModify = false, bool canReact = false) : ObservableObject
{
    public TimelineEntry Entry { get; } = entry;

    /// <summary>Reactions given to the comment (only the kinds in use); clicking one adds or removes the signed-in account's own.</summary>
    /// <remarks>An <see cref="ObservableCollection{T}"/>: an <c>ItemsSource</c> bound to an <c>IReadOnlyList</c> fails under AOT ("not a supported vector"), which aborts every binding of the row.</remarks>
    public ObservableCollection<ReactionChip> Reactions { get; } =
        [.. (entry.Reactions ?? []).Select(r => new ReactionChip(entry.Id, r.Kind, r.Count))];

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>The service lets the app react to this comment (a discussion comment, on a service that can be written).</summary>
    public bool CanReact { get; } = canReact && entry is { Kind: TimelineKind.Comment, Id: not 0 };

    /// <summary>The signed-in account wrote this comment and the service lets the app change it (edit, delete).</summary>
    public bool CanModify { get; } = canModify && entry is { Kind: TimelineKind.Comment, Id: not 0 };

    /// <summary>The text is being edited in place.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReading))]
    public partial bool IsEditing { get; set; }

    public bool IsReading => !IsEditing;

    /// <summary>Text typed while editing.</summary>
    [ObservableProperty]
    public partial string EditText { get; set; } = entry.Body;

    public string Author => Entry.Author;

    public string AuthorInitials => new GitIdentity(Entry.Author, null).Initials;

    public int AuthorColorIndex => StableColor.IndexFor(Entry.Author);

    public string DateText { get; } = date;

    /// <summary>"a commenté", "a approuvé", "a demandé des modifications"…</summary>
    public string Action => Entry.Kind switch
    {
        TimelineKind.Comment => Loc.Get("Pr.Timeline.Commented"),
        TimelineKind.ReviewComment => Loc.Get("Pr.Timeline.CommentedDiff"),
        TimelineKind.Event => Entry.Body,
        _ => Entry.Verdict switch
        {
            ReviewVerdict.Approved => Loc.Get("Pr.Timeline.Approved"),
            ReviewVerdict.ChangesRequested => Loc.Get("Pr.Timeline.ChangesRequested"),
            ReviewVerdict.Dismissed => Loc.Get("Pr.Timeline.Dismissed"),
            _ => Loc.Get("Pr.Timeline.Reviewed"),
        },
    };

    public bool IsApproved => Entry.Verdict == ReviewVerdict.Approved;

    public bool IsChangesRequested => Entry.Verdict == ReviewVerdict.ChangesRequested;

    /// <summary>"src/a.cs:12" for a comment on the diff; empty otherwise.</summary>
    public string Location => Entry.Path is { } path ? Entry.Line is { } line ? $"{path}:{line}" : path : string.Empty;

    public bool HasLocation => Entry.Path is not null;

    // An event of an issue is a sentence (shown as the action), not Markdown.
    public IReadOnlyList<MarkdownBlock> Body { get; } = entry.Kind == TimelineKind.Event ? [] : MarkdownParser.Parse(entry.Body);

    public bool HasBody => Body.Count > 0;

    /// <summary>Name read by the narrator (and by UI Automation).</summary>
    public override string ToString() => $"{Author} {Action}";
}

/// <summary>Comment of a discussion thread on the diff, ready to draw.</summary>
public sealed class ThreadCommentRow(ReviewThreadComment comment, string date)
{
    public ReviewThreadComment Comment { get; } = comment;

    public string Author => Comment.Author;

    public string AuthorInitials => new GitIdentity(Comment.Author, null).Initials;

    public int AuthorColorIndex => StableColor.IndexFor(Comment.Author);

    public string DateText { get; } = date;

    public IReadOnlyList<MarkdownBlock> Body { get; } = MarkdownParser.Parse(comment.Body);

    public bool HasBody => Body.Count > 0;

    public override string ToString() => $"{Author}, {DateText}";
}

/// <summary>Discussion thread on a line of the diff (J6.9e), ready to draw: its comments, state, and the reply being typed.</summary>
public sealed partial class ThreadRow(ReviewThread thread, IReadOnlyList<ThreadCommentRow> comments, bool canReview) : ObservableObject
{
    public ReviewThread Thread { get; } = thread;

    // An ObservableCollection: bound as an ItemsSource, which fails under AOT for an IReadOnlyList.
    public ObservableCollection<ThreadCommentRow> Comments { get; } = [.. comments];

    /// <summary>The service lets the app reply to the thread and resolve it.</summary>
    public bool CanReview { get; } = canReview && thread.Comments.Count > 0;

    public bool IsResolved => Thread.IsResolved;

    /// <summary>"Ligne 12", "Ligne 8 (ancienne version)", "Code modifié depuis".</summary>
    public string Location => Thread.Line is not { } line
        ? Loc.Get("Pr.Thread.OutdatedCode")
        : Loc.Format(Thread.Side == ReviewSide.Old ? "Pr.Thread.LineOld" : "Pr.Thread.Line", line);

    /// <summary>"Résolue", "Obsolète", "Résolue · obsolète"; empty for an open thread on current code.</summary>
    public string StatusText => (Thread.IsResolved, Thread.IsOutdated) switch
    {
        (true, true) => Loc.Get("Pr.Thread.ResolvedOutdated"),
        (true, false) => Loc.Get("Pr.Thread.Resolved"),
        (false, true) => Loc.Get("Pr.Thread.Outdated"),
        _ => string.Empty,
    };

    public string ResolveLabel => Loc.Get(IsResolved ? "Pr.Thread.Reopen" : "Pr.Thread.Resolve");

    /// <summary>Reply being typed.</summary>
    [ObservableProperty]
    public partial string ReplyText { get; set; } = string.Empty;

    public override string ToString() => Loc.Format("Pr.Thread.Summary", Location, Thread.Comments.Count);
}

/// <summary>Comment on a line waiting for the review to be sent, ready to draw.</summary>
public sealed class ReviewDraftRow(NewReviewComment comment)
{
    public NewReviewComment Comment { get; } = comment;

    /// <summary>"src/a.cs : ligne 12".</summary>
    public string Location => Loc.Format(Comment.Side == ReviewSide.Old ? "Pr.Draft.LocationOld" : "Pr.Draft.Location", Comment.Path, Comment.Line);

    public string Text => Comment.Body;

    public override string ToString() => Location;
}

/// <summary>Commit of the pull request, ready to draw.</summary>
public sealed class PullRequestCommitRow(PullRequestCommit commit, string date)
{
    public string Subject => commit.Subject;

    public string ShortSha => commit.Sha.Length > 7 ? commit.Sha[..7] : commit.Sha;

    public string AuthorInitials => new GitIdentity(commit.Author, null).Initials;

    public int AuthorColorIndex => StableColor.IndexFor(commit.Author);

    /// <summary>"Ada Lovelace · il y a 2 j · a1b2c3d".</summary>
    public string Meta => $"{commit.Author} · {date} · {ShortSha}";

    public override string ToString() => $"{Subject}, {ShortSha}";
}

/// <summary>Start of a discussion thread, shown under its file in the list: who wrote it, where, and the first words.</summary>
public sealed class FileCommentPreview(ReviewThread thread)
{
    private const int SnippetLength = 90;

    public ReviewThread Thread { get; } = thread;

    public string ThreadId => Thread.Id;

    public string Author => Thread.Comments.Count > 0 ? Thread.Comments[0].Author : string.Empty;

    /// <summary>First line of the first comment, cut to a short length (Markdown marks left as they are).</summary>
    public string Snippet { get; } = SnippetOf(thread);

    /// <summary>"L12", "L8-12"; empty when the code changed since (outdated thread).</summary>
    public string LineText => Thread switch
    {
        { Line: not { } } => string.Empty,
        { StartLine: { } start, Line: { } line } when start < line => $"L{start}-{line}",
        { Line: { } line } => $"L{line}",
    };

    public bool IsResolved => Thread.IsResolved;

    /// <summary>Replies after the first comment.</summary>
    public int ReplyCount => Math.Max(0, Thread.Comments.Count - 1);

    public bool HasReplies => ReplyCount > 0;

    public string ReplyText => ReplyCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string StatusText => Thread.IsResolved ? Loc.Get("Pr.Thread.Resolved") : string.Empty;

    private static string SnippetOf(ReviewThread thread)
    {
        if (thread.Comments.Count == 0) return string.Empty;
        var text = thread.Comments[0].Body.Trim();
        var end = text.IndexOfAny(['\r', '\n']);
        if (end >= 0) text = text[..end].TrimEnd();
        return text.Length > SnippetLength ? text[..SnippetLength].TrimEnd() + "…" : text;
    }

    public override string ToString() => $"{Author} {LineText} {Snippet}".Trim();
}

/// <summary>File of the pull request, ready to draw (a row of the list, or of the tree of folders).</summary>
public sealed class PullRequestFileRow(PullRequestFile file, int commentCount) : FileListRow
{
    public PullRequestFile File { get; } = file;

    // Comments on the file as the conversation counts them: shown while the service gives no threads.
    private readonly int _timelineCount = commentCount;

    public override string Path => File.Path;

    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];

    public string Directory => Path.LastIndexOf('/') is var i and > 0 ? Path[..(i + 1)] : string.Empty;

    /// <summary>Start of the discussions on the file, in the order of the lines (an ObservableCollection: bound as an ItemsSource).</summary>
    public ObservableCollection<FileCommentPreview> Previews { get; } = [];

    public bool HasPreviews => Previews.Count > 0;

    /// <summary>Replaces the previews by those of <paramref name="threads"/>; the comment count follows them.</summary>
    internal void SetThreads(IEnumerable<ReviewThread> threads)
    {
        var shown = threads.OrderBy(t => t.Line ?? int.MaxValue).ThenBy(t => t.StartLine ?? 0).Select(t => new FileCommentPreview(t)).ToList();
        Previews.Clear();
        foreach (var preview in shown) Previews.Add(preview);
        CommentCount = shown.Count > 0 ? shown.Sum(p => p.Thread.Comments.Count) : _timelineCount;
        OnPropertyChanged(nameof(HasPreviews));
    }

    /// <summary>Letter of the status badge: A, M, D, R…</summary>
    public string StatusLetter => File.Kind switch
    {
        FileChangeKind.Added => "A",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        FileChangeKind.Copied => "C",
        FileChangeKind.TypeChanged => "T",
        _ => "M",
    };

    // Services without patches (Azure DevOps) give no counts: they are known once the diff of the file is computed.
    private (int Additions, int Deletions)? _computed;

    public int Additions => _computed?.Additions ?? File.Additions;

    public int Deletions => _computed?.Deletions ?? File.Deletions;

    internal bool HasStats => _computed is not null;

    internal void SetStats(int additions, int deletions)
    {
        _computed = (additions, deletions);
        OnPropertyChanged(nameof(Additions));
        OnPropertyChanged(nameof(Deletions));
        OnPropertyChanged(nameof(AdditionsText));
        OnPropertyChanged(nameof(DeletionsText));
    }

    public string AdditionsText => $"+{Additions}";

    public string DeletionsText => Deletions > 0 ? $"−{Deletions}" : string.Empty;

    public int CommentCount
    {
        get;
        private set
        {
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(HasComments));
            OnPropertyChanged(nameof(CommentsText));
        }
    } = commentCount;

    public bool HasComments => CommentCount > 0;

    public string CommentsText => CommentCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public override string ToString() => Path;
}

/// <summary>Label of the pull request, ready to draw.</summary>
public sealed record PullRequestLabelRow(string Name);

/// <summary>Check of the pull request, ready to draw.</summary>
/// <param name="open">Opens an address in the browser; null: the row has no link.</param>
public sealed class PullRequestCheckRow(CheckRun run, Func<string, Task>? open = null)
{
    public CheckRun Run => run;

    public string Name => run.Name;

    public string Description => run.Description ?? string.Empty;

    public bool HasDescription => !string.IsNullOrWhiteSpace(run.Description);

    public CheckState State => run.State;

    // One flag per icon: the view picks the glyph and the brush with visibilities (no resource lookup from code, AOT).
    public bool IsSuccess => run.State == CheckState.Success;

    public bool IsFailed => run.State is CheckState.Failure or CheckState.Canceled;

    public bool IsPending => run.State == CheckState.Pending;

    public bool IsSkipped => run.State is CheckState.Skipped or CheckState.None;

    public string StateText => run.State switch
    {
        CheckState.Success => Loc.Get("Pr.Check.Success"),
        CheckState.Failure => Loc.Get("Pr.Check.Failure"),
        CheckState.Pending => Loc.Get("Pr.Check.Pending"),
        CheckState.Skipped => Loc.Get("Pr.Check.Skipped"),
        CheckState.Canceled => Loc.Get("Pr.Check.Canceled"),
        _ => string.Empty,
    };

    /// <summary>"2 min 13 s"; empty while the check runs or when the service gives no dates.</summary>
    public string DurationText => run.StartedAt is { } start && run.CompletedAt is { } end && end >= start && run.State != CheckState.Pending
        ? FormatDuration(end - start)
        : string.Empty;

    public bool HasDuration => DurationText.Length > 0;

    /// <summary>"Requis" when the merge waits for this check; empty when it is optional or unknown.</summary>
    public string RequiredText => run.IsRequired == true ? Loc.Get("Pr.Check.Required") : string.Empty;

    public bool IsRequired => run.IsRequired == true;

    public bool HasUrl => open is not null && !string.IsNullOrEmpty(run.Url);

    /// <summary>Opens the logs (or the build) in the browser.</summary>
    public IAsyncRelayCommand OpenCommand { get; } = new AsyncRelayCommand(() => run.Url is { Length: > 0 } url && open is not null ? open(url) : Task.CompletedTask);

    internal static string FormatDuration(TimeSpan span)
    {
        var seconds = (int)Math.Round(span.TotalSeconds);
        return seconds switch
        {
            < 60 => Loc.Format("Pr.Check.Duration.Seconds", seconds),
            < 3600 => Loc.Format("Pr.Check.Duration.Minutes", seconds / 60, seconds % 60),
            _ => Loc.Format("Pr.Check.Duration.Hours", seconds / 3600, seconds % 3600 / 60),
        };
    }

    /// <summary>Failures first, then running, passed, skipped; a required check before an optional one in the same state.</summary>
    internal static int Rank(CheckRun run) => run.State switch
    {
        CheckState.Failure or CheckState.Canceled => 0,
        CheckState.Pending => 1,
        CheckState.Success => 2,
        _ => 3,
    };

    public override string ToString() => $"{Name}, {StateText}";
}
