using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vigo.App.Services;
using Vigo.App.Themes;
using Windows.Foundation;
using Windows.UI;

namespace Vigo.App.Controls.Charts;

/// <summary>Colors of a chart for the current theme (from <see cref="DesignTokens"/>: a brush read from the resources cannot be cast under AOT).</summary>
internal readonly record struct ChartPalette(Color Text, Color Text2, Color Text3, Color Grid, Color Accent, Color AccentSoft, Color Accent2, Color Surface)
{
    public static ChartPalette For(ElementTheme theme) => theme == ElementTheme.Light
        ? new(DesignTokens.Light.Text, DesignTokens.Light.Text2, DesignTokens.Light.Text3, DesignTokens.Light.Stroke, DesignTokens.Light.Accent,
            DesignTokens.Light.AccentSoft2, DesignTokens.Light.AccentText, DesignTokens.Light.Card)
        : new(DesignTokens.Dark.Text, DesignTokens.Dark.Text2, DesignTokens.Dark.Text3, DesignTokens.Dark.Stroke, DesignTokens.Dark.Accent,
            DesignTokens.Dark.AccentSoft2, DesignTokens.Dark.AccentText, DesignTokens.Dark.Card);

    /// <summary>Color of the <paramref name="index"/>th part of a series: the lanes of the graph, in turn.</summary>
    public static Color Series(int index) => DesignTokens.Lanes[index % DesignTokens.Lanes.Length];
}

/// <summary>
/// Base of the Win2D charts of the statistics page (J7.14, ADR-080): a <see cref="CanvasControl"/> drawn off-screen then copied
/// pixel for pixel (see GraphCanvas: the shared XAML surface moves from frame to frame and blurs the text on hover), redrawn when
/// the theme or the size changes, with a tooltip for the hovered part. DirectX resources are freed by <see cref="Release"/> when
/// the tab is closed, not when the control is unloaded (the view is cached across tab switches).
/// </summary>
public abstract partial class ChartControl : UserControl
{
    private readonly CanvasControl _canvas = new();
    private CanvasRenderTarget? _frame;
    private Size _frameSize;
    private string? _tooltip;

    protected ChartControl()
    {
        IsTabStop = false;
        Content = _canvas;
        _canvas.Draw += OnDraw;
        _canvas.PointerMoved += (_, e) => OnPointer(e.GetCurrentPoint(_canvas).Position);
        _canvas.PointerExited += (_, _) => OnPointer(null);
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
        ActualThemeChanged += (_, _) => _canvas.Invalidate();
    }

    /// <summary>Draws the chart on a transparent frame of <paramref name="size"/>.</summary>
    private protected abstract void DrawChart(CanvasDrawingSession ds, Size size, ChartPalette palette);

    /// <summary>The pointer moved over the chart (null: it left); call <see cref="Redraw"/> when the hovered part changed.</summary>
    private protected abstract void OnPointer(Point? position);

    private protected void Redraw() => _canvas.Invalidate();

    /// <summary>Text shown after a pause over the chart; null for none.</summary>
    private protected void SetTooltip(string? text)
    {
        if (text == _tooltip) return;
        _tooltip = text;
        ToolTipService.SetToolTip(this, text);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var size = sender.Size;
        if (size.Width < 2 || size.Height < 2) return;
        if (_frame is not null && (_frame.Device != sender.Device || _frame.Dpi != sender.Dpi || _frameSize != size))
        {
            _frame.Dispose();
            _frame = null;
        }

        var target = _frame ??= new CanvasRenderTarget(sender, (float)size.Width, (float)size.Height);
        _frameSize = size;
        using (var frame = target.CreateDrawingSession())
        {
            frame.Clear(Colors.Transparent);
            DrawChart(frame, size, ChartPalette.For(ActualTheme));
        }
        args.DrawingSession.DrawImage(target, 0, 0, target.Bounds, 1, CanvasImageInterpolation.NearestNeighbor);
    }

    /// <summary>Text in a box; the format is made for one call (small charts, a few labels).</summary>
    private protected static void Text(CanvasDrawingSession ds, string text, Rect box, Color color, float size = 11,
        CanvasHorizontalAlignment horizontal = CanvasHorizontalAlignment.Left, CanvasVerticalAlignment vertical = CanvasVerticalAlignment.Center)
    {
        using var format = new CanvasTextFormat
        {
            FontSize = Appearance.Scaled(size),
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        };
        ds.DrawText(text, box, color, format);
    }

    /// <summary>Releases DirectX resources when the tab is closed for good.</summary>
    public void Release()
    {
        _canvas.RemoveFromVisualTree();
        _frame?.Dispose();
        _frame = null;
    }
}
