using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Vigo.Core.Models;
using Windows.Foundation;
using Windows.UI;

namespace Vigo.App.Controls.Charts;

/// <summary>
/// Pie chart (top contributors) with its legend on the right: a color, the name and the share of each slice. The slice under the
/// pointer is detached and gives its tooltip; "Autres" is drawn in the neutral color.
/// </summary>
public sealed partial class PieChart : ChartControl
{
    private const double Pad = 12;
    private const double Detach = 6;
    private const double LegendRow = 22;

    private IReadOnlyList<PieSlice> _slices = [];
    private int _hover = -1;

    public PieChart() => MinHeight = 160;

    public IReadOnlyList<PieSlice> Slices
    {
        get => _slices;
        set
        {
            _slices = value;
            _hover = -1;
            SetTooltip(null);
            Redraw();
        }
    }

    private double Total => _slices.Sum(s => s.Value);

    private static (Vector2 Center, float Radius) Disc(Size size)
    {
        var diameter = Math.Max(0, Math.Min(size.Height - 2 * Pad - 2 * Detach, size.Width / 2 - Pad - Detach));
        return (new Vector2((float)(Pad + Detach + diameter / 2), (float)(size.Height / 2)), (float)(diameter / 2));
    }

    private static Color ColorOf(PieSlice slice, int index, ChartPalette palette) => slice.IsOther ? palette.Text3 : ChartPalette.Series(index);

    private protected override void OnPointer(Point? position)
    {
        var next = -1;
        if (position is { } p && _slices.Count > 0 && Total > 0)
        {
            var (center, radius) = Disc(new Size(ActualWidth, ActualHeight));
            var dx = p.X - center.X;
            var dy = p.Y - center.Y;
            if (radius > 0 && Math.Sqrt(dx * dx + dy * dy) <= radius + Detach)
            {
                // Clockwise angle from 12 o'clock, like the slices are drawn.
                var angle = Math.Atan2(dx, -dy);
                if (angle < 0) angle += 2 * Math.PI;
                var sweep = 0.0;
                for (var i = 0; i < _slices.Count; i++)
                {
                    sweep += _slices[i].Value / Total * 2 * Math.PI;
                    if (angle <= sweep)
                    {
                        next = i;
                        break;
                    }
                }
            }
        }
        if (next == _hover) return;
        _hover = next;
        SetTooltip(next >= 0 ? _slices[next].Tooltip : null);
        Redraw();
    }

    private protected override void DrawChart(CanvasDrawingSession ds, Size size, ChartPalette palette)
    {
        var total = Total;
        if (_slices.Count == 0 || total <= 0) return;
        var (center, radius) = Disc(size);

        if (radius > 0)
        {
            var start = -Math.PI / 2;
            for (var i = 0; i < _slices.Count; i++)
            {
                var sweep = _slices[i].Value / total * 2 * Math.PI;
                var color = ColorOf(_slices[i], i, palette);
                var middle = start + sweep / 2;
                var origin = i == _hover ? center + new Vector2((float)(Math.Cos(middle) * Detach), (float)(Math.Sin(middle) * Detach)) : center;
                if (_slices.Count == 1)
                {
                    ds.FillCircle(origin, radius, color);
                }
                else
                {
                    using var path = new CanvasPathBuilder(ds);
                    path.BeginFigure(origin);
                    path.AddLine(origin + new Vector2((float)(Math.Cos(start) * radius), (float)(Math.Sin(start) * radius)));
                    var end = origin + new Vector2((float)(Math.Cos(start + sweep) * radius), (float)(Math.Sin(start + sweep) * radius));
                    path.AddArc(end, radius, radius, 0, CanvasSweepDirection.Clockwise, sweep > Math.PI ? CanvasArcSize.Large : CanvasArcSize.Small);
                    path.EndFigure(CanvasFigureLoop.Closed);
                    using var geometry = CanvasGeometry.CreatePath(path);
                    ds.FillGeometry(geometry, color);
                    ds.DrawGeometry(geometry, palette.Surface, 1.5f);
                }
                start += sweep;
            }
        }

        // Legend: as many rows as fit.
        var left = size.Width / 2 + Pad;
        var rows = (int)Math.Max(1, Math.Min(_slices.Count, (size.Height - 2 * Pad) / LegendRow));
        var top = (size.Height - rows * LegendRow) / 2;
        for (var i = 0; i < rows; i++)
        {
            var y = top + i * LegendRow;
            var color = ColorOf(_slices[i], i, palette);
            ds.FillRoundedRectangle(new Rect(left, y + 6, 10, 10), 2, 2, color);
            var share = (_slices[i].Value / total).ToString("P0", CultureInfo.CurrentCulture);
            Text(ds, share, new Rect(size.Width - 52, y, 44, LegendRow), i == _hover ? palette.Text : palette.Text3, 12, CanvasHorizontalAlignment.Right);
            Text(ds, _slices[i].Label, new Rect(left + 18, y, Math.Max(0, size.Width - left - 18 - 56), LegendRow), i == _hover ? palette.Text : palette.Text2, 12);
        }
    }
}
