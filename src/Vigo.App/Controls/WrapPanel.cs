using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Vigo.App.Controls;

/// <summary>
/// Lays its children out left to right and starts a new line when the next one does not fit (a row of buttons in a
/// narrow panel). Collapsed children take no room and no spacing.
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }

    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(availableSize);
        }
        return Arrange(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Arrange(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Arrange(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, maxWidth = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            if (arrange) child.Arrange(new Rect(x, y, Math.Min(size.Width, width), size.Height));
            maxWidth = Math.Max(maxWidth, x + size.Width);
            lineHeight = Math.Max(lineHeight, size.Height);
            x += size.Width + HorizontalSpacing;
        }
        return new Size(maxWidth, y + lineHeight);
    }
}
