namespace Vigo.Core.Services;

/// <summary>Font families installed on the machine, for the font pickers of the preferences.</summary>
public interface IFontCatalog
{
    /// <summary>Family names sorted by name; the fixed-width ones only when <paramref name="monospaceOnly"/>. Read off the UI thread.</summary>
    IReadOnlyList<string> GetFamilies(bool monospaceOnly);
}

/// <summary>The catalog of the running app (set once at startup, like <see cref="ShortcutMap.Current"/>); empty in tests.</summary>
public static class FontCatalog
{
    public static IFontCatalog? Current { get; set; }
}
