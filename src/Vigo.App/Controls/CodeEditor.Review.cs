using Vigo.App.Services;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.App.Controls;

/// <summary>A comment written in the box under a line or a selection of the diff (J6.11).</summary>
/// <param name="Key">Identifies the box: <see cref="CodeEditor.ReviewDone"/> tells it how the write ended.</param>
/// <param name="AsDraft">Kept for the review instead of being published at once.</param>
public sealed record EditorReviewSubmit(
    int Key, bool AsDraft, ReviewSide Side, int StartLine, int Line, int StartColumn, int EndColumn, string Body);

/// <summary>A reply typed in the box of a discussion thread.</summary>
public sealed record EditorReviewReply(int Key, string ThreadId, string Body);

/// <summary>"Résoudre" / "Rouvrir" clicked on a discussion thread.</summary>
public sealed record EditorReviewResolve(int Key, string ThreadId, bool Resolved);

/// <summary>The user typed <c>#</c>, <c>!</c> or <c>@</c> and some letters: the box asks for proposals (answer with <see cref="CodeEditor.ShowReferences"/>).</summary>
public sealed record EditorReferenceRequest(int Key, ReferenceKind Kind, string Query);

public sealed partial class CodeEditor
{
    /// <summary>A comment was written in the box under a line or a selection; answer with <see cref="ReviewDone"/>.</summary>
    public event EventHandler<EditorReviewSubmit>? ReviewSubmitted;

    /// <summary>A reply was written in the box of a thread; answer with <see cref="ReviewDone"/>.</summary>
    public event EventHandler<EditorReviewReply>? ReviewReplied;

    /// <summary>A thread was asked to be resolved or opened again; answer with <see cref="ReviewDone"/>.</summary>
    public event EventHandler<EditorReviewResolve>? ReviewResolveRequested;

    /// <summary>A box asks for proposals after a <c>#</c>, <c>!</c> or <c>@</c>; answer with <see cref="ShowReferences"/>.</summary>
    public event EventHandler<EditorReferenceRequest>? ReferenceRequested;

    /// <summary>Gives the box the proposals it asked for (a request that was replaced by a newer one is ignored by the box).</summary>
    public void ShowReferences(int key, IReadOnlyList<ReferenceSuggestion> items) => ShowNativeReferences(key, items);

    /// <summary>A reaction chosen on a comment of a thread: the account's own is added or removed.</summary>
    public event EventHandler<(long CommentId, ReactionKind Kind)>? ReviewReactionToggled;

    /// <summary>"Retirer" clicked on a comment kept for the review (index in the list sent).</summary>
    public event EventHandler<int>? ReviewDraftRemoved;

    /// <summary>
    /// Shows the discussions of the file under the lines they are about and the comments kept for the review (with their index
    /// in the screen's list). <paramref name="enabled"/> false removes all of it; <paramref name="canComment"/> lets the user
    /// start a comment on a line or a selection. <paramref name="revealThread"/> is scrolled to (and flashed) once per
    /// <paramref name="revealToken"/>, as soon as it is shown. <paramref name="references"/>: what the references of the kept
    /// comments and of the previews link to (the threads' rows carry their own).
    /// </summary>
    public void ShowReview(
        string file, bool enabled, bool canComment, bool canDraft, bool canComplete, IReadOnlyList<ThreadRow> threads,
        IReadOnlyList<(int Index, ReviewDraftRow Row)> drafts, Vigo.Core.Markdown.MarkdownReferences? references, string? revealThread = null, int revealToken = 0)
    {
        if (_closed) return;
        _reviewReferences = references;
        SetNativeReview(
            file, enabled && canComment, canDraft, canComplete, enabled ? threads : [], enabled ? drafts : [], revealThread, revealToken);
    }

    /// <summary>Tells the box how the write it was waiting for ended (the box closes, or stays with its text).</summary>
    public void ReviewDone(int key, bool ok) => NativeReviewDone(key, ok);

    private Vigo.Core.Markdown.MarkdownReferences? _reviewReferences;
}

/// <summary>Texts of the inline review (user-facing: localized by the caller).</summary>
public sealed record EditorReviewLabels(
    string Heading, string Bold, string Italic, string Strike, string Code, string Link,
    string BulletList, string NumberList, string Quote, string Suggest,
    string BoldSample, string ItalicSample, string UnderlineSample, string StrikeSample, string CodeSample, string LinkSample,
    string Preview, string NothingToPreview, string Cancel, string Comment, string AddToReview, string Placeholder,
    string Reply, string ReplyPlaceholder, string Resolve, string Reopen, string Show, string Hide, string Comments,
    string Line, string Lines, string CommentOn, string CommentOnSelection, string CommentOnLine,
    string Pending, string RemoveDraft, string OutsideDiff)
{
    /// <summary>The texts in the language of the application (a change of language restarts it).</summary>
    public static EditorReviewLabels Current { get; } = new(
        Heading: Loc.Get("ReviewEditor.Heading"),
        Bold: Loc.Get("ReviewEditor.Bold"),
        Italic: Loc.Get("ReviewEditor.Italic"),
        Strike: Loc.Get("ReviewEditor.Strike"),
        Code: Loc.Get("ReviewEditor.Code"),
        Link: Loc.Get("ReviewEditor.Link"),
        BulletList: Loc.Get("ReviewEditor.BulletList"),
        NumberList: Loc.Get("ReviewEditor.NumberList"),
        Quote: Loc.Get("ReviewEditor.Quote"),
        Suggest: Loc.Get("ReviewEditor.Suggest"),
        BoldSample: Loc.Get("ReviewEditor.BoldSample"),
        ItalicSample: Loc.Get("ReviewEditor.ItalicSample"),
        UnderlineSample: Loc.Get("ReviewEditor.UnderlineSample"),
        StrikeSample: Loc.Get("ReviewEditor.StrikeSample"),
        CodeSample: Loc.Get("ReviewEditor.CodeSample"),
        LinkSample: Loc.Get("ReviewEditor.LinkSample"),
        Preview: Loc.Get("ReviewEditor.Preview"),
        NothingToPreview: Loc.Get("ReviewEditor.NothingToPreview"),
        Cancel: Loc.Get("ReviewEditor.Cancel"),
        Comment: Loc.Get("ReviewEditor.Comment"),
        AddToReview: Loc.Get("ReviewEditor.AddToReview"),
        Placeholder: Loc.Get("ReviewEditor.Placeholder"),
        Reply: Loc.Get("ReviewEditor.Reply"),
        ReplyPlaceholder: Loc.Get("ReviewEditor.ReplyPlaceholder"),
        Resolve: Loc.Get("ReviewEditor.Resolve"),
        Reopen: Loc.Get("ReviewEditor.Reopen"),
        Show: Loc.Get("ReviewEditor.Show"),
        Hide: Loc.Get("ReviewEditor.Hide"),
        Comments: Loc.Get("ReviewEditor.Comments"),
        Line: Loc.Get("ReviewEditor.Line"),
        Lines: Loc.Get("ReviewEditor.Lines"),
        CommentOn: Loc.Get("ReviewEditor.CommentOn"),
        CommentOnSelection: Loc.Get("ReviewEditor.CommentOnSelection"),
        CommentOnLine: Loc.Get("ReviewEditor.CommentOnLine"),
        Pending: Loc.Get("ReviewEditor.Pending"),
        RemoveDraft: Loc.Get("ReviewEditor.RemoveDraft"),
        OutsideDiff: Loc.Get("ReviewEditor.OutsideDiff"));
}
