using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Vigo.App.Controls;

/// <summary>
/// Drag and drop of a branch on another one, from the left panel or a graph badge to a left panel branch or a graph
/// row (mockup: dropMenu). The dragged branch is kept here while the drag runs: drops coming from other applications
/// carry no branch and are refused.
/// </summary>
internal static class RefDragDrop
{
    private const string IconFont = "ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    /// <summary>Branch being dragged; null when no branch drag runs.</summary>
    public static GitRef? Source { get; private set; }

    public static void Start(DataPackage data, GitRef source)
    {
        Source = source;
        // Text for other applications (a text field gets the branch name); VIGO reads Source.
        data.SetText(source.Name);
        data.RequestedOperation = DataPackageOperation.Link;
    }

    public static void End() => Source = null;

    /// <summary>The dragged branch, when <paramref name="target"/> is a local branch it can be dropped on.</summary>
    public static GitRef? SourceFor(GitRef? target) =>
        Source is { } source && target is { Kind: GitRefKind.LocalBranch } && target.CanonicalName != source.CanonicalName ? source : null;

    /// <summary>
    /// The dragged local branch, when <paramref name="target"/> is a remote branch badge: nothing to merge or rebase on it,
    /// only a pull request towards it.
    /// </summary>
    public static GitRef? PullRequestSourceFor(GitRef? target) =>
        Source is { Kind: GitRefKind.LocalBranch } source && target is { Kind: GitRefKind.RemoteBranch } ? source : null;

    /// <summary>Accepts the drag over <paramref name="target"/> with a caption, or refuses it.</summary>
    public static void AcceptOver(DragEventArgs e, GitRef? target)
    {
        var pullRequestOnly = SourceFor(target) is null && PullRequestSourceFor(target) is not null;
        if (SourceFor(target) is null && !pullRequestOnly)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = pullRequestOnly ? Loc.Format("Graph.Drop.PullRequest", target!.Name) : Loc.Format("Graph.Drop.MergeOrRebase", target!.Name);
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    /// <summary>
    /// Shows the menu of a drop (mockup dropMenu: title "src → dst", merge, rebase, cancel) at <paramref name="position"/>
    /// of <paramref name="target"/>. Built in code: the icon font is created from its URI, never read from the resources
    /// (AOT, see CLAUDE.md).
    /// </summary>
    public static void ShowMenu(
        FrameworkElement target, Point position, DropMenu menu, MergeActionsViewModel merge, InteractiveRebaseViewModel? interactive = null,
        HostingPanelViewModel? hosting = null)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuFlyoutItem { Text = menu.Title, IsEnabled = false });
        flyout.Items.Add(new MenuFlyoutSeparator());
        if (menu.Merge is { } plan)
        {
            var item = Item(plan.Label, FluentIcons.Merge);
            item.IsEnabled = plan.Mode != MergeMode.UpToDate;
            item.Click += (_, _) => merge.MergeCommand.Execute(plan);
            flyout.Items.Add(item);
        }
        if (menu.Rebase is { } rebase)
        {
            var item = Item(rebase.Label, FluentIcons.ArrowRouting);
            item.Click += (_, _) => merge.RebaseCommand.Execute(rebase);
            flyout.Items.Add(item);

            // The interactive rebase always replays HEAD: offered when the dragged branch is the checked-out one.
            if (interactive is not null && rebase.Branch.IsCurrent)
            {
                var interactiveItem = Item(rebase.InteractiveLabel, FluentIcons.ArrowRouting);
                interactiveItem.IsEnabled = interactive.CanOpen;
                interactiveItem.Click += (_, _) => _ = interactive.OpenAsync(rebase.Upstream.CanonicalName, rebase.Upstream.Name, rebase.Upstream.Target);
                flyout.Items.Add(interactiveItem);
            }
        }
        // Pull request of the dragged branch towards the one it was dropped on.
        if (hosting is not null && menu.Source is { } source && menu.Target is { } destination && hosting.CanCreatePullRequest(source))
        {
            var item = Item(Loc.Format("Graph.Menu.PullRequestTo", source.Name, destination.Name), FluentIcons.Merge);
            item.Click += (_, _) => _ = hosting.CreatePullRequestAsync(source, destination);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var cancel = Item(Loc.Get("Common.Cancel"), FluentIcons.Dismiss);
        cancel.KeyboardAcceleratorTextOverride = Loc.Get("Graph.Drop.EscapeKey");
        flyout.Items.Add(cancel);
        flyout.ShowAt(target, new FlyoutShowOptions { Position = position });
    }

    private static MenuFlyoutItem Item(string text, string glyph) => new()
    {
        Text = text,
        Icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily(IconFont) },
    };
}
