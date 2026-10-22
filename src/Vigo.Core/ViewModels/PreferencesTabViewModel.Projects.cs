using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// One project of the "Projets" section, editable in place: a change is applied and saved at once, like the other
/// preferences. Texts: Project.* resources (ADR-075).
/// </summary>
public sealed partial class TabProjectItem : ObservableObject
{
    private readonly Action<TabProjectItem> _changed;
    private bool _syncing;

    internal TabProjectItem(TabProject project, Action<TabProjectItem> changed)
    {
        _changed = changed;
        Id = project.Id;
        Load(project);
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Default color of the project: its index in the graph palette.</summary>
    [ObservableProperty]
    public partial int ColorIndex { get; set; }

    /// <summary>Color chosen by the user (<c>#RRGGBB</c>); empty for the default color.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomColor))]
    public partial string Color { get; set; } = string.Empty;

    public bool IsCustomColor => Color.Length > 0;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>Shows the stored project (changed elsewhere) without saving it again.</summary>
    internal void Load(TabProject project)
    {
        _syncing = true;
        try
        {
            if (Name != project.Name) Name = project.Name;
            if (ColorIndex != project.ColorIndex) ColorIndex = project.ColorIndex;
            if (Color != (project.Color ?? string.Empty)) Color = project.Color ?? string.Empty;
            Summary = project.Repositories.Count switch
            {
                0 => Loc.Get("Project.Summary.None"),
                1 => Loc.Get("Project.Summary.One"),
                var n => Loc.Format("Project.Summary.Many", n),
            };
        }
        finally
        {
            _syncing = false;
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_syncing && e.PropertyName is nameof(Name) or nameof(ColorIndex) or nameof(Color)) _changed(this);
    }

    public override string ToString() => Name;
}

/// <summary>"Projets" section (J7.6, ADR-058): rename, recolor and delete the projects that group repository tabs.</summary>
public sealed partial class PreferencesTabViewModel
{
    private readonly TabProjects? _tabProjects;
    private bool _syncingProjects;

    public bool IsProjects => SelectedSection.Section == PreferencesSection.Projects;

    public ObservableCollection<TabProjectItem> Projects { get; } = [];

    public bool HasNoProject => Projects.Count == 0;

    private void InitializeProjects()
    {
        if (_tabProjects is null) return;
        _tabProjects.Changed += OnTabProjectsChanged;
        SyncProjects();
    }

    private void OnTabProjectsChanged(object? sender, EventArgs e) => SyncProjects();

    private void SyncProjects()
    {
        if (_tabProjects is null) return;
        _syncingProjects = true;
        try
        {
            var stored = _tabProjects.Projects;
            for (var i = Projects.Count - 1; i >= 0; i--)
            {
                if (stored.All(p => p.Id != Projects[i].Id)) Projects.RemoveAt(i);
            }

            foreach (var project in stored)
            {
                if (Projects.FirstOrDefault(p => p.Id == project.Id) is { } existing)
                    existing.Load(project);
                else
                    Projects.Add(new TabProjectItem(project, OnProjectEdited));
            }
        }
        finally
        {
            _syncingProjects = false;
        }

        OnPropertyChanged(nameof(HasNoProject));
    }

    private void OnProjectEdited(TabProjectItem item)
    {
        if (!_syncingProjects) _tabProjects?.Update(item.Id, item.Name, item.ColorIndex, item.Color);
    }

    [RelayCommand]
    private void AddProject()
    {
        if (_tabProjects is null) return;
        var number = _tabProjects.Projects.Count + 1;
        var name = Loc.Format("Project.DefaultName", number);
        while (_tabProjects.IsNameTaken(name)) name = Loc.Format("Project.DefaultName", ++number);
        _tabProjects.Create(name);
    }

    /// <summary>Gives the project a color of its own (<c>#RRGGBB</c>).</summary>
    public void SetProjectColor(TabProjectItem item, string color)
    {
        if (_tabProjects is not null) item.Color = ProjectColors.NormalizeColor(color) ?? string.Empty;
    }

    [RelayCommand]
    private static void ResetProjectColor(TabProjectItem? item)
    {
        if (item is not null) item.Color = string.Empty;
    }

    [RelayCommand]
    private async Task RemoveProjectAsync(TabProjectItem? item)
    {
        if (item is null || _tabProjects is null) return;
        var confirmed = _confirmations is null || (await _confirmations.ConfirmAsync(new ConfirmationRequest(
            Loc.Get("Project.Remove.Title"),
            Loc.Format("Project.Remove.Message", item.Name),
            Loc.Get("Project.Remove.Confirm"), IsDestructive: true), _lifetime.Token)).Confirmed;
        if (confirmed) _tabProjects.Remove(item.Id);
    }

    private void DisposeProjects()
    {
        if (_tabProjects is not null) _tabProjects.Changed -= OnTabProjectsChanged;
    }
}
