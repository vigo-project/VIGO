using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.App.Services;

/// <summary>
/// The texts of the application (Strings\fr-FR and Strings\en-US .resw, ADR-075): XAML reads them through <c>x:Uid</c>, the view
/// models through <see cref="Loc"/>. The language is chosen once at launch (<see cref="Apply"/>), before any view loads.
/// </summary>
public sealed class ResourceLocalizer : ILocalizer
{
    private readonly ResourceLoader _loader = new();

    /// <summary>Language tag of the resources in use (<c>fr-FR</c> or <c>en-US</c>).</summary>
    public static string LanguageTag { get; private set; } = "fr-FR";

    /// <summary>Resolves the preference, applies it to the resource system and the culture, and installs the localizer in <see cref="Loc"/>.</summary>
    public static void Apply(AppLanguage language)
    {
        LanguageTag = Resolve(language);
        ApplicationLanguages.PrimaryLanguageOverride = LanguageTag;
        var culture = CultureInfo.GetCultureInfo(LanguageTag);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Loc.Provider = new ResourceLocalizer();
    }

    /// <summary>The supported language for a preference: <see cref="AppLanguage.System"/> follows Windows (French, else English).</summary>
    public static string Resolve(AppLanguage language) => language switch
    {
        AppLanguage.French => "fr-FR",
        AppLanguage.English => "en-US",
        _ => string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "fr", StringComparison.OrdinalIgnoreCase) ? "fr-FR" : "en-US",
    };

    public string Lookup(string key)
    {
        // The .resw names use dots for the properties of x:Uid; from code a dot is a path separator.
        var text = _loader.GetString(key.Replace('.', '/'));
        return string.IsNullOrEmpty(text) ? key : text;
    }
}
