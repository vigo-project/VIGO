using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Windows.System;

namespace Vigo.App.Services;

/// <summary>
/// Shortcuts of the app, declared once: as XAML keyboard accelerators, and in a table read by the code editor relay
/// (ADR-038). Keys pressed in a WebView2 never reach the XAML accelerators, so the editor page sends the ones it does
/// not use back to the app, which runs the shortcut registered nearest to the editor.
/// </summary>
public static class KeyboardShortcuts
{
    /// <summary>One shortcut; key and modifiers follow the user's choice when it is a configurable command.</summary>
    private sealed class Shortcut(VirtualKey key, VirtualKeyModifiers modifiers, Func<bool> action, KeyboardAccelerator accelerator, string? commandId)
    {
        public VirtualKey Key { get; private set; } = key;

        public VirtualKeyModifiers Modifiers { get; private set; } = modifiers;

        public Func<bool> Action { get; } = action;

        public string? CommandId { get; } = commandId;

        /// <summary>Applies the gesture of the command; none disables the shortcut.</summary>
        public void Rebind(KeyGesture? gesture)
        {
            Key = gesture is { } g ? (VirtualKey)g.Key : VirtualKey.None;
            // GestureModifiers shares the values of VirtualKeyModifiers (Control, Menu, Shift).
            Modifiers = gesture is { } m ? (VirtualKeyModifiers)(int)m.Modifiers : VirtualKeyModifiers.None;
            accelerator.IsEnabled = gesture is not null;
            accelerator.Key = Key;
            accelerator.Modifiers = Modifiers;
        }
    }

    private static readonly ConditionalWeakTable<UIElement, List<Shortcut>> s_shortcuts = [];
    private static ShortcutMap? s_followed;

    /// <summary>
    /// Adds a shortcut to <paramref name="owner"/> (active while the focus is inside it, like an accelerator).
    /// <paramref name="action"/> returns whether it handled the key.
    /// </summary>
    public static void Add(UIElement owner, VirtualKey key, VirtualKeyModifiers modifiers, Func<bool> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => e.Handled = action();
        owner.KeyboardAccelerators.Add(accelerator);
        s_shortcuts.GetOrCreateValue(owner).Add(new Shortcut(key, modifiers, action, accelerator, null));
    }

    /// <summary>
    /// Adds the shortcut of a configurable command (<see cref="ShortcutIds"/>): its gesture is the one in the
    /// preferences, and follows them when the user changes it.
    /// </summary>
    public static void AddCommand(UIElement owner, string commandId, Func<bool> action)
    {
        var accelerator = new KeyboardAccelerator();
        accelerator.Invoked += (_, e) => e.Handled = action();
        owner.KeyboardAccelerators.Add(accelerator);
        var shortcut = new Shortcut(VirtualKey.None, VirtualKeyModifiers.None, action, accelerator, commandId);
        shortcut.Rebind(ShortcutMap.Current.Get(commandId));
        s_shortcuts.GetOrCreateValue(owner).Add(shortcut);
        Follow(ShortcutMap.Current);
    }

    /// <inheritdoc cref="AddCommand(UIElement, string, Func{bool})"/>
    public static void AddCommand(UIElement owner, string commandId, Action action) =>
        AddCommand(owner, commandId, () =>
        {
            action();
            return true;
        });

    // One subscription for the whole app (a map per window would keep closed windows alive through the event).
    private static void Follow(ShortcutMap map)
    {
        if (ReferenceEquals(s_followed, map)) return;
        s_followed = map;
        map.Changed += (_, _) =>
        {
            foreach (var (_, shortcuts) in s_shortcuts)
            {
                foreach (var shortcut in shortcuts)
                {
                    if (shortcut.CommandId is { } id) shortcut.Rebind(map.Get(id));
                }
            }
        };
    }

    /// <inheritdoc cref="Add(UIElement, VirtualKey, VirtualKeyModifiers, Func{bool})"/>
    public static void Add(UIElement owner, VirtualKey key, VirtualKeyModifiers modifiers, Action action) =>
        Add(owner, key, modifiers, () =>
        {
            action();
            return true;
        });

    /// <summary>
    /// Runs the shortcut matching the key registered on <paramref name="source"/> or its nearest ancestor (visual
    /// tree, as accelerators bubble); false when none handles it.
    /// </summary>
    public static bool TryInvoke(DependencyObject source, VirtualKey key, VirtualKeyModifiers modifiers)
    {
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is not UIElement ui || !s_shortcuts.TryGetValue(ui, out var shortcuts)) continue;
            foreach (var shortcut in shortcuts)
            {
                if (shortcut.Key != VirtualKey.None && shortcut.Key == key && shortcut.Modifiers == modifiers && shortcut.Action()) return true;
            }
        }
        return false;
    }
}
