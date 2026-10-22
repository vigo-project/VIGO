using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Vigo.Core.Services;

namespace Vigo.App.Services;

/// <summary>Installed font families (DirectWrite through Win2D); a family is fixed-width when "iiii" and "WWWW" measure the same.</summary>
public sealed class SystemFontCatalog : IFontCatalog
{
    private IReadOnlyList<string>? _all;
    private IReadOnlyList<string>? _monospace;

    public IReadOnlyList<string> GetFamilies(bool monospaceOnly)
    {
        // Explicit arrays: a collection expression targeting IReadOnlyList is not AOT-safe in the WinUI project (CsWinRT1032).
        _all ??= CanvasTextFormat.GetSystemFontFamilies().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        return monospaceOnly ? _monospace ??= _all.Where(IsMonospace).ToArray() : _all;
    }

    private static bool IsMonospace(string family)
    {
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            using var format = new CanvasTextFormat { FontFamily = family, FontSize = 20, WordWrapping = CanvasWordWrapping.NoWrap };
            using var narrow = new CanvasTextLayout(device, "iiiiiiii", format, 0, 0);
            using var wide = new CanvasTextLayout(device, "WWWWWWWW", format, 0, 0);
            return Math.Abs(narrow.LayoutBounds.Width - wide.LayoutBounds.Width) < 0.01 && narrow.LayoutBounds.Width > 0;
        }
        catch (Exception)
        {
            // A font DirectWrite cannot load: not offered as a fixed-width one.
            return false;
        }
    }
}
