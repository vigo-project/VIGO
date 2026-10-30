using System.Text.Json;
using Vigo.App.Services;
using Vigo.App.Themes;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.App.Controls;

/// <summary>A comment written in the box under a line or a selection of the diff (J6.11).</summary>
/// <param name="Key">Identifies the box: <see cref="CodeEditor.ReviewDone"/> tells the page how the write ended.</param>
/// <param name="AsDraft">Kept for the review instead of being published at once.</param>
public sealed record EditorReviewSubmit(
    int Key, bool AsDraft, ReviewSide Side, int StartLine, int Line, int StartColumn, int EndColumn, string Body);

/// <summary>A reply typed in the box of a discussion thread.</summary>
public sealed record EditorReviewReply(int Key, string ThreadId, string Body);

/// <summary>"Résoudre" / "Rouvrir" clicked on a discussion thread.</summary>
public sealed record EditorReviewResolve(int Key, string ThreadId, bool Resolved);

/// <summary>The user typed <c>#</c>, <c>!</c> or <c>@</c> and some letters: the page asks for proposals (answer with <see cref="CodeEditor.ShowReferences"/>).</summary>
public sealed record EditorReferenceRequest(int Key, ReferenceKind Kind, string Query);

public sealed partial class CodeEditor
{
    // The review state last sent: the page gets it again once it is ready (a content message sent meanwhile replaces the queue).
    private string? _review;

    /// <summary>A comment was written in the page's box (a line or a selection); answer with <see cref="ReviewDone"/>.</summary>
    public event EventHandler<EditorReviewSubmit>? ReviewSubmitted;

    /// <summary>A reply was written in the box of a thread; answer with <see cref="ReviewDone"/>.</summary>
    public event EventHandler<EditorReviewReply>? ReviewReplied;

    /// <summary>A thread was asked to be resolved or opened again; answer with <see cref="ReviewDone"/>.</summary>
    public event EventHandler<EditorReviewResolve>? ReviewResolveRequested;

    /// <summary>The page asks for proposals after a <c>#</c>, <c>!</c> or <c>@</c>; answer with <see cref="ShowReferences"/>.</summary>
    public event EventHandler<EditorReferenceRequest>? ReferenceRequested;

