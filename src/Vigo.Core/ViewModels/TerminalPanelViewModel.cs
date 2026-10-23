using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>One terminal of the panel: a shell started in the working directory of the repository.</summary>
public sealed partial class TerminalTabViewModel(TerminalShell shell, string commandLine, string workingDirectory, string title) : ObservableObject
{
    public TerminalShell Shell { get; } = shell;

    /// <summary>Command line actually run (the shell's own, or the custom one of the preferences).</summary>
    public string CommandLine { get; } = commandLine;

    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>"PowerShell 7 · atlas-api".</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    /// <summary>The terminal shown in the panel (underlined tab).</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Integrated terminal of a repository tab (J7.10, ADR-071), at the bottom of the tab: one terminal per tab of the
/// panel, each running a shell in the repository's working directory. Hiding the panel keeps the shells running;
/// closing a terminal ends its shell (the view owns the sessions and follows <see cref="Tabs"/>).
/// </summary>
/// <remarks>User-facing texts: <c>Terminal.*</c> resources (ADR-075).</remarks>
public sealed partial class TerminalPanelViewModel(TerminalService? service, string workingDirectory, string repositoryName, IUserNotifier? notifier = null)
    : ObservableObject, IDisposable
{
    /// <summary>Terminals are offered (the services are there).</summary>
    public bool IsAvailable => service is not null;

    /// <summary>"Git" tab: the git.exe commands VIGO runs in this repository.</summary>
    public GitCommandLogViewModel GitLog { get; } = new(service?.Commands, workingDirectory);

    /// <summary>The Git tab is the one shown (no terminal selected).</summary>
    [ObservableProperty]
    public partial bool IsGitLogSelected { get; set; }

    /// <summary>Shows the panel on its Git tab (toolbar, command palette).</summary>
    [RelayCommand]
    private void ShowGitLog()
    {
        SelectedTab = null;
        IsGitLogSelected = true;
        IsOpen = true;
    }

    public void Dispose() => GitLog.Dispose();

    /// <summary>Shells found on this machine, for the "+" menu.</summary>
    public IReadOnlyList<TerminalShell> Shells => service?.Shells ?? [];

    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    public partial TerminalTabViewModel? SelectedTab { get; set; }

    partial void OnSelectedTabChanged(TerminalTabViewModel? oldValue, TerminalTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            IsGitLogSelected = false;
        }
    }

    /// <summary>The panel is shown.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>Shows the panel (starting a first terminal when there is none), or hides it (Ctrl+`, toolbar).</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (IsOpen)
        {
            IsOpen = false;
            return;
        }
        if (Tabs.Count == 0 && !IsGitLogSelected && !OpenTab(null)) return;
        IsOpen = true;
    }

    /// <summary>Hides the panel; the shells keep running.</summary>
    [RelayCommand]
    private void Hide() => IsOpen = false;

    /// <summary>Starts a terminal with <paramref name="shell"/>; the default shell when null. False when there is no shell.</summary>
    public bool OpenTab(TerminalShell? shell)
    {
        if (service is null) return false;
        shell ??= service.DefaultShell;
        if (shell is null)
        {
            notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Terminal.NoShell.Title"), Loc.Get("Terminal.NoShell.Message"));
            return false;
        }
        var tab = new TerminalTabViewModel(shell, service.CommandLineOf(shell), workingDirectory, $"{shell.Name} · {repositoryName}");
        Tabs.Add(tab);
        SelectedTab = tab;
        IsOpen = true;
        return true;
    }

    [RelayCommand]
    private void NewTab(TerminalShell? shell) => OpenTab(shell);

    /// <summary>Ends the shell of a terminal and removes it; the panel hides when it was the last one.</summary>
    [RelayCommand]
    private void CloseTab(TerminalTabViewModel? tab)
    {
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        if (ReferenceEquals(SelectedTab, tab)) SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        if (Tabs.Count == 0 && !IsGitLogSelected) IsOpen = false;
    }
}
