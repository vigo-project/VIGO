using System.Globalization;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Row of the recent repositories list (immutable: the list is rebuilt on every change).</summary>
public sealed class RecentRepositoryItem(RecentRepository repository, TimeProvider time)
{
    public RecentRepository Repository { get; } = repository;

    public string Name => Repository.Name;

    public string Path => Repository.Path;

    public string Branch => Repository.Branch ?? string.Empty;

    public bool IsPinned => Repository.IsPinned;

    /// <summary>First two letters of the name, as in the mockups.</summary>
    public string Initials { get; } = repository.Name[..Math.Min(2, repository.Name.Length)].ToUpperInvariant();

    /// <summary>Lane palette color of the badge, stable for a given name across sessions.</summary>
    public int ColorIndex { get; } = StableColor.IndexFor(repository.Name);

    public string LastOpenedDisplay { get; } = RelativeTime.Format(repository.LastOpened, time.GetUtcNow(), time.LocalTimeZone);

    public bool Matches(string filter) =>
        filter.Length == 0
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Path.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

/// <summary>"2 min ago"-style dates of the welcome screen and of the graph rows (texts and formats: RelativeTime.* resources).</summary>
public static class RelativeTime
{
    // Month and day names follow the language of the texts, not the regional settings of the machine.
    private static CultureInfo TextCulture()
    {
        try
        {
            return CultureInfo.GetCultureInfo(Loc.Get("RelativeTime.Culture"));
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentCulture;
        }
    }

    public static string Format(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo zone)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return Loc.Get("RelativeTime.Now");
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return Loc.Format("RelativeTime.Minutes", (int)elapsed.TotalMinutes);
        }

        // Calendar days in the user's time zone: opened yesterday at 23:00 is "hier" even 2 hours later.
        var local = TimeZoneInfo.ConvertTime(when, zone);
        var days = (TimeZoneInfo.ConvertTime(now, zone).Date - local.Date).Days;
        return days switch
        {
            0 => Loc.Format("RelativeTime.Hours", (int)elapsed.TotalHours),
            1 => Loc.Get("RelativeTime.Yesterday"),
            < 7 => Loc.Format("RelativeTime.Days", days),
            < 30 => Loc.Format("RelativeTime.Weeks", days / 7),
            < 365 => Loc.Format("RelativeTime.Months", days / 30),
            _ => local.ToString(Loc.Get("RelativeTime.DateFormat"), TextCulture()),
        };
    }

    /// <summary>
    /// Date of a graph row: relative during the last week, then "Mon 28 Sep" within the year, then the full date.
    /// </summary>
    public static string FormatCommitDate(DateTimeOffset when, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(when, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        var days = (today.Date - local.Date).Days;
        if (days < 7)
        {
            return Format(when, now, zone);
        }

        return local.Year == today.Year
            ? local.ToString(Loc.Get("RelativeTime.DayFormat"), TextCulture())
            : local.ToString(Loc.Get("RelativeTime.DateFormat"), TextCulture());
    }
}
