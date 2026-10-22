using CommunityToolkit.Mvvm.ComponentModel;

namespace Vigo.Core.ViewModels;

/// <summary>Title bar tab: welcome or repository.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>Palette index of the project the tab belongs to (J7.6, <see cref="Models.ProjectColors"/>); -1 without project.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    public partial int ProjectColorIndex { get; set; } = -1;

    /// <summary>Color chosen for the project of the tab (<c>#RRGGBB</c>); null for the default color of <see cref="ProjectColorIndex"/>.</summary>
    [ObservableProperty]
    public partial string? ProjectColor { get; set; }

    /// <summary>Name of the project of the tab; null without project.</summary>
    [ObservableProperty]
    public partial string? ProjectName { get; set; }

    public bool HasProject => ProjectColorIndex >= 0;

    /// <summary>Fluent System Icons glyph name (e.g. "home", "folder").</summary>
    public abstract string IconName { get; }

    /// <summary>Services of the window hosting the tab, retargeted when the tab moves to another window.</summary>
    internal TabServices? Services { get; init; }
}
