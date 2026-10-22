namespace Vigo.Core.Models;

/// <summary>A point of a chart: its axis label, its value, and the sentence shown when it is hovered.</summary>
public sealed record ChartPoint(string Label, double Value, string Tooltip);

/// <summary>Points of a line or bar chart, with the text a screen reader gets for the whole chart.</summary>
public sealed record ChartSeries(IReadOnlyList<ChartPoint> Points, string Summary)
{
    public static ChartSeries Empty { get; } = new([], string.Empty);

    public double Max => Points.Count == 0 ? 0 : Points.Max(p => p.Value);
}

/// <summary>A part of a pie chart; <see cref="IsOther"/> is the "Autres" slice that gathers the small ones.</summary>
public sealed record PieSlice(string Label, double Value, string Tooltip, bool IsOther = false);

/// <summary>Round graduations of a chart axis.</summary>
public static class ChartScale
{
    /// <summary>
    /// Graduations 0, step, 2×step… covering <paramref name="max"/> with about <paramref name="target"/> intervals; the step is
    /// 1, 2, 5 times a power of ten, at least 1 (the charts count commits). A zero or negative maximum gives 0 and 1.
    /// </summary>
    public static IReadOnlyList<double> NiceTicks(double max, int target = 4)
    {
        if (max <= 0 || double.IsNaN(max) || double.IsInfinity(max)) return [0, 1];
        var raw = max / Math.Max(1, target);
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var fraction = raw / power;
        var step = Math.Max(1, (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * power);

        List<double> ticks = [];
        for (var value = 0.0; ; value += step)
        {
            ticks.Add(value);
            if (value >= max) break;
        }
        return ticks;
    }
}
