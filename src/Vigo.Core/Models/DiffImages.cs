namespace Vigo.Core.Models;

/// <summary>How the two versions of an image are compared (J5.7, ADR-043).</summary>
public enum ImageCompareMode
{
    /// <summary>Before and after next to each other.</summary>
    SideBySide,

    /// <summary>After drawn over before, with an adjustable opacity.</summary>
    Overlay,

    /// <summary>After on the left of a movable divider, before on its right.</summary>
    Slider,
}

/// <summary>Both versions of an image file as encoded bytes; null when that version has no file (added or deleted).</summary>
public sealed record DiffImages(string Path, byte[]? Old, byte[]? New)
{
    /// <summary>Extensions the Windows imaging codecs decode and the image diff shows (SVG is text: code diff).</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".jxr", ".avif",
    };

    /// <summary>Largest encoded image the diff reads (50 MB).</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    public static bool IsImagePath(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot >= 0 && Extensions.Contains(path[dot..]);
    }
}
