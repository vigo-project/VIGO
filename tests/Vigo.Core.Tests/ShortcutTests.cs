using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class ShortcutTests : IDisposable
{
    private readonly InMemoryPreferencesStore _store = new();
    private readonly Preferences _preferences;
    private readonly ShortcutMap _map;

    public ShortcutTests()
    {
        _preferences = new Preferences(_store, new UserPreferences());
        _map = new ShortcutMap(_preferences);
    }

    public void Dispose()
    {
        _map.Dispose();
        _preferences.Dispose();
    }

    [Theory]
    [InlineData("Ctrl+Shift+L", 'L', GestureModifiers.Ctrl | GestureModifiers.Shift)]
    [InlineData("ctrl+alt+n", 'N', GestureModifiers.Ctrl | GestureModifiers.Alt)]
    [InlineData("Ctrl+Maj+O", 'O', GestureModifiers.Ctrl | GestureModifiers.Shift)]
    [InlineData("F5", 116, GestureModifiers.None)]
    [InlineData("Ctrl+,", 188, GestureModifiers.Ctrl)]
    [InlineData("Alt+Delete", 46, GestureModifiers.Alt)]
    public void Parses_gestures(string text, int key, GestureModifiers modifiers)
    {
        Assert.True(KeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new KeyGesture(key, modifiers), gesture);
    }

    [Theory]
    [InlineData("")]
    [InlineData("L")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Hyper+L")]
    [InlineData("Ctrl+Nope")]
    public void Refuses_unusable_gestures(string text) => Assert.False(KeyGesture.TryParse(text, out _));

    [Fact]
    public void Serialized_gestures_read_back_and_display_in_french()
    {
        var gesture = new KeyGesture('L', GestureModifiers.Ctrl | GestureModifiers.Alt | GestureModifiers.Shift);

        Assert.Equal("Ctrl+Alt+Shift+L", gesture.Serialize());
        Assert.Equal("Ctrl+Alt+Maj+L", gesture.ToString());
        Assert.True(KeyGesture.TryParse(gesture.Serialize(), out var back));
        Assert.Equal(gesture, back);
        Assert.Equal("Ctrl+Entrée", new KeyGesture(KeyGesture.Enter, GestureModifiers.Ctrl).ToString());
    }

    [Fact]
    public void A_bare_letter_is_not_a_shortcut_but_a_function_key_is()
    {
        Assert.False(new KeyGesture('L', GestureModifiers.None).IsValid);
        Assert.False(new KeyGesture(KeyGesture.Escape, GestureModifiers.Ctrl).IsValid);
        Assert.False(new KeyGesture(17, GestureModifiers.Ctrl).IsValid);
        Assert.True(new KeyGesture(123, GestureModifiers.None).IsValid);
        Assert.True(KeyGesture.IsModifierKey(17));
    }

    [Fact]
    public void Catalog_defaults_are_valid_and_distinct_and_free_of_reserved_gestures()
    {
        Assert.All(ShortcutCatalog.All, d => Assert.True(d.Default.IsValid, d.Id));
        Assert.Equal(ShortcutCatalog.All.Length, ShortcutCatalog.All.Select(d => d.Default).Distinct().Count());
        Assert.Equal(ShortcutCatalog.All.Length, ShortcutCatalog.All.Select(d => d.Id).Distinct().Count());
        Assert.DoesNotContain(ShortcutCatalog.All, d => ShortcutCatalog.Reserved.Any(r => r.Gesture == d.Default));
    }

    [Fact]
    public void Defaults_apply_until_the_user_changes_one()
    {
        Assert.Equal("Ctrl+Maj+L", _map.Display(ShortcutIds.Pull));
        Assert.Equal("F5", _map.Display(ShortcutIds.Fetch));
        Assert.Equal("Ctrl+,", _map.Display(ShortcutIds.Preferences));
        Assert.Equal("Pull (Ctrl+Maj+L)", _map.WithHint("Pull", ShortcutIds.Pull));
        Assert.False(_map.IsCustomized(ShortcutIds.Pull));
    }

    [Fact]
    public async Task A_changed_shortcut_is_saved_and_raises_Changed()
    {
        var raised = 0;
        _map.Changed += (_, _) => raised++;

        await _map.SetAsync(ShortcutIds.Pull, new KeyGesture('U', GestureModifiers.Ctrl | GestureModifiers.Alt));

        Assert.Equal(1, raised);
        Assert.Equal("Ctrl+Alt+U", _map.Display(ShortcutIds.Pull));
        Assert.True(_map.IsCustomized(ShortcutIds.Pull));
        Assert.Equal([new ShortcutBinding(ShortcutIds.Pull, "Ctrl+Alt+U")], _preferences.Current.Shortcuts.ToArray());
    }

    [Fact]
    public async Task Unrelated_preference_changes_do_not_raise_Changed()
    {
        var raised = 0;
        _map.Changed += (_, _) => raised++;

        await _preferences.UpdateAsync(p => p with { Theme = AppTheme.Dark }, TestContext.Current.CancellationToken);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task A_removed_shortcut_stays_removed_and_reset_brings_the_default_back()
    {
        await _map.SetAsync(ShortcutIds.Push, null);

        Assert.Null(_map.Get(ShortcutIds.Push));
        Assert.Null(_map.Display(ShortcutIds.Push));
        Assert.Equal("Push", _map.WithHint("Push", ShortcutIds.Push));

        await _map.ResetAsync(ShortcutIds.Push);

        Assert.Equal("Ctrl+Maj+P", _map.Display(ShortcutIds.Push));
        Assert.Empty(_preferences.Current.Shortcuts);
    }

    [Fact]
    public async Task Setting_the_default_gesture_stores_nothing_and_reset_all_clears_every_change()
    {
        await _map.SetAsync(ShortcutIds.Undo, new KeyGesture('Z', GestureModifiers.Ctrl));
        Assert.Empty(_preferences.Current.Shortcuts);

        await _map.SetAsync(ShortcutIds.Undo, new KeyGesture('U', GestureModifiers.Ctrl));
        await _map.SetAsync(ShortcutIds.Redo, null);
        Assert.Equal(2, _preferences.Current.Shortcuts.Length);

        await _map.ResetAllAsync();

        Assert.Empty(_preferences.Current.Shortcuts);
        Assert.Equal("Ctrl+Z", _map.Display(ShortcutIds.Undo));
        Assert.Equal("Ctrl+Y", _map.Display(ShortcutIds.Redo));
    }

    [Fact]
    public async Task Conflicts_name_the_command_or_the_fixed_shortcut_that_uses_the_gesture()
    {
        Assert.Equal("Pull", _map.FindConflict(ShortcutIds.Push, new KeyGesture('L', GestureModifiers.Ctrl | GestureModifiers.Shift)));
        Assert.Equal("Copier", _map.FindConflict(ShortcutIds.Push, new KeyGesture('C', GestureModifiers.Ctrl)));
        Assert.Null(_map.FindConflict(ShortcutIds.Push, new KeyGesture('L', GestureModifiers.Ctrl | GestureModifiers.Shift) with { Key = 'K' }));
        // Its own gesture is not a conflict.
        Assert.Null(_map.FindConflict(ShortcutIds.Pull, new KeyGesture('L', GestureModifiers.Ctrl | GestureModifiers.Shift)));

        // A gesture given up by one command becomes free.
        await _map.SetAsync(ShortcutIds.Pull, null);
        Assert.Null(_map.FindConflict(ShortcutIds.Push, new KeyGesture('L', GestureModifiers.Ctrl | GestureModifiers.Shift)));
    }

    [Fact]
    public void Hand_edited_bindings_are_cleaned_up()
    {
        var preferences = new UserPreferences
        {
            Shortcuts =
            [
                new("tab.new", "Ctrl+Alt+T"),
                new("tab.new", "ctrl+alt+y"),
                new("git.pull", "not a gesture"),
                new("git.push", string.Empty),
                new("no.such.command", "Ctrl+Alt+Q"),
                new(" ", "Ctrl+Alt+Z"),
            ],
        }.Normalize();

        Assert.Equal(
            [new ShortcutBinding("tab.new", "Ctrl+Alt+Y"), new ShortcutBinding("git.push", string.Empty), new ShortcutBinding("no.such.command", "Ctrl+Alt+Q")],
            preferences.Shortcuts.ToArray());

        using var preferencesHolder = new Preferences(_store, preferences);
        using var map = new ShortcutMap(preferencesHolder);
        Assert.Equal("Ctrl+Alt+Y", map.Display(ShortcutIds.NewTab));
        Assert.Equal("Ctrl+Maj+L", map.Display(ShortcutIds.Pull));
        Assert.Null(map.Get(ShortcutIds.Push));
    }

    [Fact]
    public void Normalize_keeps_the_same_instance_when_nothing_changes()
    {
        var preferences = new UserPreferences { Shortcuts = [new("tab.new", "Ctrl+Alt+T")] }.Normalize();

        Assert.True(preferences.Shortcuts == preferences.Normalize().Shortcuts);
    }

    [Fact]
    public async Task The_preferences_tab_lists_the_commands_and_assigns_a_free_gesture()
    {
        using var sut = new PreferencesTabViewModel(_preferences, shortcuts: _map);
        sut.Show(PreferencesSection.Shortcuts);
        var pull = sut.ShortcutItems.Single(i => i.Definition.Id == ShortcutIds.Pull);

        Assert.True(sut.IsShortcuts);
        Assert.Equal(ShortcutCatalog.All.Length, sut.ShortcutItems.Count);
        Assert.Equal("Ctrl+Maj+L", pull.GestureText);
        Assert.False(pull.IsCustomized);

        sut.StartShortcutCaptureCommand.Execute(pull);
        Assert.True(pull.IsCapturing);
        Assert.Equal("Appuyez sur les touches…", pull.ButtonText);

        await sut.AssignShortcutAsync(pull, new KeyGesture('U', GestureModifiers.Ctrl | GestureModifiers.Alt));

        Assert.False(pull.IsCapturing);
        Assert.Equal("Ctrl+Alt+U", pull.GestureText);
        Assert.True(pull.IsCustomized);
        Assert.Equal("Ctrl+Alt+U", _map.Display(ShortcutIds.Pull));
    }

    [Fact]
    public async Task The_preferences_tab_refuses_taken_and_unusable_gestures_and_keeps_waiting()
    {
        using var sut = new PreferencesTabViewModel(_preferences, shortcuts: _map);
        var pull = sut.ShortcutItems.Single(i => i.Definition.Id == ShortcutIds.Pull);
        sut.StartShortcutCaptureCommand.Execute(pull);

        await sut.AssignShortcutAsync(pull, new KeyGesture('P', GestureModifiers.Ctrl | GestureModifiers.Shift));
        Assert.True(pull.IsCapturing);
        Assert.Contains("Push", pull.Message);
        Assert.Equal("Ctrl+Maj+L", _map.Display(ShortcutIds.Pull));

        await sut.AssignShortcutAsync(pull, new KeyGesture('K', GestureModifiers.None));
        Assert.True(pull.IsCapturing);
        Assert.True(pull.HasMessage);

        await sut.AssignShortcutAsync(pull, new KeyGesture('C', GestureModifiers.Ctrl));
        Assert.Contains("Copier", pull.Message);

        PreferencesTabViewModel.CancelShortcutCapture(pull);
        Assert.False(pull.IsCapturing);
        Assert.Null(pull.Message);
    }

    [Fact]
    public async Task The_preferences_tab_clears_resets_and_follows_changes_made_elsewhere()
    {
        using var sut = new PreferencesTabViewModel(_preferences, shortcuts: _map);
        var push = sut.ShortcutItems.Single(i => i.Definition.Id == ShortcutIds.Push);

        await sut.ClearShortcutCommand.ExecuteAsync(push);
        Assert.Equal("Aucun", push.GestureText);
        Assert.False(push.HasShortcut);
        Assert.True(push.IsCustomized);

        await sut.ResetShortcutCommand.ExecuteAsync(push);
        Assert.Equal("Ctrl+Maj+P", push.GestureText);
        Assert.True(push.HasShortcut);
        Assert.False(push.IsCustomized);

        await _preferences.UpdateAsync(p => p with { Shortcuts = [new ShortcutBinding(ShortcutIds.Push, "Ctrl+Alt+P")] }, TestContext.Current.CancellationToken);
        Assert.Equal("Ctrl+Alt+P", push.GestureText);

        await sut.ResetAllShortcutsCommand.ExecuteAsync(null);
        Assert.Equal("Ctrl+Maj+P", push.GestureText);
    }

    [Fact]
    public async Task Reset_is_refused_while_another_command_holds_the_default_gesture()
    {
        using var sut = new PreferencesTabViewModel(_preferences, shortcuts: _map);
        var pull = sut.ShortcutItems.Single(i => i.Definition.Id == ShortcutIds.Pull);
        var push = sut.ShortcutItems.Single(i => i.Definition.Id == ShortcutIds.Push);
        await _map.SetAsync(ShortcutIds.Pull, new KeyGesture('U', GestureModifiers.Ctrl | GestureModifiers.Alt));
        await _map.SetAsync(ShortcutIds.Push, new KeyGesture('L', GestureModifiers.Ctrl | GestureModifiers.Shift));

        await sut.ResetShortcutCommand.ExecuteAsync(pull);

        Assert.Contains("Push", pull.Message);
        Assert.Equal("Ctrl+Alt+U", pull.GestureText);
        Assert.Equal("Ctrl+Maj+L", push.GestureText);
    }

    [Fact]
    public void The_shortcuts_section_is_disabled_without_a_map()
    {
        using var sut = new PreferencesTabViewModel(_preferences);

        var section = sut.Sections.Single(s => s.Section == PreferencesSection.Shortcuts);
        Assert.False(section.IsEnabled);
        Assert.Empty(sut.ShortcutItems);
    }
}
