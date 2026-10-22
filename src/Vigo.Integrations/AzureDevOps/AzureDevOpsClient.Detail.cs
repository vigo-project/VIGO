using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Detail of a pull request (J6.9f, read only): description, discussion threads, reviewers' votes, commits and changed files.
// Azure DevOps gives no patch and no line counts for a changed file, so the files list has paths and kinds only.
public sealed partial class AzureDevOpsClient
{
    private const int DetailPageSize = 100;
    private const int ChangesPageSize = 500;
    private const int MaxChangePages = 4;

    public bool SupportsPullRequestDetail => true;

    public async Task<GitResult<PullRequestDetail>> GetPullRequestDetailAsync(
        string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken = default)
    {
        try
        {
            var pull = $"{RepositoryApi(repository)}/pullRequests/{pullRequest.Number}";
            var header = Single(token, $"{pull}?{ApiVersion}", AzureDevOpsJsonContext.Default.AzurePull, cancellationToken);
            var threads = Single(token, $"{pull}/threads?{ApiVersion}", AzureDevOpsJsonContext.Default.AzureThreadList, cancellationToken);
            var commits = Single(token, $"{pull}/commits?$top={DetailPageSize}&{ApiVersion}", AzureDevOpsJsonContext.Default.AzureCommitList, cancellationToken);
            var files = ReadFilesAsync(token, pull, cancellationToken);
            await Task.WhenAll(header, threads, commits, files);

            if (header.Result.Failure is { } failure) return failure;
            foreach (var part in new[] { threads.Result.Failure, commits.Result.Failure, files.Result.Failure })
            {
                if (part is { } rejected) return rejected;
            }
            if (header.Result.Body is not { } body) return new Failure(Loc.Get("Hosting.AzureUnreadable"));

            var timeline = new List<TimelineEntry>();
            var fileThreads = new List<ReviewThread>();
            foreach (var thread in threads.Result.Body?.Value ?? [])
            {
                if (thread.IsDeleted == true) continue;
                if (ToReviewThread(thread) is { } fileThread) fileThreads.Add(fileThread);
                var context = thread.ThreadContext;
                var path = context?.FilePath is { Length: > 0 } file ? file.TrimStart('/') : null;
                var line = context?.RightFileStart?.Line ?? context?.LeftFileStart?.Line;
                foreach (var comment in thread.Comments ?? [])
                {
                    // System comments ("X voted", "policy status updated"…) and deleted ones say nothing a reader needs.
                    if (comment.IsDeleted == true || comment.CommentType == "system" || string.IsNullOrWhiteSpace(comment.Content)) continue;
                    var author = comment.Author?.DisplayName ?? "?";
                    timeline.Add(path is null
                        ? new TimelineEntry(TimelineKind.Comment, author, comment.Content, comment.PublishedDate)
                        : new TimelineEntry(TimelineKind.ReviewComment, author, comment.Content, comment.PublishedDate, Path: path, Line: line));
                }
            }

            // Votes carry no date: they close the conversation, as the current state of the review.
            var voteDate = timeline.Count > 0 ? timeline.Max(t => t.CreatedAt) : body.CreationDate;
            foreach (var reviewer in body.Reviewers ?? [])
            {
                if (reviewer.IsContainer == true || VoteOf(reviewer.Vote) is not { } vote) continue;
                timeline.Add(new TimelineEntry(TimelineKind.Review, reviewer.DisplayName ?? "?", vote.Text, voteDate, vote.Verdict));
            }

            var detail = new PullRequestDetail(
                ToPullRequest(repository, body),
                body.Description ?? string.Empty,
                body.Status switch { "completed" => PullRequestState.Merged, "abandoned" => PullRequestState.Closed, _ => PullRequestState.Open },
                [.. (body.Labels ?? []).Where(l => l.Active != false).Select(l => l.Name).OfType<string>()],
                [.. timeline.OrderBy(t => t.CreatedAt)],
                [.. (commits.Result.Body?.Value ?? []).Select(c => new PullRequestCommit(
                    c.CommitId ?? string.Empty, FirstLine(c.Comment) ?? string.Empty, c.Author?.Name ?? "?", c.Author?.Date ?? DateTimeOffset.MinValue))],
                files.Result.Items,
                IsTruncated: files.Result.Truncated || (commits.Result.Body?.Value?.Length ?? 0) >= DetailPageSize,
                Threads: fileThreads);
            return new Success<PullRequestDetail>(detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Unreachable(e);
        }
    }

    // A thread on a file, read only (J6.9h): its comments without the system ones, and whether it was fixed or closed.
    // A thread on the left side only is about a line of the old version.
    internal static ReviewThread? ToReviewThread(AzureThread thread)
    {
        if (thread.Id <= 0 || thread.ThreadContext?.FilePath is not { Length: > 0 } file) return null;
        var comments = (thread.Comments ?? [])
            .Where(c => c.IsDeleted != true && c.CommentType != "system" && !string.IsNullOrWhiteSpace(c.Content))
            .Select(c => new ReviewThreadComment(c.Id, c.Author?.DisplayName ?? "?", c.Content!, c.PublishedDate))
            .ToArray();
        if (comments.Length == 0) return null;

        var context = thread.ThreadContext;
        var onOldSide = context.RightFileStart is null && context.LeftFileStart is not null;
        var start = onOldSide ? context.LeftFileStart : context.RightFileStart;
        var end = (onOldSide ? context.LeftFileEnd : context.RightFileEnd) ?? start;
        var resolved = thread.Status is "fixed" or "closed" or "wontFix" or "byDesign";
        return new ReviewThread(
            thread.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), file.TrimStart('/'), end?.Line,
            onOldSide ? ReviewSide.Old : ReviewSide.New, resolved, IsOutdated: false, comments,
            StartLine: start is not null && end is not null && start.Line != end.Line ? start.Line : null,
            StartColumn: start is { Offset: > 0 } ? start.Offset : null,
            EndColumn: end is { Offset: > 0 } ? end.Offset : null);
    }

