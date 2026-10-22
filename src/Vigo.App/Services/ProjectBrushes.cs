using Microsoft.UI.Xaml.Media;
using Vigo.Core.Models;
using Windows.UI;

namespace Vigo.App.Services;

/// <summary>
/// Brushes of the project colors (J7.6): the color chosen for the project, else the graph palette color of its index
/// (the palette in use, customized or not).
/// </summary>
public static class ProjectBrushes
{
    private static readonly Dictionary<Color, SolidColorBrush> Cache = [];

    /// <summary>Color of a project; null for no project (negative index).</summary>
    public static Color? ColorOf(int index, string? color)
    {
        if (index < 0) return null;
        if (LaneColors.TryParse(color, out var r, out var g, out var b)) return Color.FromArgb(0xFF, r, g, b);
        var lanes = Appearance.Current.Lanes;
        return lanes[ProjectColors.Normalize(index) % lanes.Length];
    }

    /// <summary>Brush of a project color; null for no project (negative index).</summary>
    public static Brush? For(int index, string? color)
    {
        if (ColorOf(index, color) is not { } value) return null;
        if (!Cache.TryGetValue(value, out var brush)) Cache[value] = brush = new SolidColorBrush(value);
        return brush;
    }
}
