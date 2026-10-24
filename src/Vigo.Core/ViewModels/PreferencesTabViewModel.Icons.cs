using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>"Icônes" section: every Fluent icon with its name, assignable to a Git profile or to a repository.</summary>
public sealed partial class PreferencesTabViewModel
{
    public bool IsIcons => SelectedSection.Section == PreferencesSection.Icons;

    /// <summary>The other sections share a scrolling page; this one fills the window and scrolls its own grid.</summary>
    public bool IsNotIcons => !IsIcons;

    /// <summary>Repositories opened before, the ones an icon can be assigned to.</summary>
    public RecentRepositories? RecentRepositories { get; init; }

    /// <summary>Sets the tab icon of a repository (path, Fluent icon name; null restores the default) in the window that owns its tab.</summary>
    public Action<string, string?>? AssignRepositoryIcon { get; init; }

    public IReadOnlyList<RecentRepository> KnownRepositories => RecentRepositories?.Items ?? [];
}
