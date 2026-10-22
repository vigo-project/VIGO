using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Vigo.Core.Models;
using Windows.Foundation;

namespace Vigo.App.Controls.Charts;

/// <summary>
/// Bar chart (commits per weekday, per hour): graduations of round numbers on the left, one bar per point with its label below
/// (every few bars when they are too narrow), the hovered bar highlighted with its tooltip.
/// </summary>
public sealed partial class BarChart : ChartControl
{
    private const double Left = 44;
    private const double Right = 14;
    private const double Top = 10;
    private const double Bottom = 24;

    private ChartSeries _series = ChartSeries.Empty;
    private int _hover = -1;

    public BarChart() => MinHeight = 140;

    public ChartSeries Series
    {
        get => _series;
        set
        {
            _series = value;
            _hover = -1;
            SetTooltip(null);
            Redraw();
        }
    }

    private protected override void OnPointer(Point? position)
    {
        var next = -1;
        var count = _series.Points.Count;
        if (position is { } p && count > 0 && ActualWidth > Left + Right && p.X >= Left && p.X < ActualWidth - Right)
            next = Math.Min(count - 1, (int)((p.X - Left) / ((ActualWidth - Left - Right) / count)));
        if (next == _hover) return;
        _hover = next;
        SetTooltip(next >= 0 ? _series.Points[next].Tooltip : null);
        Redraw();
    }

    private protected override void DrawChart(CanvasDrawingSession ds, Size size, ChartPalette palette)
    {
        var points = _series.Points;
        var width = size.Width - Left - Right;
        var height = size.Height - Top - Bottom;
        if (points.Count == 0 || width <= 0 || height <= 0) return;

        var ticks = ChartScale.NiceTicks(_series.Max);
        var top = ticks[^1];
        foreach (var tick in ticks)
        {
            var y = (float)(Top + height - tick / top * height);
            ds.DrawLine((float)Left, y, (float)(Left + width), y, palette.Grid, 1);
            Text(ds, tick.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), new Rect(0, y - 8, Left - 6, 16), palette.Text3, 11, CanvasHorizontalAlignment.Right);
        }

        var slot = width / points.Count;
        var barWidth = Math.Max(2, slot * 0.7);
        var longest = points.Max(p => p.Label.Length);
        var every = Math.Max(1, (int)Math.Ceiling((longest * 7 + 6) / slot));
        for (var i = 0; i < points.Count; i++)
        {
            var x = Left + slot * i + (slot - barWidth) / 2;
            var barHeight = points[i].Value / top * height;
            var color = i == _hover ? palette.Accent2 : palette.Accent;
            if (barHeight > 0) ds.FillRoundedRectangle(new Rect(x, Top + height - barHeight, barWidth, barHeight), 2, 2, color);
            if (i % every == 0)
                Text(ds, points[i].Label, new Rect(Left + slot * i - 4, size.Height - Bottom + 4, slot + 8, 16), palette.Text3, 11, CanvasHorizontalAlignment.Center);
        }
    }
}
