using Vigo.Core.Models;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

/// <summary>The creation dialog of a pull request with reviewers, labels and linked issues (J6.9l).</summary>
public sealed class NewPullRequestDialogViewModelTests
{
    private static NewPullRequestDialogViewModel Create(params string[] reviewers) => new(
        "feature/x → main · atlas", "Add x", "feature/x", "main",
        new PullRequestCreateOptions([.. reviewers.Select((name, i) => new PullRequestReviewerOption($"id-{i}", name))]),
        [new Issue(7, "Crash", "ada", DateTimeOffset.UnixEpoch, "https://example.test/7")]);

    [Fact]
    public void Nothing_chosen_gives_a_plain_request()
    {
        var request = Create("Ada").ToRequest();

        Assert.Equal("Add x", request.Title);
        Assert.Empty(request.Reviewers!);
        Assert.Empty(request.Labels!);
        Assert.Empty(request.LinkedIssues!);
    }

    [Fact]
    public void An_empty_title_blocks_confirmation()
    {
        var dialog = Create();

        dialog.Title = "   ";

        Assert.False(dialog.CanConfirm);
        dialog.Title = " Fix ";
        Assert.True(dialog.CanConfirm);
        Assert.Equal("Fix", dialog.ToRequest().Title);
    }

    [Fact]
    public void The_filter_narrows_the_reviewers_but_never_hides_a_selected_one()
    {
        var dialog = Create("Ada Lovelace", "Grace Hopper", "Alan Turing");
        dialog.VisibleReviewers.Single(r => r.Name == "Grace Hopper").IsSelected = true;

        dialog.ReviewerFilter = "ada";

        Assert.Equal(["Ada Lovelace", "Grace Hopper"], dialog.VisibleReviewers.Select(r => r.Name));

        dialog.ReviewerFilter = string.Empty;
        Assert.Equal(3, dialog.VisibleReviewers.Count);
    }

    [Fact]
    public void Selected_reviewers_keep_their_required_flag_in_the_request()
    {
        var dialog = Create("Ada", "Grace");
        var grace = dialog.VisibleReviewers[1];
        Assert.False(grace.CanBeRequired);
        grace.IsSelected = true;
        grace.IsRequired = true;
        Assert.True(grace.CanBeRequired);

        Assert.Equal([new NewPullRequestReviewer("id-1", "Grace", true)], dialog.ToRequest().Reviewers);
    }

    [Theory]
    [InlineData("bug, release", new[] { "bug", "release" })]
    [InlineData(" bug ,, Bug,  ", new[] { "bug" })]
    [InlineData("", new string[0])]
    public void Labels_are_split_on_commas_and_deduplicated(string text, string[] expected) =>
        Assert.Equal(expected, NewPullRequestDialogViewModel.ParseLabels(text));

    [Fact]
    public void Selected_assignees_are_sent_by_identifier()
    {
        var dialog = new NewPullRequestDialogViewModel(
            "d", "t", "h", "b",
            new PullRequestCreateOptions([], Assignees: [new PullRequestReviewerOption("ada", "ada"), new PullRequestReviewerOption("me", "me")]),
            []);
        Assert.True(dialog.HasAssignees);
        Assert.Empty(dialog.ToRequest().Assignees!);

        dialog.Assignees[1].IsSelected = true;

        Assert.Equal(["me"], dialog.ToRequest().Assignees);
    }

    [Fact]
    public void The_filter_narrows_the_assignees_but_never_hides_a_selected_one()
    {
        var dialog = new NewPullRequestDialogViewModel(
            "d", "t", "h", "b",
            new PullRequestCreateOptions([], Assignees:
            [
                new PullRequestReviewerOption("ada", "Ada Lovelace"),
                new PullRequestReviewerOption("grace", "Grace Hopper"),
                new PullRequestReviewerOption("alan", "Alan Turing"),
            ]),
            []);
        Assert.Equal(3, dialog.VisibleAssignees.Count);
        dialog.VisibleAssignees.Single(a => a.Id == "grace").IsSelected = true;

        dialog.AssigneeFilter = "ada";

        Assert.Equal(["Ada Lovelace", "Grace Hopper"], dialog.VisibleAssignees.Select(a => a.Name));
        Assert.Equal(["grace"], dialog.ToRequest().Assignees);

        dialog.AssigneeFilter = string.Empty;
        Assert.Equal(3, dialog.VisibleAssignees.Count);
    }

    [Fact]
    public void Without_assignee_options_the_dialog_offers_none()
    {
        Assert.False(Create("Ada").HasAssignees);
    }

    [Fact]
    public void Without_options_the_dialog_has_no_reviewers()
    {
        var dialog = new NewPullRequestDialogViewModel("d", "t", "h", "b", null, []);

        Assert.False(dialog.HasReviewers);
        Assert.True(dialog.HasNoReviewers);
        Assert.False(dialog.HasIssues);
    }
}
