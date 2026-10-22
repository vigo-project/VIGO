using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>"Terminal intégré" of Préférences › Éditeur et outils (J7.10, ADR-071).</summary>
public sealed partial class PreferencesTabViewModel
{
    private readonly TerminalService? _terminal;

    /// <summary>The terminal service is there: the section is shown.</summary>
    public bool HasTerminal => _terminal is not null;

    /// <summary>Shells found on this machine.</summary>
    public ObservableCollection<TerminalShell> TerminalShellOptions { get; } = [];

    /// <summary>Shell of a new terminal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TerminalCommandPlaceholder))]
    public partial TerminalShell? TerminalShellChoice { get; set; }

    partial void OnTerminalShellChoiceChanged(TerminalShell? value)
    {
        if (value is null) return;
        // The custom command belongs to the default shell: a new default shell starts with its own command.
        Update(p => p with { TerminalShell = value.Id, TerminalCommand = p.TerminalShell == value.Id ? p.TerminalCommand : null });
        if (!_syncing) TerminalCommandText = string.Empty;
    }

    /// <summary>Command line of the default shell; empty keeps <see cref="TerminalCommandPlaceholder"/>.</summary>
    [ObservableProperty]
    public partial string TerminalCommandText { get; set; } = string.Empty;

    partial void OnTerminalCommandTextChanged(string value) => Update(p => p with { TerminalCommand = value });

    public string TerminalCommandPlaceholder => TerminalShellChoice?.CommandLine ?? string.Empty;

    private void SyncTerminal(UserPreferences current)
    {
        if (_terminal is null) return;
        if (TerminalShellOptions.Count == 0)
        {
            foreach (var shell in _terminal.Shells) TerminalShellOptions.Add(shell);
        }
        TerminalShellChoice = TerminalShellOptions.FirstOrDefault(s => s.Id == current.TerminalShell) ?? TerminalShellOptions.FirstOrDefault();
        TerminalCommandText = current.TerminalCommand ?? string.Empty;
    }
}