    // Votes: 10 approved, 5 approved with suggestions, -5 waiting for the author, -10 rejected; 0 is no vote.
    internal static (ReviewVerdict Verdict, string Text)? VoteOf(int vote) => vote switch
    {
        10 => (ReviewVerdict.Approved, string.Empty),
        5 => (ReviewVerdict.Approved, Loc.Get("Hosting.VoteSuggestions")),
        -5 => (ReviewVerdict.ChangesRequested, Loc.Get("Hosting.VoteWaiting")),
        -10 => (ReviewVerdict.ChangesRequested, Loc.Get("Hosting.VoteRejected")),
        _ => null,
    };

    // "edit", "add", "delete", "rename", or a combination such as "edit, rename".
    internal static FileChangeKind KindOf(string? changeType)
    {
        var type = changeType ?? string.Empty;
        if (type.Contains("delete", StringComparison.OrdinalIgnoreCase)) return FileChangeKind.Deleted;
        if (type.Contains("rename", StringComparison.OrdinalIgnoreCase)) return FileChangeKind.Renamed;
        return type.Contains("add", StringComparison.OrdinalIgnoreCase) ? FileChangeKind.Added : FileChangeKind.Modified;
    }

    private readonly record struct Read<T>(T? Body, GitResult<PullRequestDetail>? Failure);

    private readonly record struct ReadFiles(IReadOnlyList<PullRequestFile> Items, bool Truncated, GitResult<PullRequestDetail>? Failure);

