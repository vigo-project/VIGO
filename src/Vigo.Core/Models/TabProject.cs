using Vigo.Core.Services;
using Vigo.Graph;

namespace Vigo.Core.Models;

/// <summary>
/// Named, colored group of repository tabs (J7.6, ADR-058). Repositories are linked by path (working directory, or
/// Git directory when bare); a repository belongs to at most one project.
/// </summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Name">Name shown in menus and preferences.</param>
/// <param name="ColorIndex">Default color: index in the graph palette (<see cref="ProjectColors.Count"/> entries), given by creation order.</param>
/// <param name="Repositories">Paths of the repositories of the project.</param>
/// <param name="Color">Color chosen by the user, <c>#RRGGBB</c>; null keeps the default color of <paramref name="ColorIndex"/>.</param>
public sealed record TabProject(string Id, string Name, int ColorIndex, IReadOnlyList<string> Repositories, string? Color = null);

/// <summary>Default colors of the projects: the graph lane colors, by index; a project can then take any color.</summary>
public static class ProjectColors
{
    public const int Count = LanePalette.Count;

    /// <summary>Index brought into the palette (an out-of-range value in a hand-edited file).</summary>
    public static int Normalize(int index) => ((index % Count) + Count) % Count;

    /// <summary>A chosen color as <c>#RRGGBB</c>; null when absent or unreadable (the default color applies).</summary>
    public static string? NormalizeColor(string? color) =>
        LaneColors.TryParse(color, out var r, out var g, out var b) ? LaneColors.Format(r, g, b) : null;
}
