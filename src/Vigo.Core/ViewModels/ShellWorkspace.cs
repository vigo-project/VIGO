namespace Vigo.Core.ViewModels;

/// <summary>
/// The open application windows (one <see cref="ShellViewModel"/> each, in opening order), all on the UI thread
/// (J4.7). Lets a repository or the preferences be open once in the whole application, and tabs move between windows.
/// </summary>
public sealed class ShellWorkspace
{
    private readonly List<ShellViewModel> _shells = [];

    public IReadOnlyList<ShellViewModel> Shells => _shells;

    /// <summary>Tab projects of the active profile, shared by every window (J7.6).</summary>
    public Services.TabProjects Projects { get; } = new();

    /// <summary>
    /// Opens and shows a new window, with a welcome tab when the argument is true (empty otherwise, to receive a
    /// tab), and returns its shell. Set by the application; null without windows (tests).
    /// </summary>
    public Func<bool, ShellViewModel>? OpenWindow { get; set; }

    /// <summary>Closes every window, the session keeping all of them. Set by the application.</summary>
    public Action? Quit { get; set; }

    /// <summary>
    /// Makes the profile with this id the active one: the repositories open in every window are saved under the current
    /// profile and replaced by those of the new one. Set by the application.
    /// </summary>
    public Func<string, Task>? SwitchProfile { get; set; }

    /// <summary>Automatic updates (beta deployment); null when the application has none (tests). Set by the application.</summary>
    public Services.AppUpdates? Updates { get; set; }

    internal void Add(ShellViewModel shell) => _shells.Add(shell);

    internal void Remove(ShellViewModel shell) => _shells.Remove(shell);
}
