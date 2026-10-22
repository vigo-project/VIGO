using System.Collections.Immutable;
using System.Globalization;
using Vigo.Graph;

namespace Vigo.Core.Models;

/// <summary>
/// Custom colors of the graph palette (<see cref="UserPreferences.LaneColors"/>): one <c>#RRGGBB</c> string per
/// <see cref="LanePalette.Count"/> index, an empty string keeping the default color of the design system.
/// </summary>
public static class LaneColors
{
    /// <summary>Parses <c>#RRGGBB</c> (the <c>#</c> is optional).</summary>
    public static bool TryParse(string? text, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (text is null) return false;
        var hex = text.AsSpan().Trim().TrimStart('#');
        return hex.Length == 6
            && byte.TryParse(hex[..2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out red)
            && byte.TryParse(hex.Slice(2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out green)
            && byte.TryParse(hex[4..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out blue);
    }

    public static string Format(byte red, byte green, byte blue) =>
        string.Create(CultureInfo.InvariantCulture, $"#{red:X2}{green:X2}{blue:X2}");

    /// <summary>
    /// Brings hand-edited colors back to a valid list: <see cref="LanePalette.Count"/> entries, unreadable ones
    /// dropped (default color), empty when nothing is customized.
    /// </summary>
    public static ImmutableArray<string> Normalize(ImmutableArray<string> colors)
    {
        if (colors.IsDefaultOrEmpty) return [];
        var cleaned = new string[LanePalette.Count];
        var any = false;
        for (var i = 0; i < cleaned.Length; i++)
        {
            cleaned[i] = i < colors.Length && TryParse(colors[i], out var r, out var g, out var b) ? Format(r, g, b) : string.Empty;
            any |= cleaned[i].Length > 0;
        }
        if (!any) return [];
        var result = ImmutableArray.Create(cleaned);
        // Same content: keep the instance, so that the record stays equal (no needless save).
        return result.SequenceEqual(colors) ? colors : result;
    }

    /// <summary>Colors with <paramref name="hex"/> (empty: default) at <paramref name="index"/>.</summary>
    public static ImmutableArray<string> With(ImmutableArray<string> colors, int index, string hex)
    {
        var list = new string[LanePalette.Count];
        Array.Fill(list, string.Empty);
        if (!colors.IsDefaultOrEmpty) colors.CopyTo(0, list, 0, Math.Min(colors.Length, list.Length));
        list[index] = hex;
        return Normalize(ImmutableArray.Create(list));
    }
}