    private async Task<Read<T>> Single<T>(string token, string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, url, token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!Accepted(response)) return new Read<T>(default, await RejectAsync<PullRequestDetail>(response, cancellationToken));
        return new Read<T>(await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken), null);
    }

    // The files are the changes of the last iteration (push) of the pull request, read page after page.
    private async Task<ReadFiles> ReadFilesAsync(string token, string pull, CancellationToken cancellationToken)
    {
        var iterations = await Single(token, $"{pull}/iterations?{ApiVersion}", AzureDevOpsJsonContext.Default.AzureIterationList, cancellationToken);
        if (iterations.Failure is { } failure) return new ReadFiles([], false, failure);
        var last = (iterations.Body?.Value ?? []).Select(i => i.Id).DefaultIfEmpty(0).Max();
        if (last == 0) return new ReadFiles([], false, null);

        var files = new List<PullRequestFile>();
        for (var page = 0; ; page++)
        {
            if (page == MaxChangePages) return new ReadFiles(files, true, null);
            var url = $"{pull}/iterations/{last}/changes?$top={ChangesPageSize}&$skip={page * ChangesPageSize}&{ApiVersion}";
            var changes = await Single(token, url, AzureDevOpsJsonContext.Default.AzureChangeList, cancellationToken);
            if (changes.Failure is { } rejected) return new ReadFiles([], false, rejected);

            var entries = changes.Body?.ChangeEntries ?? [];
            files.AddRange(entries.Where(e => e.Item?.IsFolder != true && e.Item?.Path is { Length: > 0 }).Select(e =>
            {
                var kind = KindOf(e.ChangeType);
                return new PullRequestFile(e.Item!.Path!.TrimStart('/'), kind == FileChangeKind.Renamed ? e.OriginalPath?.TrimStart('/') : null, kind, 0, 0, null);
            }));
            if (changes.Body?.NextSkip is not > 0) return new ReadFiles(files, false, null);
        }
    }
}

internal sealed record AzureLabel(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("active")] bool? Active);

internal sealed record AzureThreadContextLine(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("offset")] int Offset = 0);

internal sealed record AzureThreadContext(
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("rightFileStart")] AzureThreadContextLine? RightFileStart,
    [property: JsonPropertyName("leftFileStart")] AzureThreadContextLine? LeftFileStart,
    [property: JsonPropertyName("rightFileEnd")] AzureThreadContextLine? RightFileEnd = null,
    [property: JsonPropertyName("leftFileEnd")] AzureThreadContextLine? LeftFileEnd = null);

internal sealed record AzureThreadComment(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("author")] AzureIdentity? Author,
    [property: JsonPropertyName("publishedDate")] DateTimeOffset PublishedDate,
    [property: JsonPropertyName("commentType")] string? CommentType,
    [property: JsonPropertyName("isDeleted")] bool? IsDeleted,
    [property: JsonPropertyName("id")] int Id = 0);

internal sealed record AzureThread(
    [property: JsonPropertyName("comments")] AzureThreadComment[]? Comments,
    [property: JsonPropertyName("threadContext")] AzureThreadContext? ThreadContext,
    [property: JsonPropertyName("isDeleted")] bool? IsDeleted,
    [property: JsonPropertyName("id")] int Id = 0,
    [property: JsonPropertyName("status")] string? Status = null);

internal sealed record AzureThreadList(
    [property: JsonPropertyName("value")] AzureThread[]? Value);

internal sealed record AzureCommitAuthor(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("date")] DateTimeOffset? Date);

internal sealed record AzureCommit(
    [property: JsonPropertyName("commitId")] string? CommitId,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("author")] AzureCommitAuthor? Author);

internal sealed record AzureCommitList(
    [property: JsonPropertyName("value")] AzureCommit[]? Value);

internal sealed record AzureCommitRef(
    [property: JsonPropertyName("commitId")] string? CommitId);

internal sealed record AzureIteration(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("commonRefCommit")] AzureCommitRef? CommonRefCommit = null,
    [property: JsonPropertyName("sourceRefCommit")] AzureCommitRef? SourceRefCommit = null);

internal sealed record AzureIterationList(
    [property: JsonPropertyName("value")] AzureIteration[]? Value);

internal sealed record AzureChangeItem(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("isFolder")] bool? IsFolder);

internal sealed record AzureChange(
    [property: JsonPropertyName("changeType")] string? ChangeType,
    [property: JsonPropertyName("item")] AzureChangeItem? Item,
    [property: JsonPropertyName("originalPath")] string? OriginalPath);

internal sealed record AzureChangeList(
    [property: JsonPropertyName("changeEntries")] AzureChange[]? ChangeEntries,
    [property: JsonPropertyName("nextSkip")] int? NextSkip);
