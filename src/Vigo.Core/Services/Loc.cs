using System.Globalization;

namespace Vigo.Core.Services;

/// <summary>Source of the localized texts shown by the view models (implemented by the app on top of its .resw resources).</summary>
public interface ILocalizer
{
    /// <summary>The text of a resource key; the key itself when it is unknown (a missing text stays visible, never throws).</summary>
    string Lookup(string key);
}

/// <summary>
/// Static access to the localized texts for code that has no UI dependency (view models, models), like
/// <c>ShortcutMap.Current</c>: the app installs its <see cref="ILocalizer"/> once at launch, before any view model exists.
/// Keys are PascalCase and dotted by area (<c>Preferences.SshAgent.Running</c>), the same in every .resw file.
/// </summary>
public static class Loc
{
    /// <summary>Set once, at launch. Until then (tests) the keys themselves are returned.</summary>
    public static ILocalizer Provider { get; set; } = KeyLocalizer.Instance;

    public static string Get(string key) => Provider.Lookup(key);

    /// <summary>A text with <c>{0}</c> placeholders (composite format) filled with the arguments, in the current culture.</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Provider.Lookup(key), args);

    private sealed class KeyLocalizer : ILocalizer
    {
        public static KeyLocalizer Instance { get; } = new();

        public string Lookup(string key) => key;
    }
}