    /// <summary>Gives the page the proposals it asked for (a request that was replaced by a newer one is ignored by the page).</summary>
    public void ShowReferences(int key, IReadOnlyList<ReferenceSuggestion> items) => Send(w =>
    {
        w.WriteString("type", "refResults");
        w.WriteNumber("key", key);
        w.WriteStartArray("items");
        foreach (var item in items)
        {
            w.WriteStartObject();
            w.WriteString("label", item.Label);
            w.WriteString("detail", item.Detail);
            w.WriteString("insert", item.Insert);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// <summary>A link of a comment was clicked (http / https address; the page never navigates by itself).</summary>
    public event EventHandler<string>? LinkClicked;

    /// <summary>"Retirer" clicked on a comment kept for the review (index in the list sent).</summary>
    public event EventHandler<int>? ReviewDraftRemoved;

    /// <summary>
    /// Shows the discussions of the file under the lines they are about and the comments kept for the review (with their index
    /// in the screen's list). <paramref name="enabled"/> false removes all of it; <paramref name="canComment"/> lets the user
    /// start a comment on a line or a selection. <paramref name="revealThread"/> is scrolled to (and flashed) once per
    /// <paramref name="revealToken"/>, as soon as the page shows it.
    /// </summary>
    public void ShowReview(
        string file, bool enabled, bool canComment, bool canDraft, EditorReviewLabels labels, ReferenceLinks links, IReadOnlyDictionary<string, string> mentions,
        bool canComplete, IReadOnlyList<ThreadRow> threads, IReadOnlyList<(int Index, ReviewDraftRow Row)> drafts,
        string? revealThread = null, int revealToken = 0)
    {
        if (_closed) return;
        SetNativeReview(enabled ? threads : [], revealThread, revealToken);
        var json = Serialize(w =>
        {
            w.WriteString("type", "review");
            w.WriteString("file", file);
            w.WriteBoolean("enabled", enabled);
            w.WriteBoolean("canComment", canComment);
            w.WriteBoolean("canDraft", canDraft);
            WriteReviewLabels(w, labels);
            w.WriteBoolean("canComplete", canComplete);
            // A thread to scroll to; the token tells the page whether it already did it for this request.
            if (revealThread is not null && revealToken > 0)
            {
                w.WriteStartObject("reveal");
                w.WriteString("thread", revealThread);
                w.WriteNumber("token", revealToken);
                w.WriteEndObject();
            }
            w.WriteStartObject("mentions");
            foreach (var (id, name) in mentions) w.WriteString(id, name);
            w.WriteEndObject();
            w.WriteStartObject("links");
            if (links.Issue is { } issue) w.WriteString("issue", issue);
            if (links.Pull is { } pull) w.WriteString("pull", pull);
            if (links.User is { } user) w.WriteString("user", user);
            w.WriteEndObject();
            w.WriteStartArray("threads");
            foreach (var row in threads)
            {
                var thread = row.Thread;
                w.WriteStartObject();
                w.WriteString("id", thread.Id);
                w.WriteNumber("line", thread.Line ?? 0);
                if (thread.StartLine is { } start) w.WriteNumber("startLine", start);
                if (thread.StartColumn is { } startColumn) w.WriteNumber("startColumn", startColumn);
                if (thread.EndColumn is { } endColumn) w.WriteNumber("endColumn", endColumn);
                w.WriteString("side", thread.Side == ReviewSide.Old ? "old" : "new");
                w.WriteBoolean("resolved", thread.IsResolved);
                w.WriteString("status", row.StatusText);
                w.WriteBoolean("canReply", row.CanReview);
                w.WriteStartArray("comments");
                foreach (var comment in row.Comments)
                {
                    w.WriteStartObject();
                    w.WriteString("author", comment.Author);
                    w.WriteString("initials", comment.AuthorInitials);
                    w.WriteString("color", LaneColor(comment.AuthorColorIndex));
                    w.WriteString("date", comment.DateText);
                    w.WriteString("body", comment.Comment.Body);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("drafts");
            foreach (var (index, row) in drafts)
            {
                var draft = row.Comment;
                w.WriteStartObject();
                w.WriteNumber("index", index);
                w.WriteNumber("line", draft.Line);
                if (draft.StartLine is { } start) w.WriteNumber("startLine", start);
                if (draft.StartColumn is { } startColumn) w.WriteNumber("startColumn", startColumn);
                if (draft.EndColumn is { } endColumn) w.WriteNumber("endColumn", endColumn);
                w.WriteString("side", draft.Side == ReviewSide.Old ? "old" : "new");
                w.WriteString("body", draft.Body);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
        _review = json;
        if (_ready) _view!.CoreWebView2.PostWebMessageAsJson(json);
    }

    /// <summary>Tells the page how the write a box was waiting for ended (the box closes, or stays with its text).</summary>
    public void ReviewDone(int key, bool ok) => Send(w =>
    {
        w.WriteString("type", "reviewResult");
        w.WriteNumber("key", key);
        w.WriteBoolean("ok", ok);
    });

    private void ReplayReview()
    {
        if (_review is { } json) _view!.CoreWebView2.PostWebMessageAsJson(json);
    }

    private bool TryHandleReview(string? type, JsonElement message)
    {
        switch (type)
        {
            case "reviewSubmit":
                ReviewSubmitted?.Invoke(this, new EditorReviewSubmit(
                    message.GetProperty("key").GetInt32(),
                    message.GetProperty("mode").GetString() == "draft",
                    message.GetProperty("side").GetString() == "old" ? ReviewSide.Old : ReviewSide.New,
                    message.GetProperty("startLine").GetInt32(),
                    message.GetProperty("line").GetInt32(),
                    message.GetProperty("startColumn").GetInt32(),
                    message.GetProperty("endColumn").GetInt32(),
                    message.GetProperty("body").GetString() ?? string.Empty));
                return true;
            case "reviewReply":
                ReviewReplied?.Invoke(this, new EditorReviewReply(
                    message.GetProperty("key").GetInt32(), message.GetProperty("threadId").GetString() ?? string.Empty,
                    message.GetProperty("body").GetString() ?? string.Empty));
                return true;
            case "reviewResolve":
                ReviewResolveRequested?.Invoke(this, new EditorReviewResolve(
                    message.GetProperty("key").GetInt32(), message.GetProperty("threadId").GetString() ?? string.Empty,
                    message.GetProperty("resolved").GetBoolean()));
                return true;
            case "refSearch":
                ReferenceRequested?.Invoke(this, new EditorReferenceRequest(
                    message.GetProperty("key").GetInt32(),
                    message.GetProperty("kind").GetString() switch { "pull" => ReferenceKind.PullRequest, "person" => ReferenceKind.Person, _ => ReferenceKind.Issue },
                    message.GetProperty("query").GetString() ?? string.Empty));
                return true;
            case "openLink":
                if (message.GetProperty("url").GetString() is { } url) LinkClicked?.Invoke(this, url);
                return true;
            case "reviewRemoveDraft":
                ReviewDraftRemoved?.Invoke(this, message.GetProperty("index").GetInt32());
                return true;
            default:
                return false;
        }
    }

    private static string LaneColor(int index) => Hex(Appearance.Current.Lanes[(int)((uint)index % Appearance.Current.Lanes.Length)]);

    private static void WriteReviewLabels(System.Text.Json.Utf8JsonWriter w, EditorReviewLabels l)
    {
        w.WriteStartObject("labels");
        w.WriteString("heading", l.Heading);
        w.WriteString("bold", l.Bold);
        w.WriteString("italic", l.Italic);
        w.WriteString("underline", l.Underline);
        w.WriteString("strike", l.Strike);
        w.WriteString("code", l.Code);
        w.WriteString("codeBlock", l.CodeBlock);
        w.WriteString("link", l.Link);
        w.WriteString("bulletList", l.BulletList);
        w.WriteString("numberList", l.NumberList);
        w.WriteString("quote", l.Quote);
        w.WriteString("suggest", l.Suggest);
        w.WriteString("boldSample", l.BoldSample);
        w.WriteString("italicSample", l.ItalicSample);
        w.WriteString("underlineSample", l.UnderlineSample);
        w.WriteString("strikeSample", l.StrikeSample);
        w.WriteString("codeSample", l.CodeSample);
        w.WriteString("linkSample", l.LinkSample);
        w.WriteString("write", l.Write);
        w.WriteString("preview", l.Preview);
        w.WriteString("nothingToPreview", l.NothingToPreview);
        w.WriteString("cancel", l.Cancel);
        w.WriteString("comment", l.Comment);
        w.WriteString("addToReview", l.AddToReview);
        w.WriteString("placeholder", l.Placeholder);
        w.WriteString("reply", l.Reply);
        w.WriteString("replyPlaceholder", l.ReplyPlaceholder);
        w.WriteString("resolve", l.Resolve);
        w.WriteString("reopen", l.Reopen);
        w.WriteString("show", l.Show);
        w.WriteString("hide", l.Hide);
        w.WriteString("comments", l.Comments);
        w.WriteString("line", l.Line);
        w.WriteString("lines", l.Lines);
        w.WriteString("commentOn", l.CommentOn);
        w.WriteString("commentOnSelection", l.CommentOnSelection);
        w.WriteString("commentOnLine", l.CommentOnLine);
        w.WriteString("suggestion", l.SuggestionTitle);
        w.WriteString("pending", l.Pending);
        w.WriteString("removeDraft", l.RemoveDraft);
        w.WriteString("outsideDiff", l.OutsideDiff);
        w.WriteString("unknownPerson", l.UnknownPerson);
        w.WriteEndObject();
    }
}

/// <summary>Texts of the inline review (user-facing: localized by the caller).</summary>
public sealed record EditorReviewLabels(
    string Heading, string Bold, string Italic, string Underline, string Strike, string Code, string CodeBlock, string Link,
    string BulletList, string NumberList, string Quote, string Suggest,
    string BoldSample, string ItalicSample, string UnderlineSample, string StrikeSample, string CodeSample, string LinkSample,
    string Write, string Preview, string NothingToPreview, string Cancel, string Comment, string AddToReview, string Placeholder,
    string Reply, string ReplyPlaceholder, string Resolve, string Reopen, string Show, string Hide, string Comments,
    string Line, string Lines, string CommentOn, string CommentOnSelection, string CommentOnLine, string SuggestionTitle,
    string Pending, string RemoveDraft, string OutsideDiff, string UnknownPerson)
{
    /// <summary>The texts in the language of the application (a change of language restarts it).</summary>
    public static EditorReviewLabels Current { get; } = new(
        Heading: Loc.Get("ReviewEditor.Heading"),
        Bold: Loc.Get("ReviewEditor.Bold"),
        Italic: Loc.Get("ReviewEditor.Italic"),
        Underline: Loc.Get("ReviewEditor.Underline"),
        Strike: Loc.Get("ReviewEditor.Strike"),
        Code: Loc.Get("ReviewEditor.Code"),
        CodeBlock: Loc.Get("ReviewEditor.CodeBlock"),
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
        Write: Loc.Get("ReviewEditor.Write"),
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
        SuggestionTitle: Loc.Get("ReviewEditor.SuggestionTitle"),
        Pending: Loc.Get("ReviewEditor.Pending"),
        RemoveDraft: Loc.Get("ReviewEditor.RemoveDraft"),
        OutsideDiff: Loc.Get("ReviewEditor.OutsideDiff"),
        UnknownPerson: Loc.Get("ReviewEditor.UnknownPerson"));
}
