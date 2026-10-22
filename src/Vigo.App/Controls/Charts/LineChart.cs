using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Vigo.Core.Models;
using Windows.Foundation;

namespace Vigo.App.Controls.Charts;

/// <summary>
/// Line chart with an area under it (commits per month): graduations of round numbers on the left, labels of the points that
/// have one (the years) at the bottom, and the point nearest to the pointer marked with its tooltip.
/// </summary>
public sealed partial class LineChart : ChartControl
{
    private const double Left = 44;
    private const double Right = 14;
    private const double Top = 10;
    private const double Bottom = 24;

    private ChartSeries _series = ChartSeries.Empty;
    private int _hover = -1;

    public LineChart() => MinHeight = 140;

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

    private double X(int index, double width) =>
        _series.Points.Count <= 1 ? Left + width / 2 : Left + width * index / (_series.Points.Count - 1);

    private protected override void OnPointer(Point? position)
    {
        var next = -1;
        if (position is { } p && _series.Points.Count > 0 && ActualWidth > Left + Right)
        {
            var width = ActualWidth - Left - Right;
            next = _series.Points.Count == 1 ? 0 : (int)Math.Round((p.X - Left) / width * (_series.Points.Count - 1));
            if (next < 0 || next >= _series.Points.Count || p.X < Left - 8 || p.X > Left + width + 8) next = -1;
        }
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
        double Y(double value) => Top + height - value / top * height;

        foreach (var tick in ticks)
        {
            var y = (float)Y(tick);
            ds.DrawLine((float)Left, y, (float)(Left + width), y, palette.Grid, 1);
            Text(ds, tick.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), new Rect(0, y - 8, Left - 6, 16), palette.Text3, 11, CanvasHorizontalAlignment.Right);
        }

        // Years (and the first month): a label is skipped when it would touch the previous one.
        var labelEnd = double.MinValue;
        for (var i = 0; i < points.Count; i++)
        {
            var label = points[i].Label;
            if (label.Length == 0) continue;
            var x = X(i, width);
            var half = label.Length * 3.5 + 2;
            if (x - half < labelEnd + 6) continue;
            labelEnd = x + half;
            Text(ds, label, new Rect(x - half - 4, size.Height - Bottom + 4, 2 * half + 8, 16), palette.Text3, 11, CanvasHorizontalAlignment.Center);
        }

        var line = new Vector2[points.Count];
        for (var i = 0; i < points.Count; i++) line[i] = new Vector2((float)X(i, width), (float)Y(points[i].Value));

        if (points.Count > 1)
        {
            using (var area = new CanvasPathBuilder(ds))
            {
                area.BeginFigure((float)Left, (float)(Top + height));
                foreach (var point in line) area.AddLine(point);
                area.AddLine((float)(Left + width), (float)(Top + height));
                area.EndFigure(CanvasFigureLoop.Closed);
                using var geometry = CanvasGeometry.CreatePath(area);
                ds.FillGeometry(geometry, Windows.UI.Color.FromArgb(0x30, palette.Accent.R, palette.Accent.G, palette.Accent.B));
            }

            using var path = new CanvasPathBuilder(ds);
            path.BeginFigure(line[0]);
            for (var i = 1; i < line.Length; i++) path.AddLine(line[i]);
            path.EndFigure(CanvasFigureLoop.Open);
            using var stroke = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(stroke, palette.Accent, 2);
        }

        if (_hover >= 0)
        {
            var point = line[_hover];
            ds.DrawLine(point.X, (float)Top, point.X, (float)(Top + height), palette.Grid, 1);
            ds.FillCircle(point, 5, palette.Surface);
            ds.DrawCircle(point, 5, palette.Accent, 2);
        }
        else if (points.Count == 1)
        {
            ds.FillCircle(line[0], 4, palette.Accent);
        }
    }
}
