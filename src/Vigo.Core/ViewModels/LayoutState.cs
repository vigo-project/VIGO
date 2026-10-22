using CommunityToolkit.Mvvm.ComponentModel;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Current window placement, panel / column widths and file list mode, shared by every repository tab (a resize in one tab applies
/// to all). Each width is kept within its <see cref="LayoutLimits"/>; saved on demand.
/// </summary>
public sealed partial class LayoutState(ILayoutStore store, LayoutSettings initial) : ObservableObject
{
    public WindowBounds? WindowBounds { get; set; } = initial.Window;

    public bool IsMaximized { get; set; } = initial.IsMaximized;

    public double LeftPanelWidth
    {
        get;
        set => SetProperty(ref field, LayoutLimits.LeftPanel.Clamp(value));
    } = initial.LeftPanelWidth;

    public double RightPanelWidth
    {
        get;
        set => SetProperty(ref field, LayoutLimits.RightPanel.Clamp(value));
    } = initial.RightPanelWidth;

    /// <summary>Width of the graph column set by the user; null: sized from the number of lanes.</summary>
    public double? GraphColumnWidth
    {
        get;
        set => SetProperty(ref field, value is { } width ? LayoutLimits.GraphColumn.Clamp(width) : null);
    } = initial.GraphColumnWidth;

    public double AuthorColumnWidth
    {
        get;
        set => SetProperty(ref field, LayoutLimits.AuthorColumn.Clamp(value));
    } = initial.AuthorColumnWidth;

    public double DateColumnWidth
    {
        get;
        set => SetProperty(ref field, LayoutLimits.DateColumn.Clamp(value));
    } = initial.DateColumnWidth;

    public double ShaColumnWidth
    {
        get;
        set => SetProperty(ref field, LayoutLimits.ShaColumn.Clamp(value));
    } = initial.ShaColumnWidth;

    /// <summary>Largest height of the commit description in the details panel.</summary>
    public double CommitMessageHeight
    {
        get;
        set => SetProperty(ref field, LayoutLimits.CommitMessage.Clamp(value));
    } = initial.CommitMessageHeight;

    /// <summary>Height of the integrated terminal panel, shared by every tab.</summary>
    public double TerminalHeight
    {
        get;
        set => SetProperty(ref field, LayoutLimits.Terminal.Clamp(value));
    } = initial.TerminalHeight;

    /// <summary>Width of the changed files list of the pull request screen, shared by every tab.</summary>
    public double PullRequestFilesWidth
    {
        get;
        set => SetProperty(ref field, LayoutLimits.PullRequestFiles.Clamp(value));
    } = initial.PullRequestFilesWidth;

    /// <summary>Changed files are listed as a tree of folders rather than as paths.</summary>
    public bool FileListAsTree
    {
        get;
        set => SetProperty(ref field, value);
    } = initial.FileListAsTree;

    /// <summary>
    /// Panel widths to apply in a body of <paramref name="totalWidth"/>: the saved widths, reduced when needed so that
    /// <see cref="LayoutLimits.CenterMinimum"/> stays for the graph (right panel first, never below the minimums).
    /// The saved widths are kept: they come back when the window grows again.
    /// </summary>
    public (double Left, double Right) FitPanels(double totalWidth)
    {
        var left = LeftPanelWidth;
        var right = RightPanelWidth;
        var excess = left + right + LayoutLimits.CenterMinimum - totalWidth;
        if (excess <= 0) return (left, right);

        var fromRight = Math.Min(excess, right - LayoutLimits.RightPanel.Minimum);
        right -= fromRight;
        left -= Math.Min(excess - fromRight, left - LayoutLimits.LeftPanel.Minimum);
        return (left, right);
    }

    /// <summary>Largest width the left panel can take in a body of <paramref name="totalWidth"/>, given the right panel.</summary>
    public double MaxLeftPanelWidth(double totalWidth) =>
        Math.Max(LayoutLimits.LeftPanel.Minimum, totalWidth - LayoutLimits.CenterMinimum - FitPanels(totalWidth).Right);

    /// <summary>Largest width the right panel can take in a body of <paramref name="totalWidth"/>, given the left panel.</summary>
    public double MaxRightPanelWidth(double totalWidth) =>
        Math.Max(LayoutLimits.RightPanel.Minimum, totalWidth - LayoutLimits.CenterMinimum - FitPanels(totalWidth).Left);

    /// <summary>Terminal height to apply in a body of <paramref name="totalHeight"/>: the saved one, leaving room for the graph.</summary>
    public double FitTerminal(double totalHeight) =>
        Math.Max(LayoutLimits.Terminal.Minimum, Math.Min(TerminalHeight, totalHeight - LayoutLimits.TerminalCenterMinimum));

    public LayoutSettings ToSettings() => new()
    {
        Window = WindowBounds,
        IsMaximized = IsMaximized,
        LeftPanelWidth = LeftPanelWidth,
        RightPanelWidth = RightPanelWidth,
        GraphColumnWidth = GraphColumnWidth,
        AuthorColumnWidth = AuthorColumnWidth,
        DateColumnWidth = DateColumnWidth,
        ShaColumnWidth = ShaColumnWidth,
        CommitMessageHeight = CommitMessageHeight,
        TerminalHeight = TerminalHeight,
        FileListAsTree = FileListAsTree,
        PullRequestFilesWidth = PullRequestFilesWidth,
    };

    public Task SaveAsync(CancellationToken cancellationToken = default) => store.SaveAsync(ToSettings(), cancellationToken);
}
