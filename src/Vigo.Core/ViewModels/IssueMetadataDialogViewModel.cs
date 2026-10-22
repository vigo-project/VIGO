using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A label or an assignee the dialog offers, with its check box.</summary>
public sealed partial class MetadataChoice(string name, bool isSelected) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = isSelected;
}

/// <summary>A milestone the dialog offers; a null <see cref="Number"/> is "no milestone".</summary>
public sealed record MilestoneChoice(int? Number, string Title)
{
    public override string ToString() => Title;
}

/// <summary>
/// Dialog of the labels, assignees and milestone of an issue (J6.10d): check boxes for the first two, one choice for the
/// milestone. What is not offered any more (a label deleted on the service, a list cut at the page limit) is kept as long as
/// it stays checked, so confirming never drops something the user did not see.
/// </summary>
/// <remarks>Texts: <c>IssueMeta.*</c> resources for the code, <c>x:Uid</c> in the view (ADR-075).</remarks>
public sealed partial class IssueMetadataDialogViewModel : ObservableObject
{
    public static string NoMilestone => Loc.Get("IssueMeta.NoMilestone");

    public IssueMetadataDialogViewModel(string description, IssueMetadataOptions options, IssueMetadata current, string? currentMilestoneTitle)
    {
        Description = description;
        IsTruncated = options.IsTruncated;

        foreach (var name in Merge(options.Labels, current.Labels)) Labels.Add(new MetadataChoice(name, current.Labels.Contains(name, StringComparer.OrdinalIgnoreCase)));
        foreach (var name in Merge(options.Assignees, current.Assignees)) Assignees.Add(new MetadataChoice(name, current.Assignees.Contains(name, StringComparer.OrdinalIgnoreCase)));

        var none = new MilestoneChoice(null, NoMilestone);
        Milestones.Add(none);
        foreach (var milestone in options.Milestones) Milestones.Add(new MilestoneChoice(milestone.Number, milestone.Title));
        var chosen = current.MilestoneNumber is { } number ? Milestones.FirstOrDefault(m => m.Number == number) : null;
        chosen ??= currentMilestoneTitle is { Length: > 0 } title ? Milestones.FirstOrDefault(m => m.Title == title) : null;
        SelectedMilestone = chosen ?? none;
        InitialResult = Result;
        RefilterLabels();
        RefilterAssignees();
    }

    /// <summary>The choices when the dialog opened (the milestone resolved to its number): what "nothing changed" is compared to.</summary>
    public IssueMetadata InitialResult { get; }

    public string Title { get; } = Loc.Get("IssueMeta.Title");

    public string ConfirmText { get; } = Loc.Get("IssueMeta.Confirm");

    /// <summary>Line under the title ("atlas/atlas-api · #5").</summary>
    public string Description { get; }

    /// <summary>A list was cut at the page limit: the rest is on the hosting service.</summary>
    public bool IsTruncated { get; }

    public string TruncatedText { get; } = Loc.Get("IssueMeta.Truncated");

    public ObservableCollection<MetadataChoice> Labels { get; } = [];

    public bool HasLabels => Labels.Count > 0;

    public ObservableCollection<MetadataChoice> Assignees { get; } = [];

    public bool HasAssignees => Assignees.Count > 0;

    [ObservableProperty]
    public partial string LabelFilter { get; set; } = string.Empty;

    /// <summary>The labels matching the filter, plus the checked ones whatever the filter (a selection is never hidden).</summary>
    public ObservableCollection<MetadataChoice> VisibleLabels { get; } = [];

    [ObservableProperty]
    public partial string AssigneeFilter { get; set; } = string.Empty;

    /// <summary>The assignees matching the filter, plus the checked ones whatever the filter.</summary>
    public ObservableCollection<MetadataChoice> VisibleAssignees { get; } = [];

    public ObservableCollection<MilestoneChoice> Milestones { get; } = [];

    [ObservableProperty]
    public partial MilestoneChoice SelectedMilestone { get; set; }

    /// <summary>The choices as they stand.</summary>
    public IssueMetadata Result => new(
        [.. Labels.Where(l => l.IsSelected).Select(l => l.Name)],
        [.. Assignees.Where(a => a.IsSelected).Select(a => a.Name)],
        SelectedMilestone.Number);

    partial void OnLabelFilterChanged(string value) => RefilterLabels();

    partial void OnAssigneeFilterChanged(string value) => RefilterAssignees();

    private void RefilterLabels() => Refilter(Labels, VisibleLabels, LabelFilter);

    private void RefilterAssignees() => Refilter(Assignees, VisibleAssignees, AssigneeFilter);

    // Rebuilds the visible list in place only when it changed, so the check boxes keep their state and focus while typing.
    private static void Refilter(ObservableCollection<MetadataChoice> all, ObservableCollection<MetadataChoice> visible, string filter)
    {
        filter = filter.Trim();
        var wanted = all
            .Where(c => c.IsSelected || filter.Length == 0 || c.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (wanted.SequenceEqual(visible)) return;

        visible.Clear();
        foreach (var choice in wanted) visible.Add(choice);
    }

    // The offered names, then the ones the issue carries that are not offered.
    private static IEnumerable<string> Merge(IReadOnlyList<string> offered, IReadOnlyList<string> current) =>
        offered.Concat(current.Where(c => !offered.Contains(c, StringComparer.OrdinalIgnoreCase)));
}
