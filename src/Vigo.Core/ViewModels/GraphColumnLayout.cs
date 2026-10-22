namespace Vigo.Core.ViewModels;

/// <summary>
/// Widths of the commit graph columns (graph, message, author, date, SHA), shared by the XAML header and the Win2D
/// renderer. Laid out from the left: the message column takes the remaining width but never less than
/// <see cref="MinMessageWidth"/>; when the view is too narrow the right-hand columns go past its edge (clipped),
/// they never move back over the graph.
/// </summary>
public readonly record struct GraphColumnLayout(double Graph, double Message, double Author, double Date, double Sha)
{
    public const double MinMessageWidth = 120;

    /// <summary>Space kept free on the right, under the vertical scroll bar.</summary>
    public const double RightPadding = 12;

    public double MessageX => Graph;

    public double AuthorX => MessageX + Message;

    public double DateX => AuthorX + Author;

    public double ShaX => DateX + Date;

    public static GraphColumnLayout Compute(double viewportWidth, double graph, double author, double date, double sha)
    {
        var message = Math.Max(MinMessageWidth, viewportWidth - RightPadding - graph - author - date - sha);
        return new GraphColumnLayout(graph, message, author, date, sha);
    }
}
