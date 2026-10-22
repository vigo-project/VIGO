namespace Vigo.Core.Services;

/// <summary>Release notes embedded from <c>Changelog/CHANGELOG-*.md</c>: one "## version — date" section per pushed tag.</summary>
public static class Changelog
{
    /// <summary>The notes in the UI language; empty when the file is missing.</summary>
    public static string Text => field ??= Load();

    private static string Load()
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream(Loc.Get("Preferences.About.ChangelogFile"));
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Body of the section of <paramref name="version"/> (without its heading); null when it has none.</summary>
    public static string? SectionOf(string markdown, string version)
    {
        var heading = "## " + version;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l == heading || l.StartsWith(heading + " ", StringComparison.Ordinal));
        if (start < 0) return null;
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        return string.Join('\n', lines[(start + 1)..(end < 0 ? lines.Length : end)]).Trim();
    }
}
