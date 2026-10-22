using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// The tab projects of the active profile (J7.6, ADR-058), shared by every window: loaded from the session at launch
/// and on profile switch, saved with it at exit. All calls are on the UI thread.
/// </summary>
public sealed class TabProjects
{
    private List<TabProject> _projects = [];

    public IReadOnlyList<TabProject> Projects => _projects;

    /// <summary>The projects, their members or their colors changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Replaces every project (session loaded); an unusable entry is dropped, a repository kept in the first project naming it.</summary>
    public void Load(IEnumerable<TabProject>? projects)
    {
        var loaded = new List<TabProject>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects ?? [])
        {
            if (project is null || string.IsNullOrWhiteSpace(project.Id) || string.IsNullOrWhiteSpace(project.Name) || !seenIds.Add(project.Id)) continue;
            var paths = (project.Repositories ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && seenPaths.Add(p)).ToList();
            loaded.Add(project with { ColorIndex = ProjectColors.Normalize(project.ColorIndex), Color = ProjectColors.NormalizeColor(project.Color), Repositories = paths });
        }

        _projects = loaded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public TabProject? Find(string id) => _projects.FirstOrDefault(p => p.Id == id);

    /// <summary>Project holding the repository at <paramref name="path"/>, if any.</summary>
    public TabProject? ProjectOf(string path) =>
        _projects.FirstOrDefault(p => p.Repositories.Contains(path, StringComparer.OrdinalIgnoreCase));

    /// <summary>Creates an empty project; without a color, the first one no project uses yet.</summary>
    public TabProject Create(string name, int? colorIndex = null)
    {
        var color = colorIndex is { } given
            ? ProjectColors.Normalize(given)
            : Enumerable.Range(0, ProjectColors.Count).FirstOrDefault(i => _projects.All(p => p.ColorIndex != i), _projects.Count % ProjectColors.Count);
        var project = new TabProject(Guid.NewGuid().ToString("N"), name.Trim(), color, []);
        _projects = [.. _projects, project];
        Changed?.Invoke(this, EventArgs.Empty);
        return project;
    }

    /// <summary>Puts the repository in the project, taking it out of the one it was in.</summary>
    public void Assign(string path, string projectId)
    {
        if (Find(projectId) is null) return;
        _projects =
        [
            .. _projects.Select(p => p.Id == projectId
                ? p with { Repositories = [.. p.Repositories.Where(r => !SamePath(r, path)), path] }
                : p with { Repositories = [.. p.Repositories.Where(r => !SamePath(r, path))] }),
        ];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the repository out of its project (no effect when it has none).</summary>
    public void Unassign(string path)
    {
        if (ProjectOf(path) is null) return;
        _projects = [.. _projects.Select(p => p with { Repositories = [.. p.Repositories.Where(r => !SamePath(r, path))] })];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Renames and recolors a project; a blank name leaves the name as it is. <paramref name="color"/> (<c>#RRGGBB</c>) is the
    /// chosen color, an empty text going back to the default one; null leaves it as it is.
    /// </summary>
    public void Update(string id, string? name = null, int? colorIndex = null, string? color = null)
    {
        if (Find(id) is not { } project) return;
        var updated = project with
        {
            Name = string.IsNullOrWhiteSpace(name) ? project.Name : name.Trim(),
            ColorIndex = colorIndex is { } index ? ProjectColors.Normalize(index) : project.ColorIndex,
            Color = color is null ? project.Color : ProjectColors.NormalizeColor(color),
        };
        if (updated == project) return;
        _projects = [.. _projects.Select(p => p.Id == id ? updated : p)];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes a project; its tabs stay open, outside any project.</summary>
    public void Remove(string id)
    {
        if (Find(id) is null) return;
        _projects = [.. _projects.Where(p => p.Id != id)];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Name already used by another project (case-insensitive): refused, to keep the menus unambiguous.</summary>
    public bool IsNameTaken(string name, string? exceptId = null) =>
        _projects.Any(p => p.Id != exceptId && string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
