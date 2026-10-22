using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.AzureDevOps;

// Filtered, paged list of work items (J6.14): the filters of the Issues section (state, assigned to me, created by me, text) go to a
// WIQL query; the service answers every matching id (up to the requested count), the page is cut here and read in one call.
public sealed partial class AzureDevOpsClient
{
    private static readonly string[] DoneStates = ["Closed", "Done", "Removed"];

    public bool SupportsIssueFilters => true;

    public async Task<GitResult<IssuePage>> ListIssuePageAsync(
        string token, HostRepository repository, IssueQuery query, string? login, int page, CancellationToken cancellationToken = default)
    {
        try
        {
            var projectApi = ProjectApi(repository);
            var from = (Math.Max(page, 1) - 1) * PageSize;
            using var wiql = NewRequest(HttpMethod.Post, $"{projectApi}/wit/wiql?$top={from + PageSize + 1}&{ApiVersion}", token);
            wiql.Content = JsonContent.Create(new AzureWiql(BuildWiql(query)), AzureDevOpsJsonContext.Default.AzureWiql);
            using var wiqlResponse = await http.SendAsync(wiql, cancellationToken);
            if (!Accepted(wiqlResponse)) return await RejectAsync<IssuePage>(wiqlResponse, cancellationToken);

            var refs = await wiqlResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureWiqlResult, cancellationToken);
            var all = (refs?.WorkItems ?? []).Select(w => w.Id).ToArray();
            var ids = all.Skip(from).Take(PageSize).ToArray();
            if (ids.Length == 0) return new Success<IssuePage>(new IssuePage([], false));

            var fields = "System.Id,System.Title,System.CreatedBy,System.ChangedDate,System.WorkItemType";
            using var items = NewRequest(HttpMethod.Get, $"{projectApi}/wit/workitems?ids={string.Join(',', ids)}&fields={fields}&{ApiVersion}", token);
            using var itemsResponse = await http.SendAsync(items, cancellationToken);
            if (!Accepted(itemsResponse)) return await RejectAsync<IssuePage>(itemsResponse, cancellationToken);

            var list = await itemsResponse.Content.ReadFromJsonAsync(AzureDevOpsJsonContext.Default.AzureWorkItemList, cancellationToken);
            var byId = (list?.Value ?? []).ToDictionary(w => w.Id);
            // The query's order (most recently changed first) is the one to keep: the batch call answers in any order.
            IReadOnlyList<Issue> result = [.. ids.Where(byId.ContainsKey).Select(id => ToIssue(repository, byId[id]))];
            return new Success<IssuePage>(new IssuePage(result, all.Length > from + PageSize));
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

    /// <summary>
    /// The WIQL query of a filter: the state, the "me" switches and the search text. Words are searched in the title (a number is
    /// also an id); <c>type:</c>, <c>state:</c>, <c>tag:</c>, <c>area:</c> and <c>iteration:</c> qualifiers (quoted when they hold
    /// spaces) narrow the list, a <c>state:</c> one replacing the state switch.
    /// </summary>
    internal static string BuildWiql(IssueQuery query)
    {
        var where = new List<string> { "[System.TeamProject] = @project" };
        var terms = Tokenize(query.Search).ToList();
        var stateGiven = terms.Any(t => t.Key == "state");
        if (!stateGiven)
        {
            var states = string.Join(", ", DoneStates.Select(Quote));
            if (query.State == IssueStateFilter.Open) where.Add($"[System.State] Not In ({states})");
            else if (query.State == IssueStateFilter.Closed) where.Add($"[System.State] In ({states})");
        }
        if (query.AssignedToMe) where.Add("[System.AssignedTo] = @Me");
        if (query.CreatedByMe) where.Add("[System.CreatedBy] = @Me");

        foreach (var (key, value) in terms)
        {
            switch (key)
            {
                case "type": where.Add($"[System.WorkItemType] = {Quote(value)}"); break;
                case "state": where.Add($"[System.State] = {Quote(value)}"); break;
                case "tag": where.Add($"[System.Tags] Contains {Quote(value)}"); break;
                case "area": where.Add($"[System.AreaPath] Under {Quote(value)}"); break;
                case "iteration": where.Add($"[System.IterationPath] Under {Quote(value)}"); break;
                default:
                    var number = value.TrimStart('#');
                    where.Add(int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                        ? $"([System.Id] = {id} Or [System.Title] Contains {Quote(value)})"
                        : $"[System.Title] Contains {Quote(value)}");
                    break;
            }
        }
        return $"Select [System.Id] From WorkItems Where {string.Join(" And ", where)} Order By [System.ChangedDate] Desc";
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    // "type:Bug tag:\"to do\" crash" -> (type, Bug), (tag, to do), ("", crash). Qualifier names are case-insensitive.
    private static IEnumerable<(string Key, string Value)> Tokenize(string search)
    {
        var i = 0;
        while (i < search.Length)
        {
            if (char.IsWhiteSpace(search[i]))
            {
                i++;
                continue;
            }
            var token = new StringBuilder();
            var quoted = false;
            while (i < search.Length && (quoted || !char.IsWhiteSpace(search[i])))
            {
                if (search[i] == '"') quoted = !quoted;
                else token.Append(search[i]);
                i++;
            }
            var text = token.ToString();
            var colon = text.IndexOf(':');
            if (colon > 0 && text[..colon].ToLowerInvariant() is "type" or "state" or "tag" or "area" or "iteration" && colon < text.Length - 1)
                yield return (text[..colon].ToLowerInvariant(), text[(colon + 1)..]);
            else if (text.Length > 0)
                yield return (string.Empty, text);
        }
    }
}
