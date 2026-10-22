namespace Vigo.Core.Models;

/// <summary>Tabs of one window when the application was closed.</summary>
/// <param name="Repositories">Paths of the repository tabs, in tab order (working directory, or Git directory when bare).</param>
/// <param name="SelectedIndex">Index of the selected repository tab in <paramref name="Repositories"/>; -1 when the
/// welcome tab was selected.</param>
/// <param name="Bounds">Normal (restored) bounds of the window, in physical pixels; null: placed by the application.</param>
/// <param name="IsMaximized">The window was maximized.</param>
/// <param name="Unrestored">Paths of <paramref name="Repositories"/> that could not be opened at the last launch: if they are
/// still missing at the next one, they are forgotten without a warning (J9.44).</param>
public sealed record SessionWindow(
    IReadOnlyList<string> Repositories, int SelectedIndex = -1, WindowBounds? Bounds = null, bool IsMaximized = false,
    IReadOnlyList<string>? Unrestored = null);

/// <summary>
/// Repositories open when the application was closed, reopened at the next launch (<c>session.json</c>, one entry
/// per profile): one entry per window, the first one being the main window, and the tab projects (J7.6).
/// </summary>
public sealed record SessionSettings(IReadOnlyList<SessionWindow> Windows, IReadOnlyList<TabProject>? Projects = null)
{
    public static readonly SessionSettings Empty = new([]);
}

/// <summary>Profiles (identity, host accounts) come with J6; until then everything belongs to this one.</summary>
public static class SessionProfiles
{
    public const string Default = "default";
}
