using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A repository offered by the "Dépôt" selector: an open tab of the window, or a recent repository.</summary>
/// <param name="Tab">The open tab; null for a recent repository that is not open.</param>
/// <param name="GroupTitle">Heading shown above the item when it opens a group of the list; null otherwise.</param>
public sealed record RepositoryPickerItem(string Name, string Path, string? Branch, string? GroupTitle, RepositoryTabViewModel? Tab, bool IsCurrent)
{
    public bool HasGroupTitle => GroupTitle is not null;

    public bool HasBranch => !string.IsNullOrEmpty(Branch);

    public string Icon => Tab is null ? "history" : "folder";

    /// <summary>Read by the screen reader.</summary>
    public override string ToString() => HasBranch ? $"{Name}, {Branch}, {Path}" : $"{Name}, {Path}";
}

/// <summary>
/// "Dépôt" selector of the toolbar: the repositories open in this window (a click switches to the tab), then the recent
/// ones (a click opens them in a tab), filtered by typing.
/// </summary>
public sealed partial class RepositoryPickerViewModel : ObservableObject
{
    private const int MaxRecent = 30;

    private readonly ShellViewModel _shell;
    private readonly RecentRepositories _recent;

    public RepositoryPickerViewModel(ShellViewModel shell, RecentRepositories recent)
    {
        _shell = shell;
        _recent = recent;
    }

    /// <summary>Repositories matching <see cref="Filter"/> (an ObservableCollection: bound as an ItemsSource).</summary>
    public ObservableCollection<RepositoryPickerItem> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>Text the names and paths are filtered by, without regard to case.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>Reads the open tabs and the recent list again and clears the filter; called when the selector opens.</summary>
    public void Refresh()
    {
        if (Filter.Length > 0) Filter = string.Empty;
        else Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();
        var text = Filter.Trim();
        bool Matches(string name, string path) =>
            text.Length == 0 || name.Contains(text, StringComparison.OrdinalIgnoreCase) || path.Contains(text, StringComparison.OrdinalIgnoreCase);

        var current = _shell.SelectedTab as RepositoryTabViewModel;
        var openPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = true;
        foreach (var tab in _shell.Tabs.OfType<RepositoryTabViewModel>())
        {
            var path = PathOf(tab);
            openPaths.Add(Normalize(path));
            if (!Matches(tab.Info.Name, path)) continue;
            Items.Add(new RepositoryPickerItem(tab.Info.Name, path, tab.Info.HeadName, first ? Loc.Get("RepositoryPicker.Open") : null, tab, ReferenceEquals(tab, current)));
            first = false;
        }

        first = true;
        foreach (var recent in _recent.Items.Where(r => !openPaths.Contains(Normalize(r.Path)) && Matches(r.Name, r.Path)).Take(MaxRecent))
        {
            Items.Add(new RepositoryPickerItem(recent.Name, recent.Path, recent.Branch, first ? Loc.Get("RepositoryPicker.Recent") : null, null, false));
            first = false;
        }
        OnPropertyChanged(nameof(HasItems));
    }

    internal static string PathOf(RepositoryTabViewModel tab) => tab.Info.WorkingDirectory ?? tab.Info.GitDirectory;

    private static string Normalize(string path) => path.TrimEnd('\\', '/');

    /// <summary>Switches to the open tab, or opens the recent repository in a tab.</summary>
    [RelayCommand]
    private async Task ChooseAsync(RepositoryPickerItem? item)
    {
        if (item is null || item.IsCurrent) return;
        if (item.Tab is { } tab && _shell.Tabs.Contains(tab)) _shell.SelectedTab = tab;
        else await _shell.OpenRepositoryAsync(item.Path);
    }

    /// <summary>Enter in the filter: the first repository listed that is not the current one.</summary>
    public Task ChooseFirstAsync() => ChooseAsync(Items.FirstOrDefault(i => !i.IsCurrent));
}
