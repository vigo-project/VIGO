using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

// "Raccourcis" section (J7.3, ADR-054): one row per configurable command; a click on the gesture waits for the next key
// combination, which the view hands to AssignShortcutAsync. Texts: Shortcuts.* resources (ADR-075).
public sealed partial class PreferencesTabViewModel
{
    private readonly ShortcutMap? _shortcuts;

    public bool IsShortcuts => SelectedSection.Section == PreferencesSection.Shortcuts;

    public ObservableCollection<ShortcutItem> ShortcutItems { get; } = [];

    private void InitializeShortcuts()
    {
        if (_shortcuts is null) return;
        foreach (var definition in ShortcutCatalog.All) ShortcutItems.Add(new ShortcutItem(definition));
        _shortcuts.Changed += OnShortcutsChanged;
        SyncShortcuts();
    }

    private void DisposeShortcuts()
    {
        if (_shortcuts is not null) _shortcuts.Changed -= OnShortcutsChanged;
    }

    private void OnShortcutsChanged(object? sender, EventArgs e) => SyncShortcuts();

    private void SyncShortcuts()
    {
        if (_shortcuts is null) return;
        foreach (var item in ShortcutItems)
        {
            item.GestureText = _shortcuts.Display(item.Definition.Id) ?? Loc.Get("Shortcuts.None");
            item.HasShortcut = _shortcuts.Get(item.Definition.Id) is not null;
            item.IsCustomized = _shortcuts.IsCustomized(item.Definition.Id);
        }
    }

    /// <summary>Waits for the next key combination for this command (one row at a time).</summary>
    [RelayCommand]
    private void StartShortcutCapture(ShortcutItem item)
    {
        foreach (var other in ShortcutItems)
        {
            other.IsCapturing = ReferenceEquals(other, item);
            other.Message = null;
        }
    }

    /// <summary>The capture ended without a key (Échap, focus lost).</summary>
    public static void CancelShortcutCapture(ShortcutItem item)
    {
        item.IsCapturing = false;
        item.Message = null;
    }

    /// <summary>
    /// The user pressed <paramref name="gesture"/> while the row was waiting for one: assigned unless it is not usable
    /// or already taken, in which case the row explains why and keeps waiting.
    /// </summary>
    public async Task AssignShortcutAsync(ShortcutItem item, KeyGesture gesture)
    {
        if (_shortcuts is null) return;
        if (!gesture.IsValid)
        {
            item.Message = Loc.Get("Shortcuts.NeedsModifier");
            return;
        }
        if (_shortcuts.FindConflict(item.Definition.Id, gesture) is { } conflict)
        {
            item.Message = Loc.Format("Shortcuts.Conflict", gesture, conflict);
            return;
        }
        item.IsCapturing = false;
        item.Message = null;
        await _shortcuts.SetAsync(item.Definition.Id, gesture);
    }

    /// <summary>Removes the shortcut of the command.</summary>
    [RelayCommand]
    private Task ClearShortcutAsync(ShortcutItem item)
    {
        item.IsCapturing = false;
        item.Message = null;
        return _shortcuts?.SetAsync(item.Definition.Id, null) ?? Task.CompletedTask;
    }

    /// <summary>Gives the command its default shortcut back, unless another command took it meanwhile.</summary>
    [RelayCommand]
    private async Task ResetShortcutAsync(ShortcutItem item)
    {
        if (_shortcuts is null) return;
        item.IsCapturing = false;
        item.Message = null;
        if (_shortcuts.FindConflict(item.Definition.Id, item.Definition.Default) is { } conflict)
        {
            item.Message = Loc.Format("Shortcuts.ResetConflict", item.Definition.Default, conflict);
            return;
        }
        await _shortcuts.ResetAsync(item.Definition.Id);
    }

    [RelayCommand]
    private Task ResetAllShortcutsAsync()
    {
        foreach (var item in ShortcutItems)
        {
            item.IsCapturing = false;
            item.Message = null;
        }
        return _shortcuts?.ResetAllAsync() ?? Task.CompletedTask;
    }
}

/// <summary>Row of the "Raccourcis" section.</summary>
public sealed partial class ShortcutItem(ShortcutDefinition definition) : ObservableObject
{
    public ShortcutDefinition Definition { get; } = definition;

    public string Label => Definition.Label;

    public string ScopeLabel => Loc.Get(Definition.Scope == ShortcutScope.Repository ? "Shortcuts.Scope.Repository" : "Shortcuts.Scope.Window");

    /// <summary>Gesture as shown ("Ctrl+Maj+L"), or "Aucun".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    public partial string GestureText { get; set; } = string.Empty;

    /// <summary>The command has a shortcut (the "Retirer" action applies).</summary>
    [ObservableProperty]
    public partial bool HasShortcut { get; set; }

    /// <summary>Differs from the default (the "Rétablir" action applies).</summary>
    [ObservableProperty]
    public partial bool IsCustomized { get; set; }

    /// <summary>Waiting for the next key combination.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    public partial bool IsCapturing { get; set; }

    /// <summary>Why the last combination was refused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    /// <summary>Text of the button that starts the capture.</summary>
    public string ButtonText => IsCapturing ? Loc.Get("Shortcuts.Capturing") : GestureText;

    public override string ToString() => Label;
}
