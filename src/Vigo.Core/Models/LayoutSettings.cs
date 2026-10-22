namespace Vigo.Core.Models;

/// <summary>Window rectangle in physical pixels (screen coordinates), as reported by <c>AppWindow</c>.</summary>
public sealed record WindowBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Window placement and panel / column widths, remembered across launches (<c>layout.json</c>).
/// Shared by every repository tab. Missing values keep their defaults (the mockup sizes).
/// </summary>
public sealed record LayoutSettings
{
    /// <summary>Bounds of the window in its normal (restored) state; null until the window has been shown once.</summary>
    public WindowBounds? Window { get; init; }

    /// <summary>The window is maximized: it is maximized again at launch, <see cref="Window"/> being its restore bounds.</summary>
    public bool IsMaximized { get; init; }

    public double LeftPanelWidth { get; init; } = LayoutLimits.LeftPanel.Default;

    public double RightPanelWidth { get; init; } = LayoutLimits.RightPanel.Default;

    /// <summary>Width of the graph column set by the user; null: sized from the number of lanes.</summary>
    public double? GraphColumnWidth { get; init; }

    public double AuthorColumnWidth { get; init; } = LayoutLimits.AuthorColumn.Default;

    public double DateColumnWidth { get; init; } = LayoutLimits.DateColumn.Default;

    public double ShaColumnWidth { get; init; } = LayoutLimits.ShaColumn.Default;

    /// <summary>Largest height of the commit description in the details panel (it scrolls beyond).</summary>
    public double CommitMessageHeight { get; init; } = LayoutLimits.CommitMessage.Default;

    /// <summary>Height of the integrated terminal panel (J7.10).</summary>
    public double TerminalHeight { get; init; } = LayoutLimits.Terminal.Default;

    /// <summary>Changed files are listed as a tree of folders rather than as paths (Path / Tree).</summary>
    public bool FileListAsTree { get; init; }

    /// <summary>Width of the changed files list of the pull request screen.</summary>
    public double PullRequestFilesWidth { get; init; } = LayoutLimits.PullRequestFiles.Default;

    /// <summary>Brings every width back within its limits (hand-edited or corrupted file).</summary>
    public LayoutSettings Normalize() => this with
    {
        Window = Window is { Width: > 0, Height: > 0 } ? Window : null,
        LeftPanelWidth = LayoutLimits.LeftPanel.Clamp(LeftPanelWidth),
        RightPanelWidth = LayoutLimits.RightPanel.Clamp(RightPanelWidth),
        GraphColumnWidth = GraphColumnWidth is { } graph ? LayoutLimits.GraphColumn.Clamp(graph) : null,
        AuthorColumnWidth = LayoutLimits.AuthorColumn.Clamp(AuthorColumnWidth),
        DateColumnWidth = LayoutLimits.DateColumn.Clamp(DateColumnWidth),
        ShaColumnWidth = LayoutLimits.ShaColumn.Clamp(ShaColumnWidth),
        CommitMessageHeight = LayoutLimits.CommitMessage.Clamp(CommitMessageHeight),
        TerminalHeight = LayoutLimits.Terminal.Clamp(TerminalHeight),
        PullRequestFilesWidth = LayoutLimits.PullRequestFiles.Clamp(PullRequestFilesWidth),
    };
}

/// <summary>Allowed range and default of a resizable width, in device-independent pixels.</summary>
public readonly record struct WidthLimit(double Minimum, double Maximum, double Default)
{
    /// <summary>Clamps <paramref name="width"/>; a non-finite value gives the default.</summary>
    public double Clamp(double width) => double.IsFinite(width) ? Math.Clamp(width, Minimum, Maximum) : Default;
}

public static class LayoutLimits
{
    public static readonly WidthLimit LeftPanel = new(180, 480, 264);
    public static readonly WidthLimit RightPanel = new(300, 720, 360);

    /// <summary>Width always left to the graph between the two panels.</summary>
    public const double CenterMinimum = 360;

    public static readonly WidthLimit GraphColumn = new(24, 600, 64);
    public static readonly WidthLimit AuthorColumn = new(80, 400, 170);
    public static readonly WidthLimit DateColumn = new(70, 300, 120);
    public static readonly WidthLimit ShaColumn = new(60, 200, 76);

    /// <summary>Width of the changed files list beside the diff of a pull request.</summary>
    public static readonly WidthLimit PullRequestFiles = new(200, 800, 340);

    /// <summary>Width always left to the diff beside the files list of a pull request.</summary>
    public const double PullRequestDiffMinimum = 320;

    /// <summary>Height (not width) of the commit description box.</summary>
    public static readonly WidthLimit CommitMessage = new(40, 900, 140);

    /// <summary>Height (not width) of the integrated terminal panel.</summary>
    public static readonly WidthLimit Terminal = new(100, 900, 232);

    /// <summary>Height always left to the graph above the terminal panel.</summary>
    public const double TerminalCenterMinimum = 160;
}
