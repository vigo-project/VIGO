using System.Globalization;
using System.Text;

namespace Vigo.Core.Models;

/// <summary>Modifier keys of a gesture; values match <c>Windows.System.VirtualKeyModifiers</c> (Control, Menu, Shift).</summary>
[Flags]
public enum GestureModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>
/// A key with its modifiers, as a keyboard shortcut. <see cref="Key"/> is a Windows virtual-key code (the active
/// keyboard layout, like the XAML accelerators and the editor page's <c>keyCode</c>), so it carries no UI type.
/// </summary>
public readonly record struct KeyGesture(int Key, GestureModifiers Modifiers)
{
    public const int Backspace = 8, Tab = 9, Enter = 13, Escape = 27, Space = 32, Delete = 46;

    // Names stored in preferences.json (invariant), then the ones shown to the user.
    private static readonly (int Key, string Name, string Display)[] s_keys = BuildKeys();

    private static readonly Dictionary<string, int> s_byName =
        s_keys.ToDictionary(k => k.Name, k => k.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, (string Name, string Display)> s_byKey =
        s_keys.ToDictionary(k => k.Key, k => (k.Name, k.Display));

    /// <summary>Shift, Ctrl, Alt (Menu) and the Windows keys: a gesture is never made of them alone.</summary>
    public static bool IsModifierKey(int key) => key is 16 or 17 or 18 or 91 or 92 or (>= 160 and <= 165);

    /// <summary>The key can be part of a shortcut (letters, digits, F1–F12, navigation and punctuation keys).</summary>
    public static bool IsSupportedKey(int key) => s_byKey.ContainsKey(key) && key != Escape;

    /// <summary>
    /// Usable as a shortcut: a supported key with at least one modifier, or a function key alone (a bare letter would
    /// be typed into the text fields).
    /// </summary>
    public bool IsValid => IsSupportedKey(Key) && (Modifiers != GestureModifiers.None || Key is >= 112 and <= 123);

    /// <summary>Text for <c>preferences.json</c>: <c>Ctrl+Shift+L</c>.</summary>
    public string Serialize() => Format(display: false);

    /// <summary>Text for the user: <c>Ctrl+Maj+L</c>.</summary>
    public override string ToString() => Format(display: true);

    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var modifiers = GestureModifiers.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= GestureModifiers.Ctrl; break;
                case "ALT": modifiers |= GestureModifiers.Alt; break;
                case "SHIFT" or "MAJ": modifiers |= GestureModifiers.Shift; break;
                default: return false;
            }
        }
        if (!s_byName.TryGetValue(parts[^1], out var key)) return false;
        var parsed = new KeyGesture(key, modifiers);
        if (!parsed.IsValid) return false;
        gesture = parsed;
        return true;
    }

    private string Format(bool display)
    {
        var text = new StringBuilder();
        if (Modifiers.HasFlag(GestureModifiers.Ctrl)) text.Append("Ctrl+");
        if (Modifiers.HasFlag(GestureModifiers.Alt)) text.Append("Alt+");
        if (Modifiers.HasFlag(GestureModifiers.Shift)) text.Append(display ? "Maj+" : "Shift+");
        if (s_byKey.TryGetValue(Key, out var names)) text.Append(display ? names.Display : names.Name);
        else text.Append(CultureInfo.InvariantCulture, $"VK{Key}");
        return text.ToString();
    }

    private static (int, string, string)[] BuildKeys()
    {
        var keys = new List<(int, string, string)>
        {
            (Backspace, "Backspace", "Retour"), (Tab, "Tab", "Tab"), (Enter, "Enter", "Entrée"), (Escape, "Escape", "Échap"),
            (Space, "Space", "Espace"), (33, "PageUp", "Page préc."), (34, "PageDown", "Page suiv."), (35, "End", "Fin"),
            (36, "Home", "Début"), (37, "Left", "←"), (38, "Up", "↑"), (39, "Right", "→"), (40, "Down", "↓"),
            (45, "Insert", "Inser"), (Delete, "Delete", "Suppr"),
            (186, ";", ";"), (187, "=", "="), (188, ",", ","), (189, "-", "-"), (190, ".", "."), (191, "/", "/"),
            (192, "`", "`"), (219, "[", "["), (220, "\\", "\\"), (221, "]", "]"), (222, "'", "'"),
        };
        for (var digit = 0; digit <= 9; digit++)
        {
            var name = digit.ToString(CultureInfo.InvariantCulture);
            keys.Add((48 + digit, name, name));
        }
        for (var letter = 0; letter < 26; letter++)
        {
            var name = ((char)('A' + letter)).ToString();
            keys.Add((65 + letter, name, name));
        }
        for (var f = 1; f <= 12; f++)
        {
            var name = "F" + f.ToString(CultureInfo.InvariantCulture);
            keys.Add((111 + f, name, name));
        }
        return [.. keys];
    }
}
