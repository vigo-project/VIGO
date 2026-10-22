using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Vigo.App.Services;

/// <summary>
/// Interface font and text size chosen in the preferences, put into the resources before any view exists. The XAML reads
/// them with ThemeResource (resolved when a style or a view loads, a StaticResource is fixed when Styles.xaml loads):
/// <c>VigoFont</c> and the sizes below, whose keys are declared in App.xaml. Changes apply at the next launch.
/// </summary>
internal static class UiText
{
    // Keys declared in App.xaml (VigoFontSize<N>, VigoLineHeight<N>); a new size in the XAML needs its key in both places.
    private static readonly int[] FontSizes = [7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 20, 22, 24, 28];
    private static readonly int[] LineHeights = [13, 14, 16, 18, 20, 21, 22, 28, 36];

    public static void Apply(Application application, string? family, double scale)
    {
        if (family is not null)
        {
            // The font is the first of a list: Segoe UI stays behind it for the glyphs it lacks.
            var font = new FontFamily($"{family}, Segoe UI");
            application.Resources["VigoFont"] = font;
            application.Resources["VigoDisplayFont"] = font;
            application.Resources["ContentControlThemeFontFamily"] = font;
        }

        if (Math.Abs(scale - 1) < 0.001) return;
        // Default size of the controls and of the text without a style (14, 12 for the tab titles).
        application.Resources["ControlContentThemeFontSize"] = Math.Round(14 * scale, 1);
        application.Resources["TabViewItemHeaderFontSize"] = Math.Round(12 * scale, 1);
        foreach (var size in FontSizes) application.Resources[$"VigoFontSize{size}"] = Math.Round(size * scale, 1);
        foreach (var height in LineHeights) application.Resources[$"VigoLineHeight{height}"] = Math.Round(height * scale, 1);
    }
}
