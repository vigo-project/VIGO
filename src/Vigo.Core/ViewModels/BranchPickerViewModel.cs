using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A branch or tag offered by the "Branche" selector of the toolbar.</summary>
/// <param name="GroupTitle">Heading shown above the item when it opens a group of the list; null otherwise.</param>
public sealed record BranchPickerItem(GitRef Ref, string? GroupTitle, string Icon, string Detail, bool IsCurrent)
{
    public string Name => Ref.Name;

    public bool HasGroupTitle => GroupTitle is not null;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Read by the screen reader.</summary>
    public override string ToString() => HasDetail ? $"{Name}, {Detail}" : Name;
}

/// <summary>
/// "Branche" selector of the toolbar: local branches, remote branches and tags that can be filtered by typing; picking
/// one checks it out like the left panel does (a remote branch goes to the local branch of the same name).
/// </summary>
public sealed partial class BranchPickerViewModel : ObservableObject
{
    private const int MaxItemsPerGroup = 100;

    private readonly GraphViewModel _graph;
    private readonly RefsPanelViewModel _refs;
    private readonly bool _canCheckout;

    public BranchPickerViewModel(GraphViewModel graph, RefsPanelViewModel refs, bool canCheckout)
    {
        _graph = graph;
        _refs = refs;
        _canCheckout = canCheckout;
    }

    /// <summary>Branches and tags matching <see cref="Filter"/> (an ObservableCollection: bound as an ItemsSource).</summary>
    public ObservableCollection<BranchPickerItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>Text the names are filtered by, without regard to case.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>Reads the refs again and clears the filter; called when the selector opens.</summary>
    public void Refresh()
    {
        if (Filter.Length > 0) Filter = string.Empty;
        else Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        if (_graph.Refs is { } refs)
        {
            var text = Filter.Trim();
            bool Matches(GitRef r) => text.Length == 0 || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase);

            var locals = refs.OfKind(GitRefKind.LocalBranch).ToList();
            var localNames = locals.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
            AddGroup(Loc.Get("BranchPicker.Local"), "branch", locals.OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Where(Matches), Detail);
            // A remote branch that already has its local branch is reached through that one.
            AddGroup(Loc.Get("BranchPicker.Remote"), "cloud",
                refs.OfKind(GitRefKind.RemoteBranch).Where(b => !localNames.Contains(LocalNameOf(b))).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Where(Matches), _ => string.Empty);
            AddGroup(Loc.Get("BranchPicker.Tags"), "tag", refs.OfKind(GitRefKind.Tag).OrderByDescending(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(Matches), _ => string.Empty);
        }
        OnPropertyChanged(nameof(HasItems));
    }

    private void AddGroup(string title, string icon, IEnumerable<GitRef> refs, Func<GitRef, string> detail)
    {
        var first = true;
        foreach (var reference in refs.Take(MaxItemsPerGroup))
        {
            Items.Add(new BranchPickerItem(reference, first ? title : null, icon, detail(reference), reference.IsCurrent));
            first = false;
        }
    }

    private static string Detail(GitRef branch)
    {
        var parts = new List<string>();
        if (branch.IsCurrent) parts.Add(Loc.Get("BranchPicker.Current"));
        if (branch.Ahead > 0) parts.Add($"↑{branch.Ahead}");
        if (branch.Behind > 0) parts.Add($"↓{branch.Behind}");
        return string.Join(' ', parts);
    }

    private static string LocalNameOf(GitRef remoteBranch) =>
        remoteBranch.Remote is { } remote && remoteBranch.Name.StartsWith(remote + "/", StringComparison.Ordinal)
            ? remoteBranch.Name[(remote.Length + 1)..]
            : remoteBranch.Name;

    /// <summary>Checks out the branch or tag (nothing for the current branch).</summary>
    [RelayCommand]
    private Task ChooseAsync(BranchPickerItem? item) =>
        item is { IsCurrent: false } && _canCheckout ? _refs.CheckoutRefAsync(item.Ref) : Task.CompletedTask;

    /// <summary>Enter in the filter: the first branch listed.</summary>
    public Task ChooseFirstAsync() => ChooseAsync(Items.FirstOrDefault(i => !i.IsCurrent));
}
