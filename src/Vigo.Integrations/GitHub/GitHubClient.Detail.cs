using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.GitHub;

// Detail of a pull request (J6.9a): description, discussion, reviews, comments on the diff, commits and files. Read only; every
// list follows the "next" link of the service up to a page limit, and the screen says when a list was cut.
public sealed partial class GitHubClient
{
    private const int DetailPageSize = 100;
    private const int MaxPages = 5;
    private const int MaxFilePages = 10;

    public bool SupportsPullRequestDetail => true;

    public async Task<GitResult<PullRequestDetail>> GetPullRequestDetailAsync(
        string token, HostRepository repository, PullRequest pullRequest, CancellationToken cancellationToken = default)
    {
        try
        {
            var api = $"{RepositoryUrl(repository)}";
            var pull = $"{api}/pulls/{pullRequest.Number}";
            var header = Single(token, pull, GitHubJsonContext.Default.GitHubPull, cancellationToken);
            var comments = Pages(token, $"{api}/issues/{pullRequest.Number}/comments?per_page={DetailPageSize}",
                GitHubJsonContext.Default.GitHubCommentArray, MaxPages, cancellationToken);
            var reviews = Pages(token, $"{pull}/reviews?per_page={DetailPageSize}",
                GitHubJsonContext.Default.GitHubReviewArray, MaxPages, cancellationToken);
            var lineComments = Pages(token, $"{pull}/comments?per_page={DetailPageSize}",
                GitHubJsonContext.Default.GitHubReviewCommentArray, MaxPages, cancellationToken);
            var commits = Pages(token, $"{pull}/commits?per_page={DetailPageSize}",
                GitHubJsonContext.Default.GitHubPullCommitArray, MaxPages, cancellationToken);
            var files = Pages(token, $"{pull}/files?per_page={DetailPageSize}",
                GitHubJsonContext.Default.GitHubPullFileArray, MaxFilePages, cancellationToken);
            var threads = ReadThreadsAsync(token, repository, pullRequest, cancellationToken);
            await Task.WhenAll(header, comments, reviews, lineComments, commits, files, threads);

            if (header.Result.Failure is { } failure) return failure;
            foreach (var part in new[] { comments.Result.Failure, reviews.Result.Failure, lineComments.Result.Failure, commits.Result.Failure, files.Result.Failure })
            {
                if (part is { } rejected) return rejected;
            }
            if (header.Result.Body is not { } body) return new Failure(Loc.Get("Hosting.GitHubUnreadable"));

            var timeline = new List<TimelineEntry>();
            timeline.AddRange(comments.Result.Items.Select(c =>
                ToEntry(c)));
            foreach (var review in reviews.Result.Items)
            {
                var verdict = VerdictOf(review.State);
                // A pending review is the user's own draft; a plain "commented" review without text only wraps its line comments.
                if (review.State == "PENDING" || (verdict == ReviewVerdict.Commented && string.IsNullOrWhiteSpace(review.Body))) continue;
                timeline.Add(new TimelineEntry(TimelineKind.Review, review.User?.Login ?? "?", review.Body ?? string.Empty,
                    review.SubmittedAt ?? DateTimeOffset.MinValue, verdict));
            }
            timeline.AddRange(lineComments.Result.Items.Select(c =>
                new TimelineEntry(TimelineKind.ReviewComment, c.User?.Login ?? "?", c.Body ?? string.Empty, c.CreatedAt,
                    Path: c.Path, Line: c.Line ?? c.OriginalLine, DiffHunk: c.DiffHunk)));

            var detail = new PullRequestDetail(
                ToPullRequest(body),
                body.Body ?? string.Empty,
                body.Merged ? PullRequestState.Merged : body.State == "closed" ? PullRequestState.Closed : PullRequestState.Open,
                [.. (body.Labels ?? []).Select(l => l.Name).OfType<string>()],
                [.. timeline.OrderBy(t => t.CreatedAt)],
                [.. commits.Result.Items.Select(c => new PullRequestCommit(
                    c.Sha ?? string.Empty, FirstLine(c.Commit?.Message), c.Author?.Login ?? c.Commit?.Author?.Name ?? "?",
                    c.Commit?.Author?.Date ?? DateTimeOffset.MinValue))],
                [.. files.Result.Items.Select(f => new PullRequestFile(
                    f.Filename ?? string.Empty, f.PreviousFilename, KindOf(f.Status), f.Additions, f.Deletions, f.Patch))],
                IsTruncated: comments.Result.Truncated || reviews.Result.Truncated || lineComments.Result.Truncated
                    || commits.Result.Truncated || files.Result.Truncated,
                NodeId: body.NodeId,
                IsBehind: body.MergeableState == "behind",
                AutoMerge: AutoMergeOf(body.AutoMerge),
                Threads: threads.Result);
            return new Success<PullRequestDetail>(detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new Failure(Loc.Format("Hosting.Unreachable", "GitHub", e.Message), Exception: e);
        }
    }

    internal static TimelineEntry ToEntry(GitHubComment c) =>
        new(TimelineKind.Comment, c.User?.Login ?? "?", c.Body ?? string.Empty, c.CreatedAt, Id: c.Id, Reactions: ReactionsOf(c.Reactions));

    // Only the kinds somebody used, in the order of the picker.
    internal static IReadOnlyList<ReactionCount> ReactionsOf(GitHubReactions? r) => r is null ? [] :
        [.. new[]
        {
            new ReactionCount(ReactionKind.ThumbsUp, r.ThumbsUp), new ReactionCount(ReactionKind.ThumbsDown, r.ThumbsDown),
            new ReactionCount(ReactionKind.Laugh, r.Laugh), new ReactionCount(ReactionKind.Hooray, r.Hooray),
            new ReactionCount(ReactionKind.Confused, r.Confused), new ReactionCount(ReactionKind.Heart, r.Heart),
            new ReactionCount(ReactionKind.Rocket, r.Rocket), new ReactionCount(ReactionKind.Eyes, r.Eyes),
        }.Where(c => c.Count > 0)];

    private static MergeMethod? AutoMergeOf(GitHubAutoMerge? auto) => auto is null ? null : auto.MergeMethod switch
    {
        "squash" => MergeMethod.Squash,
        "rebase" => MergeMethod.Rebase,
        _ => MergeMethod.Merge,
    };

    internal static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        var end = message.IndexOf('\n');
        return (end < 0 ? message : message[..end]).TrimEnd('\r');
    }

