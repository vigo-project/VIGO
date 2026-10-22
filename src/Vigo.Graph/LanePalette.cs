namespace Vigo.Graph;

/// <summary>
/// Graph lane palette: 10 colors (<c>--b1</c>…<c>--b10</c> of the design system).
/// The lane engine (J2.3) only handles indices; the color is resolved at render time.
/// </summary>
public static class LanePalette
{
    public const int Count = 10;

    /// <summary>Color index (0..Count-1) for a given lane.</summary>
    public static int ColorIndexFor(int lane) => (int)((uint)lane % Count);
}
