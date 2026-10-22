using System.Runtime.CompilerServices;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

/// <summary>Installs the French texts (Strings\fr-FR\Resources.resw) as the <see cref="Loc"/> provider, like the app does at launch.</summary>
internal sealed class FrenchTestLocalizer(Dictionary<string, string> texts) : ILocalizer
{
    [ModuleInitializer]
    internal static void Install() => Loc.Provider = new FrenchTestLocalizer(LocalizationResourcesTests.Load("fr-FR"));

    public string Lookup(string key) => texts.GetValueOrDefault(key, key);
}