    internal static ReviewVerdict VerdictOf(string? state) => state switch
    {
        "APPROVED" => ReviewVerdict.Approved,
        "CHANGES_REQUESTED" => ReviewVerdict.ChangesRequested,
        "DISMISSED" => ReviewVerdict.Dismissed,
        "COMMENTED" => ReviewVerdict.Commented,
        _ => ReviewVerdict.None,
    };

    internal static FileChangeKind KindOf(string? status) => status switch
    {
        "added" => FileChangeKind.Added,
        "removed" => FileChangeKind.Deleted,
        "renamed" => FileChangeKind.Renamed,
        "copied" => FileChangeKind.Copied,
        _ => FileChangeKind.Modified,
    };

    private readonly record struct Read<T>(T? Body, GitResult<PullRequestDetail>? Failure);

    private readonly record struct ReadPages<T>(T[] Items, bool Truncated, GitResult<PullRequestDetail>? Failure);

    private async Task<Read<T>> Single<T>(string token, string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, url, token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return new Read<T>(default, await RejectAsync<PullRequestDetail>(response, cancellationToken));
        return new Read<T>(await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken), null);
    }

    // A list read page after page by the "next" link, at most <paramref name="maxPages"/> pages.
    private async Task<ReadPages<T>> Pages<T>(
        string token, string firstUrl, JsonTypeInfo<T[]> typeInfo, int maxPages, CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? url = firstUrl;
        for (var page = 0; url is not null; page++)
        {
            if (page == maxPages) return new ReadPages<T>([.. items], true, null);
            using var request = NewRequest(HttpMethod.Get, url, token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return new ReadPages<T>([], false, await RejectAsync<PullRequestDetail>(response, cancellationToken));
            items.AddRange(await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken) ?? []);
            url = NextLink(response);
        }
        return new ReadPages<T>([.. items], false, null);
    }

    // `Link: <https://…?page=2>; rel="next", <https://…?page=9>; rel="last"`.
    internal static string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in values.SelectMany(v => v.Split(',')))
        {
            var pieces = part.Split(';');
            if (pieces.Length < 2 || !pieces.Skip(1).Any(p => p.Trim().Replace(" ", string.Empty) == "rel=\"next\"")) continue;
            var link = pieces[0].Trim();
            if (link.Length > 2 && link[0] == '<' && link[^1] == '>') return link[1..^1];
        }
        return null;
    }
}

internal sealed record GitHubComment(
    [property: JsonPropertyName("user")] GitHubAccount? User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("id")] long Id = 0,
    [property: JsonPropertyName("reactions")] GitHubReactions? Reactions = null);

internal sealed record GitHubReactions(
    [property: JsonPropertyName("+1")] int ThumbsUp = 0,
    [property: JsonPropertyName("-1")] int ThumbsDown = 0,
    [property: JsonPropertyName("laugh")] int Laugh = 0,
    [property: JsonPropertyName("hooray")] int Hooray = 0,
    [property: JsonPropertyName("confused")] int Confused = 0,
    [property: JsonPropertyName("heart")] int Heart = 0,
    [property: JsonPropertyName("rocket")] int Rocket = 0,
    [property: JsonPropertyName("eyes")] int Eyes = 0);

internal sealed record GitHubReviewComment(
    [property: JsonPropertyName("user")] GitHubAccount? User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("original_line")] int? OriginalLine,
    [property: JsonPropertyName("diff_hunk")] string? DiffHunk,
    [property: JsonPropertyName("id")] long Id = 0);

internal sealed record GitHubCommitAuthor(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("date")] DateTimeOffset? Date);

internal sealed record GitHubCommitBody(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("author")] GitHubCommitAuthor? Author);

internal sealed record GitHubPullCommit(
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("commit")] GitHubCommitBody? Commit,
    [property: JsonPropertyName("author")] GitHubAccount? Author);

internal sealed record GitHubPullFile(
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("previous_filename")] string? PreviousFilename,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions,
    [property: JsonPropertyName("patch")] string? Patch);

internal sealed record GitHubLabel(
    [property: JsonPropertyName("name")] string? Name);
