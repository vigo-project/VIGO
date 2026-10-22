using System.Collections.Immutable;
using Vigo.Core.Models;

namespace Vigo.Core.Services;

/// <summary>
/// Current gesture of every configurable shortcut: the default of <see cref="ShortcutCatalog"/> unless the user changed
/// it in the preferences. Views register their shortcuts by command id and follow <see cref="Changed"/>.
/// </summary>
public sealed class ShortcutMap : IDisposable
{
    private readonly Preferences? _preferences;
    private ImmutableArray<ShortcutBinding> _source;
    private Dictionary<string, KeyGesture?> _current = [];

    /// <summary>
    /// Map of the application, set once at startup. Defaults only until then (view models and tests that show
    /// shortcut texts without being given a map).
    /// </summary>
    public static ShortcutMap Current { get; set; } = new(null);

    public ShortcutMap(Preferences? preferences)
    {
        _preferences = preferences;
        Rebuild();
        if (_preferences is not null) _preferences.Changed += OnPreferencesChanged;
    }

    /// <summary>Raised when the gesture of at least one command changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Gesture of the command; null when the user removed its shortcut.</summary>
    public KeyGesture? Get(string id) => _current.TryGetValue(id, out var gesture) ? gesture : null;

    /// <summary>Gesture as shown in menus, tooltips and the palette ("Ctrl+Maj+L"); null without shortcut.</summary>
    public string? Display(string id) => Get(id)?.ToString();

    /// <summary>"Pull (Ctrl+Maj+L)", or just the text when the command has no shortcut.</summary>
    public string WithHint(string text, string id) => Display(id) is { } shortcut ? $"{text} ({shortcut})" : text;

    /// <summary>
    /// What already uses the gesture: another command (its label) or a fixed shortcut of the application; null when it is free.
    /// </summary>
    public string? FindConflict(string id, KeyGesture gesture)
    {
        foreach (var definition in ShortcutCatalog.All)
        {
            if (definition.Id != id && Get(definition.Id) == gesture) return definition.Label;
        }
        foreach (var (reserved, use) in ShortcutCatalog.Reserved)
        {
            if (reserved == gesture) return use;
        }
        return null;
    }

    /// <summary>Gives the command this gesture (or none with null); the caller checks <see cref="FindConflict"/> first.</summary>
    public Task SetAsync(string id, KeyGesture? gesture) => Update(id, gesture is { } g ? g.Serialize() : string.Empty);

    /// <summary>Back to the default gesture of the command.</summary>
    public Task ResetAsync(string id) => Update(id, null);

    /// <summary>Back to the defaults for every command.</summary>
    public Task ResetAllAsync() =>
        _preferences?.UpdateAsync(p => p with { Shortcuts = [] }) ?? Task.CompletedTask;

    /// <summary>The user changed the gesture of the command (it differs from the default).</summary>
    public bool IsCustomized(string id) =>
        ShortcutCatalog.Find(id) is { } definition && Get(id) != definition.Default;

    private Task Update(string id, string? gesture)
    {
        if (_preferences is null || ShortcutCatalog.Find(id) is not { } definition) return Task.CompletedTask;
        return _preferences.UpdateAsync(p =>
        {
            var others = p.Shortcuts.RemoveAll(b => b.Command == id);
            // Same as the default: nothing to remember.
            return p with
            {
                Shortcuts = gesture is null || gesture == definition.Default.Serialize()
                    ? others
                    : others.Add(new ShortcutBinding(id, gesture)),
            };
        });
    }

    public void Dispose()
    {
        if (_preferences is not null) _preferences.Changed -= OnPreferencesChanged;
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        if (_preferences!.Current.Shortcuts == _source) return;
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        _source = _preferences?.Current.Shortcuts ?? [];
        var map = new Dictionary<string, KeyGesture?>();
        foreach (var definition in ShortcutCatalog.All) map[definition.Id] = definition.Default;
        foreach (var binding in _source)
        {
            if (!map.ContainsKey(binding.Command)) continue;
            map[binding.Command] = KeyGesture.TryParse(binding.Gesture, out var gesture) ? gesture : null;
        }
        _current = map;
    }
}
