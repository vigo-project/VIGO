namespace Vigo.Core.Models;

/// <summary>Commits made in one UTC month.</summary>
public sealed record MonthCount(int Year, int Month, int Count);

/// <summary>One person (after the mailmap and the grouping of <see cref="CommitStatsBuilder"/>) and what they committed.</summary>
public sealed record Contributor(string Name, string Email, int Commits, DateTimeOffset First, DateTimeOffset Last);

/// <summary>
/// What the "Statistiques" page draws (J7.14, ADR-080): commits per month, contributors, and when people work. Neutral data,
/// without any UI type; the charts take their own series from it.
/// </summary>
/// <param name="CommitsPerMonth">From the first month to the last, months without a commit counted as 0.</param>
/// <param name="Contributors">Most active first.</param>
/// <param name="ByWeekday">Seven counts, Sunday first (<see cref="DayOfWeek"/>), by the author's local time.</param>
/// <param name="ByHour">Twenty-four counts, by the author's local time.</param>
public sealed record RepositoryStats(
    int TotalCommits,
    IReadOnlyList<MonthCount> CommitsPerMonth,
    IReadOnlyList<Contributor> Contributors,
    IReadOnlyList<int> ByWeekday,
    IReadOnlyList<int> ByHour)
{
    /// <summary>A repository without any commit.</summary>
    public static RepositoryStats Empty { get; } = new(0, [], [], new int[7], new int[24]);
}

/// <summary>
/// Aggregates commits one at a time, without keeping them (a repository can have a million): months are UTC, the weekday and
/// the hour are the author's local ones (they say when people work). Contributors are grouped by e-mail address without case,
/// then by name: the same person with two addresses counts once.
/// </summary>
public sealed class CommitStatsBuilder
{
    private sealed class Person(string name, string email)
    {
        public string Name { get; set; } = name;

        public string Email { get; set; } = email;

        public int Commits { get; set; }

        public DateTimeOffset First { get; set; } = DateTimeOffset.MaxValue;

        public DateTimeOffset Last { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly Dictionary<int, int> _months = [];
    private readonly Dictionary<string, Person> _people = new(StringComparer.OrdinalIgnoreCase);
    private readonly int[] _weekdays = new int[7];
    private readonly int[] _hours = new int[24];

    /// <summary>Commits added so far.</summary>
    public int Count { get; private set; }

    /// <param name="when">Author date (the instant).</param>
    /// <param name="localWeekday">Weekday in the author's time zone.</param>
    /// <param name="localHour">Hour (0-23) in the author's time zone.</param>
    public void Add(DateTimeOffset when, string name, string email, DayOfWeek localWeekday, int localHour)
    {
        Count++;
        var utc = when.UtcDateTime;
        var month = utc.Year * 12 + utc.Month - 1;
        _months[month] = _months.GetValueOrDefault(month) + 1;
        _weekdays[(int)localWeekday]++;
        if (localHour is >= 0 and < 24) _hours[localHour]++;

        if (!_people.TryGetValue(email, out var person)) _people[email] = person = new Person(name, email);
        person.Commits++;
        // The log is newest first, but nothing here depends on it: the name of the latest commit wins.
        if (when > person.Last) person.Name = name;
        if (when < person.First) person.First = when;
        if (when > person.Last) person.Last = when;
    }

    public RepositoryStats Build()
    {
        if (Count == 0) return RepositoryStats.Empty;

        List<MonthCount> months = [];
        var first = _months.Keys.Min();
        var last = _months.Keys.Max();
        for (var month = first; month <= last; month++)
            months.Add(new MonthCount(month / 12, month % 12 + 1, _months.GetValueOrDefault(month)));

        // The same name behind several addresses is one person: the address with the most commits stands for them.
        var contributors = _people.Values
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var main = group.OrderByDescending(p => p.Commits).First();
                return new Contributor(main.Name, main.Email, group.Sum(p => p.Commits), group.Min(p => p.First), group.Max(p => p.Last));
            })
            .OrderByDescending(c => c.Commits)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new RepositoryStats(Count, months, contributors, _weekdays.ToArray(), _hours.ToArray());
    }
}
