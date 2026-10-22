using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A possible reviewer, with the check boxes that request them and make them required.</summary>
public sealed partial class ReviewerChoice(string id, string name, bool showRequired = true) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>The "required" box is shown only when the service can make a reviewer mandatory.</summary>
    public bool ShowRequired { get; } = showRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeRequired))]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsRequired { get; set; }

    /// <summary>"Required" only makes sense for a reviewer that is requested.</summary>
    public bool CanBeRequired => IsSelected;
}

/// <summary>A person the new pull request can be assigned to.</summary>
public sealed partial class AssigneeChoice(string id, string name) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>An open issue (work item) that can be linked to the new pull request.</summary>
public sealed partial class LinkedIssueChoice(int number, string title) : ObservableObject
{
    public int Number { get; } = number;

    public string Title { get; } = title;

    public string Label { get; } = $"#{number} {title}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Dialog that creates a pull request with its reviewers, labels and linked issues (J6.9l): title, description and draft state,
/// then a filterable list of reviewers (each one requested, optionally required), free labels (comma separated: the services
/// have no list of them) and the open issues to link.
/// </summary>
/// <remarks>Texts: <c>Pr.New.*</c> resources for the code, <c>x:Uid</c> in the view (ADR-075).</remarks>
public sealed partial class NewPullRequestDialogViewModel : ObservableObject
{
    public NewPullRequestDialogViewModel(
        string description, string title, string head, string baseBranch,
        PullRequestCreateOptions? options, IReadOnlyList<Issue> issues)
    {
        Description = description;
        Title = title;
        Head = head;
        Base = baseBranch;
        IsTruncated = options?.IsTruncated == true;
        SupportsRequiredReviewers = options?.SupportsRequiredReviewers ?? true;
        foreach (var reviewer in options?.Reviewers ?? []) _reviewers.Add(new ReviewerChoice(reviewer.Id, reviewer.Name, SupportsRequiredReviewers));
        foreach (var person in options?.Assignees ?? []) Assignees.Add(new AssigneeChoice(person.Id, person.Name));
        foreach (var issue in issues) Issues.Add(new LinkedIssueChoice(issue.Number, issue.Title));
        Refilter();
        RefilterAssignees();
    }

    private readonly List<ReviewerChoice> _reviewers = [];

    public string DialogTitle { get; } = Loc.Get("Pr.New.Title");

    public string ConfirmText { get; } = Loc.Get("Pr.New.Confirm");

    /// <summary>Line under the title ("feature/x → main · atlas/atlas-api").</summary>
    public string Description { get; }

    public string Head { get; }

    public string Base { get; }

    public string TruncatedText { get; } = Loc.Get("Pr.New.Truncated");

    public bool IsTruncated { get; }

    /// <summary>False on GitHub: a review can be requested but not made mandatory per pull request.</summary>
    public bool SupportsRequiredReviewers { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDraft { get; set; }

    /// <summary>Labels typed by the user, separated by commas.</summary>
    [ObservableProperty]
    public partial string LabelsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReviewerFilter { get; set; } = string.Empty;

    /// <summary>The reviewers matching the filter, plus the requested ones whatever the filter (a selection is never hidden).</summary>
    public ObservableCollection<ReviewerChoice> VisibleReviewers { get; } = [];

    public bool HasReviewers => _reviewers.Count > 0;

    public bool HasNoReviewers => _reviewers.Count == 0;

    /// <summary>The people the pull request can be assigned to; empty when the service has none at creation.</summary>
    public ObservableCollection<AssigneeChoice> Assignees { get; } = [];

    public bool HasAssignees => Assignees.Count > 0;

    [ObservableProperty]
    public partial string AssigneeFilter { get; set; } = string.Empty;

    /// <summary>The assignees matching the filter, plus the selected ones whatever the filter (a selection is never hidden).</summary>
    public ObservableCollection<AssigneeChoice> VisibleAssignees { get; } = [];

    public ObservableCollection<LinkedIssueChoice> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    public bool CanConfirm => Title.Trim().Length > 0;

    partial void OnReviewerFilterChanged(string value) => Refilter();

    partial void OnAssigneeFilterChanged(string value) => RefilterAssignees();

    /// <summary>The request as the dialog stands.</summary>
    public NewPullRequest ToRequest() => new(
        Title.Trim(), Body.Trim(), Head, Base, IsDraft,
        [.. _reviewers.Where(r => r.IsSelected).Select(r => new NewPullRequestReviewer(r.Id, r.Name, r.IsRequired && SupportsRequiredReviewers))],
        ParseLabels(LabelsText),
        [.. Issues.Where(i => i.IsSelected).Select(i => i.Number)],
        [.. Assignees.Where(a => a.IsSelected).Select(a => a.Id)]);

    /// <summary>Labels split on commas, trimmed, without blanks or duplicates (case ignored).</summary>
    internal static IReadOnlyList<string> ParseLabels(string text) =>
        [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    // Rebuilds the visible list in place only when it changed, so the check boxes keep their state and focus while typing.
    private void Refilter()
    {
        var filter = ReviewerFilter.Trim();
        var wanted = _reviewers
            .Where(r => r.IsSelected || filter.Length == 0 || r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (wanted.SequenceEqual(VisibleReviewers)) return;

        VisibleReviewers.Clear();
        foreach (var reviewer in wanted) VisibleReviewers.Add(reviewer);
    }

    private void RefilterAssignees()
    {
        var filter = AssigneeFilter.Trim();
        var wanted = Assignees
            .Where(a => a.IsSelected || filter.Length == 0 || a.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (wanted.SequenceEqual(VisibleAssignees)) return;

        VisibleAssignees.Clear();
        foreach (var assignee in wanted) VisibleAssignees.Add(assignee);
    }
}
