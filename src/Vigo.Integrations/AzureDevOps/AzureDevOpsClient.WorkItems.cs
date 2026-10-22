using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Work item screen (J6.14, read only): every field the process template gives, grouped by what it says (planning, people, dates,
// details, custom fields), the rich text fields as Markdown, the links (hierarchy, related items, pull requests, commits, branches,
// builds), the attachments, then the comments and the changes in one timeline. The item itself is required; the type (colors and
// state categories), the comments, the changes and the titles of the linked work items are read in parallel and only enrich it.
public sealed partial class AzureDevOpsClient
{
    private const int WorkItemPageSize = 200;
    private const int MaxLinkedTitles = 100;
    private const string CommentsApiVersion = "api-version=7.1-preview.4";

    public bool SupportsWorkItemDetail => true;

    public async Task<GitResult<WorkItemDetail>> GetWorkItemDetailAsync(
        string token, HostRepository repository, Issue workItem, CancellationToken cancellationToken = default)
    {
        try
        {
            var projectApi = ProjectApi(repository);
            using var request = NewRequest(HttpMethod.Get, $"{projectApi}/wit/workitems/{workItem.Number}?$expand=all&{ApiVersion}", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<WorkItemDetail>(response, cancellationToken);

            var item = await response.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureWorkItemFull, cancellationToken);
            if (item?.Fields is null) return new Failure(Loc.Get("Hosting.AzureUnreadable"));

            var fields = new WorkItemFieldBag(item.Fields);
            var type = fields.Text("System.WorkItemType") ?? workItem.Type;
            var relations = item.Relations ?? [];
            var linkedIds = relations.Select(r => WorkItemIdOf(r.Url)).OfType<int>().Distinct().Take(MaxLinkedTitles).ToArray();

            var typeInfo = type is null
                ? Task.FromResult<AzureWorkItemType?>(null)
                : OptionalAsync(token, $"{projectApi}/wit/workitemtypes/{Uri.EscapeDataString(type)}?{ApiVersion}", AzureDevOpsJsonContext.Default.AzureWorkItemType, cancellationToken);
            var comments = OptionalAsync(token, $"{projectApi}/wit/workItems/{workItem.Number}/comments?$top={WorkItemPageSize}&order=asc&{CommentsApiVersion}",
                AzureDevOpsJsonContext.Default.AzureWorkItemCommentList, cancellationToken);
            var updates = OptionalAsync(token, $"{projectApi}/wit/workItems/{workItem.Number}/updates?$top={WorkItemPageSize}&{ApiVersion}",
                AzureDevOpsJsonContext.Default.AzureWorkItemUpdateList, cancellationToken);
            var linked = linkedIds.Length == 0
                ? Task.FromResult<AzureWorkItemList?>(null)
                : OptionalAsync(token, $"{projectApi}/wit/workitems?ids={string.Join(',', linkedIds)}&fields=System.Title,System.WorkItemType,System.State&{ApiVersion}",
                    AzureDevOpsJsonContext.Default.AzureWorkItemList, cancellationToken);
            var pullTasks = relations.Select(PullRefOf).OfType<(string Project, string Repository, int Number)>().DistinctBy(p => p.Number).Take(MaxLinkedTitles)
                .Select(async p => (p.Number, Pull: await OptionalAsync(token,
                    $"https://{repository.Host}/{Uri.EscapeDataString(repository.Owner.Split('/')[0])}/{Uri.EscapeDataString(p.Project)}/_apis/git/repositories/{Uri.EscapeDataString(p.Repository)}/pullrequests/{p.Number}?{ApiVersion}",
                    AzureDevOpsJsonContext.Default.AzurePull, cancellationToken))).ToArray();
            await Task.WhenAll(typeInfo, comments, updates, linked, Task.WhenAll(pullTasks));
            var pulls = pullTasks.Select(t => t.Result).Where(p => p.Pull is not null).ToDictionary(p => p.Number, p => p.Pull);

            var titles = (linked.Result?.Value ?? []).ToDictionary(w => w.Id, w => w.Fields);
            var zone = (time ?? TimeProvider.System).LocalTimeZone;
            return new Success<WorkItemDetail>(BuildWorkItemDetail(
                repository, workItem, fields, relations, titles, typeInfo.Result, comments.Result, updates.Result, zone, pulls));
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

    // An extra the item can do without: a refused or failed call leaves the screen without it (cancellation still propagates).
    private async Task<T?> OptionalAsync<T>(string token, string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) where T : class
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, url, token);
            using var response = await http.SendAsync(request, cancellationToken);
            return Accepted(response) ? await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static WorkItemDetail BuildWorkItemDetail(
        HostRepository repository, Issue listed, WorkItemFieldBag fields, AzureWorkItemRelation[] relations,
        Dictionary<int, AzureWorkItemFields?> titles, AzureWorkItemType? typeInfo, AzureWorkItemCommentList? comments,
        AzureWorkItemUpdateList? updates, TimeZoneInfo zone, Dictionary<int, AzurePull?> pulls)
    {
        var type = fields.Text("System.WorkItemType") ?? listed.Type;
        var state = fields.Text("System.State") ?? string.Empty;
        var summary = new Issue(
            listed.Number, fields.Text("System.Title") ?? listed.Title, fields.Person("System.CreatedBy") ?? listed.Author,
            fields.Date("System.ChangedDate") ?? listed.UpdatedAt, listed.Url, type);

        var sections = BuildSections(fields, zone);
        var texts = BuildTexts(fields);
        var timeline = new List<TimelineEntry>();
        foreach (var c in comments?.Comments ?? [])
        {
            if (string.IsNullOrWhiteSpace(c.Text)) continue;
            timeline.Add(new TimelineEntry(TimelineKind.Comment, c.CreatedBy?.DisplayName ?? "?", HtmlToMarkdown.ConvertIfHtml(c.Text), c.CreatedDate, Id: c.Id));
        }
        foreach (var u in updates?.Value ?? []) timeline.AddRange(UpdateEntries(repository, u, titles));

        var tags = (fields.Text("System.Tags") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var links = new List<WorkItemLink>();
        var attachments = new List<WorkItemAttachment>();
        foreach (var relation in relations)
        {
            if (relation.Rel == "AttachedFile")
                attachments.Add(new WorkItemAttachment(relation.Attributes?.Name ?? relation.Url, relation.Attributes?.ResourceSize ?? 0, relation.Url));
            else if (ToLink(repository, relation, titles, pulls) is { } link) links.Add(link);
        }

        var commentCount = (int?)fields.Number("System.CommentCount") ?? comments?.Comments?.Length ?? 0;
        var truncated = (comments?.Comments?.Length ?? 0) >= WorkItemPageSize || (updates?.Value?.Length ?? 0) >= WorkItemPageSize;
        return new WorkItemDetail(
            summary, state, StateKindOf(state, typeInfo), typeInfo?.Color, fields.Person("System.AssignedTo"), tags, sections, texts,
            [.. links.OrderBy(l => l.Kind).ThenBy(l => l.WorkItemId ?? int.MaxValue)], attachments,
            [.. timeline.OrderBy(t => t.CreatedAt)], commentCount, truncated);
    }

    // --- Fields ------------------------------------------------------------------------------------------------------------

    private enum FieldKind
    {
        Text,
        Number,
        Hours,
        Date,
        Person,
        Path,
    }

    private sealed record KnownField(string Reference, string Name, string Section, FieldKind Kind);

    private static readonly KnownField[] KnownFields =
    [
        new("System.AreaPath", "AreaPath", "Planning", FieldKind.Path),
        new("System.IterationPath", "IterationPath", "Planning", FieldKind.Path),
        new("Microsoft.VSTS.Common.Priority", "Priority", "Planning", FieldKind.Number),
        new("Microsoft.VSTS.Common.Severity", "Severity", "Planning", FieldKind.Text),
        new("System.BoardColumn", "BoardColumn", "Planning", FieldKind.Text),
        new("Microsoft.VSTS.Scheduling.StoryPoints", "StoryPoints", "Planning", FieldKind.Number),
        new("Microsoft.VSTS.Scheduling.Effort", "Effort", "Planning", FieldKind.Number),
        new("Microsoft.VSTS.Scheduling.Size", "Size", "Planning", FieldKind.Number),
        new("Microsoft.VSTS.Scheduling.OriginalEstimate", "OriginalEstimate", "Planning", FieldKind.Hours),
        new("Microsoft.VSTS.Scheduling.RemainingWork", "RemainingWork", "Planning", FieldKind.Hours),
        new("Microsoft.VSTS.Scheduling.CompletedWork", "CompletedWork", "Planning", FieldKind.Hours),
        new("Microsoft.VSTS.Scheduling.StartDate", "StartDate", "Planning", FieldKind.Date),
        new("Microsoft.VSTS.Scheduling.FinishDate", "FinishDate", "Planning", FieldKind.Date),
        new("Microsoft.VSTS.Scheduling.TargetDate", "TargetDate", "Planning", FieldKind.Date),
        new("Microsoft.VSTS.Scheduling.DueDate", "DueDate", "Planning", FieldKind.Date),
        new("Microsoft.VSTS.Common.Risk", "Risk", "Planning", FieldKind.Text),
        new("Microsoft.VSTS.Common.ValueArea", "ValueArea", "Planning", FieldKind.Text),
        new("Microsoft.VSTS.Common.BusinessValue", "BusinessValue", "Planning", FieldKind.Number),
        new("Microsoft.VSTS.Common.TimeCriticality", "TimeCriticality", "Planning", FieldKind.Number),
        new("Microsoft.VSTS.Common.Activity", "Activity", "Planning", FieldKind.Text),

        new("System.AssignedTo", "AssignedTo", "People", FieldKind.Person),
        new("System.CreatedBy", "CreatedBy", "People", FieldKind.Person),
        new("System.ChangedBy", "ChangedBy", "People", FieldKind.Person),
        new("Microsoft.VSTS.Common.ActivatedBy", "ActivatedBy", "People", FieldKind.Person),
        new("Microsoft.VSTS.Common.ResolvedBy", "ResolvedBy", "People", FieldKind.Person),
        new("Microsoft.VSTS.Common.ClosedBy", "ClosedBy", "People", FieldKind.Person),

        new("System.CreatedDate", "Created", "Dates", FieldKind.Date),
        new("System.ChangedDate", "Changed", "Dates", FieldKind.Date),
        new("Microsoft.VSTS.Common.StateChangeDate", "StateChanged", "Dates", FieldKind.Date),
        new("Microsoft.VSTS.Common.ActivatedDate", "Activated", "Dates", FieldKind.Date),
        new("Microsoft.VSTS.Common.ResolvedDate", "Resolved", "Dates", FieldKind.Date),
        new("Microsoft.VSTS.Common.ClosedDate", "Closed", "Dates", FieldKind.Date),

        new("System.Reason", "Reason", "Details", FieldKind.Text),
        new("Microsoft.VSTS.Common.ResolvedReason", "ResolvedReason", "Details", FieldKind.Text),
        new("Microsoft.VSTS.Build.FoundIn", "FoundIn", "Details", FieldKind.Text),
        new("Microsoft.VSTS.Build.IntegrationBuild", "IntegrationBuild", "Details", FieldKind.Text),
        new("System.Rev", "Revision", "Details", FieldKind.Number),
    ];

    private static readonly string[] SectionOrder = ["Planning", "People", "Dates", "Details"];

    // Rich text fields, in reading order: resource key suffix and field.
    private static readonly (string Name, string Reference)[] TextFields =
    [
        ("Description", "System.Description"),
        ("ReproSteps", "Microsoft.VSTS.TCM.ReproSteps"),
        ("AcceptanceCriteria", "Microsoft.VSTS.Common.AcceptanceCriteria"),
        ("SystemInfo", "Microsoft.VSTS.TCM.SystemInfo"),
    ];

    // Bookkeeping of the service or already shown elsewhere: never listed as a custom field.
    private static readonly HashSet<string> HiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.Title", "System.WorkItemType", "System.State", "System.Tags", "System.CommentCount", "System.Parent",
        "System.TeamProject", "System.NodeName", "System.AreaId", "System.IterationId", "System.AuthorizedAs", "System.AuthorizedDate",
        "System.RevisedDate", "System.Watermark", "System.PersonId", "System.BoardColumnDone", "System.History", "System.Description",
        "System.RelatedLinkCount", "System.AttachedFileCount", "System.HyperLinkCount", "System.ExternalLinkCount", "System.RemoteLinkCount",
        "Microsoft.VSTS.Common.StackRank", "Microsoft.VSTS.Common.BacklogPriority", "Microsoft.VSTS.TCM.ReproSteps",
        "Microsoft.VSTS.Common.AcceptanceCriteria", "Microsoft.VSTS.TCM.SystemInfo", StepsField, "Microsoft.VSTS.TCM.Parameters",
        "Microsoft.VSTS.TCM.LocalDataSource",
    };

    private static bool IsHiddenField(string reference) =>
        HiddenFields.Contains(reference) || reference.StartsWith("WEF_", StringComparison.Ordinal)
        || reference.StartsWith("System.AreaLevel", StringComparison.Ordinal) || reference.StartsWith("System.IterationLevel", StringComparison.Ordinal)
        || reference.Contains(".Kanban.", StringComparison.Ordinal);

    private static List<WorkItemSection> BuildSections(WorkItemFieldBag fields, TimeZoneInfo zone)
    {
        var sections = new List<WorkItemSection>();
        foreach (var key in SectionOrder)
        {
            var list = new List<WorkItemField>();
            foreach (var known in KnownFields.Where(k => k.Section == key))
            {
                if (fields.Format(known, zone) is { } value) list.Add(new WorkItemField(Loc.Get($"WorkItem.Field.{known.Name}"), value));
            }
            if (list.Count > 0) sections.Add(new WorkItemSection(Loc.Get($"WorkItem.Section.{key}"), list));
        }

        var other = new List<WorkItemField>();
        foreach (var (reference, element) in fields.Unused().Where(f => !IsHiddenField(f.Key)))
        {
            if (WorkItemFieldBag.Describe(element, zone) is not { Length: > 0 } value) continue;
            if (value.Length > 400 || HtmlToMarkdown.LooksLikeHtml(value)) continue;
            other.Add(new WorkItemField(LabelOf(reference), value));
        }
        if (other.Count > 0) sections.Add(new WorkItemSection(Loc.Get("WorkItem.Section.Other"), [.. other.OrderBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase)]));
        return sections;
    }

    private static List<WorkItemText> BuildTexts(WorkItemFieldBag fields)
    {
        var texts = new List<WorkItemText>();
        foreach (var (name, reference) in TextFields)
        {
            if (fields.Text(reference) is { Length: > 0 } html && HtmlToMarkdown.ConvertIfHtml(html) is { Length: > 0 } markdown)
                texts.Add(new WorkItemText(Loc.Get($"WorkItem.Text.{name}"), markdown));
        }
        if (fields.Text(StepsField) is { Length: > 0 } stepsXml)
        {
            var steps = StepsMarkdown(stepsXml);
            if (steps.Length > 0) texts.Add(new WorkItemText(Loc.Get("WorkItem.Text.Steps"), steps));
        }
        // A custom long text field is a text of its own rather than a line of the field list.
        foreach (var (reference, element) in fields.Unused().Where(f => !IsHiddenField(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not { Length: > 0 } value) continue;
            if (value.Length > 400 || HtmlToMarkdown.LooksLikeHtml(value))
                texts.Add(new WorkItemText(LabelOf(reference), HtmlToMarkdown.ConvertIfHtml(value)));
        }
        return texts;
    }

    private const string StepsField = "Microsoft.VSTS.TCM.Steps";

    // The steps of a test case are XML: <steps><step><parameterizedString>action</parameterizedString><parameterizedString>expected</...>,
    // each text being HTML escaped once more. Falls back to the text itself when it is not that XML.
    internal static string StepsMarkdown(string xml)
    {
        try
        {
            var blocks = new List<string>();
            var number = 0;
            foreach (var step in System.Xml.Linq.XDocument.Parse(xml).Descendants("step"))
            {
                var parts = step.Elements("parameterizedString").Select(p => HtmlToMarkdown.ConvertIfHtml(p.Value)).ToArray();
                var action = parts.ElementAtOrDefault(0) ?? string.Empty;
                var expected = parts.ElementAtOrDefault(1) ?? string.Empty;
                if (action.Length == 0 && expected.Length == 0) continue;
                var block = new System.Text.StringBuilder($"**{Loc.Format("WorkItem.Steps.Step", ++number)}**");
                if (action.Length > 0) block.Append("\n\n").Append(action);
                if (expected.Length > 0) block.Append("\n\n*").Append(Loc.Get("WorkItem.Steps.Expected")).Append("* ").Append(expected);
                blocks.Add(block.ToString());
            }
            return string.Join("\n\n---\n\n", blocks);
        }
        catch (System.Xml.XmlException)
        {
            return HtmlToMarkdown.ConvertIfHtml(xml);
        }
    }

    // "Custom.TeamOwner" -> "Team Owner".
    internal static string LabelOf(string reference)
    {
        var name = reference[(reference.LastIndexOf('.') + 1)..].Replace('_', ' ');
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) builder.Append(' ');
            builder.Append(name[i]);
        }
        return builder.ToString();
    }

    private static WorkItemStateKind StateKindOf(string state, AzureWorkItemType? typeInfo)
    {
        var category = typeInfo?.States?.FirstOrDefault(s => string.Equals(s.Name, state, StringComparison.OrdinalIgnoreCase))?.Category;
        return category switch
        {
            "Proposed" => WorkItemStateKind.Proposed,
            "InProgress" => WorkItemStateKind.InProgress,
            "Resolved" => WorkItemStateKind.Resolved,
            "Completed" => WorkItemStateKind.Completed,
            "Removed" => WorkItemStateKind.Removed,
            _ => state.ToLowerInvariant() switch
            {
                "new" or "to do" or "todo" or "proposed" or "open" or "approved" or "design" => WorkItemStateKind.Proposed,
                "resolved" or "in review" or "ready for test" or "ready" => WorkItemStateKind.Resolved,
                "closed" or "done" or "completed" or "finished" => WorkItemStateKind.Completed,
                "removed" or "cut" => WorkItemStateKind.Removed,
                _ => WorkItemStateKind.InProgress,
            },
        };
    }

    // --- Links -------------------------------------------------------------------------------------------------------------

    private static int? WorkItemIdOf(string url)
    {
        const string marker = "/_apis/wit/workItems/";
        var at = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && int.TryParse(url[(at + marker.Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private static WorkItemLinkKind? HierarchyKind(string rel) => rel switch
    {
        "System.LinkTypes.Hierarchy-Reverse" => WorkItemLinkKind.Parent,
        "System.LinkTypes.Hierarchy-Forward" => WorkItemLinkKind.Child,
        "System.LinkTypes.Related" => WorkItemLinkKind.Related,
        "System.LinkTypes.Dependency-Reverse" => WorkItemLinkKind.Predecessor,
        "System.LinkTypes.Dependency-Forward" => WorkItemLinkKind.Successor,
        "System.LinkTypes.Duplicate-Forward" or "System.LinkTypes.Duplicate-Reverse" => WorkItemLinkKind.Duplicate,
        "Microsoft.VSTS.Common.TestedBy-Forward" or "Microsoft.VSTS.Common.TestedBy-Reverse" => WorkItemLinkKind.Tests,
        _ => null,
    };

    private static WorkItemLink? ToLink(
        HostRepository repository, AzureWorkItemRelation relation, Dictionary<int, AzureWorkItemFields?> titles, Dictionary<int, AzurePull?>? pulls = null)
    {
        if (WorkItemIdOf(relation.Url) is { } id)
        {
            titles.TryGetValue(id, out var fields);
            return new WorkItemLink(
                HierarchyKind(relation.Rel) ?? WorkItemLinkKind.Other, fields?.Title ?? $"#{id}", $"https://{repository.Host}/{repository.Owner}/_workitems/edit/{id}",
                id, fields?.WorkItemType, fields?.State, relation.Attributes?.Comment,
                fields?.State is { Length: > 0 } linkedState ? StateKindOf(linkedState, null) : null);
        }
        if (relation.Rel == "Hyperlink")
            return new WorkItemLink(WorkItemLinkKind.Hyperlink, relation.Attributes?.Comment is { Length: > 0 } c ? c : relation.Url, relation.Url);
        if (relation.Rel == "ArtifactLink") return ArtifactLinkOf(repository, relation, pulls);
        return null;
    }

    // Draft wins over active: a draft is still an active pull request for the service.
    private static string? PullStatusText(AzurePull pull) => pull.Status?.ToLowerInvariant() switch
    {
        "active" when pull.IsDraft => Loc.Get("WorkItem.PullStatus.Draft"),
        "active" => Loc.Get("WorkItem.PullStatus.Active"),
        "completed" => Loc.Get("WorkItem.PullStatus.Completed"),
        "abandoned" => Loc.Get("WorkItem.PullStatus.Abandoned"),
        _ => null,
    };

    private static WorkItemStateKind? PullStateKind(AzurePull pull) => pull.Status?.ToLowerInvariant() switch
    {
        "active" when pull.IsDraft => WorkItemStateKind.Proposed,
        "active" => WorkItemStateKind.InProgress,
        "completed" => WorkItemStateKind.Completed,
        "abandoned" => WorkItemStateKind.Removed,
        _ => null,
    };

    // The project, repository and number a pull request artifact link points to; null for any other relation.
    private static (string Project, string Repository, int Number)? PullRefOf(AzureWorkItemRelation relation)
    {
        const string scheme = "vstfs:///";
        if (relation.Rel != "ArtifactLink" || !relation.Url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        return Uri.UnescapeDataString(relation.Url[scheme.Length..]).Split('/', 5) is ["Git", "PullRequestId", var project, var repo, var number]
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? (project, repo, id) : null;
    }

    // vstfs:///Git/PullRequestId/{project}%2F{repository}%2F{id}, .../Git/Commit/..., .../Git/Ref/.../GB{branch}, vstfs:///Build/Build/{id}.
    private static WorkItemLink? ArtifactLinkOf(HostRepository repository, AzureWorkItemRelation relation, Dictionary<int, AzurePull?>? pulls)
    {
        const string scheme = "vstfs:///";
        if (!relation.Url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = Uri.UnescapeDataString(relation.Url[scheme.Length..]).Split('/', 5);
        var web = $"https://{repository.Host}/{repository.Owner}";
        switch (parts)
        {
            case ["Git", "PullRequestId", _, var repo, var number]:
                {
                var pull = int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && pulls is not null && pulls.TryGetValue(id, out var found) ? found : null;
                var title = pull is { Title.Length: > 0 } ? Loc.Format("WorkItem.Link.PullRequestTitled", number, pull.Title) : Loc.Format("WorkItem.Link.PullRequest", number);
                return new WorkItemLink(WorkItemLinkKind.PullRequest, title, $"{web}/_git/{repo}/pullrequest/{number}", State: pull is null ? null : PullStatusText(pull), StateKind: pull is null ? null : PullStateKind(pull));
            }
            case ["Git", "Commit", _, var repo, var sha]:
                return new WorkItemLink(WorkItemLinkKind.Commit, Loc.Format("WorkItem.Link.Commit", sha.Length > 8 ? sha[..8] : sha), $"{web}/_git/{repo}/commit/{sha}");
            case ["Git", "Ref", _, var repo, var reference] when reference.StartsWith("GB", StringComparison.Ordinal):
                return new WorkItemLink(WorkItemLinkKind.Branch, Loc.Format("WorkItem.Link.Branch", reference[2..]), $"{web}/_git/{repo}?version={Uri.EscapeDataString(reference)}");
            case ["Git", "Ref", _, var repo, var reference] when reference.StartsWith("GT", StringComparison.Ordinal):
                return new WorkItemLink(WorkItemLinkKind.Branch, Loc.Format("WorkItem.Link.Tag", reference[2..]), $"{web}/_git/{repo}?version={Uri.EscapeDataString(reference)}");
            case ["Build", "Build", var build]:
                return new WorkItemLink(WorkItemLinkKind.Build, Loc.Format("WorkItem.Link.Build", build), $"{web}/_build/results?buildId={build}");
            default:
                return null;
        }
    }

    // --- Changes -----------------------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset NoDate = new(9000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static IEnumerable<TimelineEntry> UpdateEntries(HostRepository repository, AzureWorkItemUpdate update, Dictionary<int, AzureWorkItemFields?> titles)
    {
        var author = update.RevisedBy?.DisplayName ?? "?";
        var changed = update.Fields?.GetValueOrDefault("System.ChangedDate")?.NewValue;
        var when = changed is { ValueKind: JsonValueKind.String } element && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : update.RevisedDate;
        if (when >= NoDate) yield break;

        if (update.Rev == 1)
        {
            yield return new TimelineEntry(TimelineKind.Event, author, Loc.Get("WorkItem.Event.Created"), when);
            yield break;
        }

        foreach (var (reference, change) in update.Fields ?? [])
        {
            if (ChangeSentence(reference, change) is { } sentence) yield return new TimelineEntry(TimelineKind.Event, author, sentence, when);
        }
        foreach (var added in update.Relations?.Added ?? [])
        {
            if (LinkTitle(repository, added, titles) is { } title) yield return new TimelineEntry(TimelineKind.Event, author, Loc.Format("WorkItem.Event.LinkAdded", title), when);
        }
        foreach (var removed in update.Relations?.Removed ?? [])
        {
            if (LinkTitle(repository, removed, titles) is { } title) yield return new TimelineEntry(TimelineKind.Event, author, Loc.Format("WorkItem.Event.LinkRemoved", title), when);
        }
    }

    private static string? LinkTitle(HostRepository repository, AzureWorkItemRelation relation, Dictionary<int, AzureWorkItemFields?> titles) =>
        relation.Rel == "AttachedFile" ? relation.Attributes?.Name : ToLink(repository, relation, titles)?.Title;

    private static string? ChangeSentence(string reference, AzureFieldChange change)
    {
        var before = ChangeValue(change.OldValue);
        var after = ChangeValue(change.NewValue);
        if (before == after) return null;

        switch (reference)
        {
            case "System.State":
                return before is null ? Loc.Format("WorkItem.Event.StateSet", after ?? string.Empty) : Loc.Format("WorkItem.Event.State", before, after ?? string.Empty);
            case "System.AssignedTo":
                return after is null ? Loc.Get("WorkItem.Event.Unassigned") : Loc.Format("WorkItem.Event.Assigned", after);
            case "System.Title":
                return Loc.Format("WorkItem.Event.Renamed", before ?? string.Empty, after ?? string.Empty);
            case "System.Tags":
                return ChangedField(Loc.Get("WorkItem.Field.Tags"), before, after);
        }
        // Dates, people and bookkeeping are not worth a line; the other known fields (estimates, priority, paths…) are.
        var known = KnownFields.FirstOrDefault(k => k.Reference == reference);
        return known is null or { Kind: FieldKind.Date or FieldKind.Person } or { Name: "Revision" }
            ? null
            : ChangedField(Loc.Get($"WorkItem.Field.{known.Name}"), before, after);
    }

    private static string ChangedField(string label, string? before, string? after) => (before, after) switch
    {
        (null, { } to) => Loc.Format("WorkItem.Event.Set", label, to),
        ({ }, null) => Loc.Format("WorkItem.Event.Cleared", label),
        _ => Loc.Format("WorkItem.Event.Changed", label, before!, after!),
    };

    private static string? ChangeValue(JsonElement? element) =>
        element is { } value && WorkItemFieldBag.Describe(value, TimeZoneInfo.Utc) is { Length: > 0 } text ? text : null;

    /// <summary>Reads the fields of a work item by reference name and remembers which ones were used.</summary>
    private sealed class WorkItemFieldBag(Dictionary<string, JsonElement> fields)
    {
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

        public string? Text(string reference)
        {
            if (!fields.TryGetValue(reference, out var element)) return null;
            _used.Add(reference);
            return element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text ? text : Describe(element, TimeZoneInfo.Utc);
        }

        public double? Number(string reference)
        {
            if (!fields.TryGetValue(reference, out var element)) return null;
            _used.Add(reference);
            return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value : null;
        }

        public DateTimeOffset? Date(string reference)
        {
            if (!fields.TryGetValue(reference, out var element)) return null;
            _used.Add(reference);
            return element.ValueKind == JsonValueKind.String && TryDate(element.GetString(), out var date) ? date : null;
        }

        public string? Person(string reference)
        {
            if (!fields.TryGetValue(reference, out var element)) return null;
            _used.Add(reference);
            return PersonOf(element);
        }

        /// <summary>The field as the screen shows it; null when the item does not have it (it then counts as used).</summary>
        public string? Format(KnownField known, TimeZoneInfo zone)
        {
            if (!fields.TryGetValue(known.Reference, out var element)) return null;
            _used.Add(known.Reference);
            return known.Kind switch
            {
                FieldKind.Hours => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var hours) ? $"{FormatNumber(hours)} h" : null,
                FieldKind.Number => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? FormatNumber(value) : Describe(element, zone),
                FieldKind.Person => PersonOf(element),
                FieldKind.Date => element.ValueKind == JsonValueKind.String && TryDate(element.GetString(), out var date) ? FormatDate(date, zone) : null,
                FieldKind.Path => element.ValueKind == JsonValueKind.String ? element.GetString()?.Replace('\\', '/') : null,
                _ => Describe(element, zone),
            };
        }

        public IEnumerable<KeyValuePair<string, JsonElement>> Unused() => fields.Where(f => !_used.Contains(f.Key));

        public static string FormatNumber(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        public static string? PersonOf(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() is { Length: > 0 } name ? name : null,
            JsonValueKind.Object when element.TryGetProperty("displayName", out var name) && name.GetString() is { Length: > 0 } display => display,
            _ => null,
        };

        public static string? Describe(JsonElement element, TimeZoneInfo zone) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() is { Length: > 0 } text
                ? text.Length >= 20 && text[4] == '-' && text[10] == 'T' && TryDate(text, out var date) ? FormatDate(date, zone) : text.Trim()
                : null,
            JsonValueKind.Number => element.TryGetDouble(out var value) ? FormatNumber(value) : null,
            JsonValueKind.True => Loc.Get("WorkItem.Yes"),
            JsonValueKind.False => Loc.Get("WorkItem.No"),
            JsonValueKind.Object => PersonOf(element),
            _ => null,
        };

        private static bool TryDate(string? text, out DateTimeOffset date)
        {
            date = default;
            return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
        }

        // A date without a time of day (schedule fields) keeps its day whatever the time zone; the others are shown in local time.
        private static string FormatDate(DateTimeOffset date, TimeZoneInfo zone) =>
            date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : TimeZoneInfo.ConvertTime(date, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    // --- Pictures ----------------------------------------------------------------------------------------------------------

    private const long MaxImageBytes = 25 * 1024 * 1024;

    /// <summary>Pictures pasted in a work item are attachments of the organization: they only load with the token.</summary>
    public async Task<GitResult<byte[]>> DownloadImageAsync(string token, string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)))
            return new Failure(Loc.Get("Hosting.NotWritable"));
        try
        {
            using var request = NewRequest(HttpMethod.Get, url, token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!Accepted(response)) return await RejectAsync<byte[]>(response, cancellationToken);
            if (response.Content.Headers.ContentLength > MaxImageBytes) return new Failure(Loc.Get("Hosting.ImageTooLarge"));
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length > MaxImageBytes ? new Failure(Loc.Get("Hosting.ImageTooLarge")) : new Success<byte[]>(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Unreachable(e);
        }
    }
}

internal sealed record AzureWorkItemFull(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("fields")] Dictionary<string, JsonElement>? Fields,
    [property: JsonPropertyName("relations")] AzureWorkItemRelation[]? Relations = null);

internal sealed record AzureRelationAttributes(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("resourceSize")] long? ResourceSize = null);

internal sealed record AzureWorkItemRelation(
    [property: JsonPropertyName("rel")] string Rel,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("attributes")] AzureRelationAttributes? Attributes = null);

internal sealed record AzureWorkItemComment(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("createdBy")] AzureIdentity? CreatedBy,
    [property: JsonPropertyName("createdDate")] DateTimeOffset CreatedDate);

internal sealed record AzureWorkItemCommentList(
    [property: JsonPropertyName("comments")] AzureWorkItemComment[]? Comments);

internal sealed record AzureFieldChange(
    [property: JsonPropertyName("oldValue")] JsonElement? OldValue,
    [property: JsonPropertyName("newValue")] JsonElement? NewValue);

internal sealed record AzureRelationChanges(
    [property: JsonPropertyName("added")] AzureWorkItemRelation[]? Added,
    [property: JsonPropertyName("removed")] AzureWorkItemRelation[]? Removed);

internal sealed record AzureWorkItemUpdate(
    [property: JsonPropertyName("rev")] int Rev,
    [property: JsonPropertyName("revisedBy")] AzureIdentity? RevisedBy,
    [property: JsonPropertyName("revisedDate")] DateTimeOffset RevisedDate,
    [property: JsonPropertyName("fields")] Dictionary<string, AzureFieldChange>? Fields = null,
    [property: JsonPropertyName("relations")] AzureRelationChanges? Relations = null);

internal sealed record AzureWorkItemUpdateList(
    [property: JsonPropertyName("value")] AzureWorkItemUpdate[]? Value);

internal sealed record AzureWorkItemTypeState(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("category")] string? Category);

internal sealed record AzureWorkItemType(
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("states")] AzureWorkItemTypeState[]? States);
