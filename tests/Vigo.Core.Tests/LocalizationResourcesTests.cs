using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

/// <summary>Consistency of the .resw files of the application (ADR-075): same keys in both languages, no missing key.</summary>
public sealed partial class LocalizationResourcesTests
{
    private static readonly string SourceRoot = FindSourceRoot();

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VIGO.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("VIGO.slnx not found above " + AppContext.BaseDirectory);
    }

    internal static Dictionary<string, string> Load(string language) =>
        XDocument.Load(Path.Combine(SourceRoot, "src", "Vigo.App", "Strings", language, "Resources.resw")).Root!
            .Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!, StringComparer.Ordinal);

    [Fact]
    public void French_and_english_define_the_same_keys()
    {
        var french = Load("fr-FR");
        var english = Load("en-US");

        Assert.Empty(french.Keys.Except(english.Keys));
        Assert.Empty(english.Keys.Except(french.Keys));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Changelog_file_of_each_language_is_embedded(string language)
    {
        var name = Load(language)["Preferences.About.ChangelogFile"];

        using var stream = typeof(Vigo.Core.ViewModels.PreferencesTabViewModel).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
    }

    [Fact]
    public void No_key_is_also_the_scope_of_another()
    {
        // The resource compiler (PRI278) refuses "Refs.LoadMore" next to "Refs.LoadMore.Label".
        var keys = Load("fr-FR").Keys.ToList();

        Assert.All(keys, key => Assert.DoesNotContain(keys, other => other.StartsWith(key + ".", StringComparison.Ordinal)));
    }

    [Fact]
    public void Texts_are_not_empty_and_keep_the_same_placeholders()
    {
        var french = Load("fr-FR");
        var english = Load("en-US");

        foreach (var (key, text) in french)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{key}: empty French text");
            Assert.False(string.IsNullOrWhiteSpace(english[key]), $"{key}: empty English text");
            Assert.Equal(Placeholders(text), Placeholders(english[key]));
        }
    }

    [Fact]
    public void Keys_used_by_the_code_exist()
    {
        var keys = Load("fr-FR").Keys.ToHashSet(StringComparer.Ordinal);
        // Any string literal that looks like a key of an area (Refs., Toolbar., ...): also finds keys chosen by a condition.
        var areas = keys.Where(k => k.Contains('.') && !k.Contains(".[")).Select(k => k[..k.IndexOf('.')]).Distinct().ToList();
        var literal = new Regex(@"""(?<key>(?:" + string.Join('|', areas) + @")\.[A-Za-z0-9.]+)""");
        var used = Directory.EnumerateFiles(Path.Combine(SourceRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => literal.Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value))
            .Distinct().ToList();

        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.Contains(k, keys));
    }

    [Fact]
    public void Uids_used_by_the_xaml_have_resources()
    {
        var names = Load("fr-FR").Keys.ToList();
        var uids = Directory.EnumerateFiles(Path.Combine(SourceRoot, "src", "Vigo.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => XamlUid().Matches(File.ReadAllText(f)).Select(m => m.Groups["uid"].Value))
            .Distinct().ToList();

        Assert.NotEmpty(uids);
        Assert.All(uids, u => Assert.Contains(names, n => n.StartsWith(u + ".", StringComparison.Ordinal)));
    }

    [Fact]
    public void Format_fills_the_placeholders_and_an_unknown_key_is_returned_as_is()
    {
        Assert.Equal("Toutes les 5 minutes", Loc.Format("Preferences.AutoFetch.EveryMinutes", 5));
        Assert.Equal("Some.Unknown.Key", Loc.Get("Some.Unknown.Key"));
    }

    private static string Placeholders(string text) =>
        string.Join(",", Placeholder().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal));

    [GeneratedRegex(@"x:Uid=""(?<uid>[^""]+)""")]
    private static partial Regex XamlUid();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
