using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Vigo.Core.Services;

namespace Vigo.Git.Tests;

/// <summary>Installs the French texts (Strings\fr-FR\Resources.resw) as the <see cref="Loc"/> provider, like the app does at launch.</summary>
internal sealed class FrenchTestLocalizer(Dictionary<string, string> texts) : ILocalizer
{
    [ModuleInitializer]
    internal static void Install() => Loc.Provider = new FrenchTestLocalizer(Load());

    public string Lookup(string key) => texts.GetValueOrDefault(key, key);

    private static Dictionary<string, string> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VIGO.slnx"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("VIGO.slnx not found above " + AppContext.BaseDirectory);
        return XDocument.Load(Path.Combine(root, "src", "Vigo.App", "Strings", "fr-FR", "Resources.resw")).Root!
            .Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!, StringComparer.Ordinal);
    }
}
