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

    /// <summary>
    /// Notes to show after an update: the sections newer than <paramref name="lastSeen"/> up to <paramref name="current"/>, newest
    /// first, each under its heading (a single section: its body alone). Only the current section when nothing was seen before.
    /// </summary>
    public static string? NotesSince(string markdown, string current, string? lastSeen)
    {
        if (ParseVersion(current) is not { } cur || lastSeen is null || ParseVersion(lastSeen) is not { } seen)
            return SectionOf(markdown, current) is { Length: > 0 } only ? only : null;

        List<string> bodies = [], headings = [];
        foreach (var heading in markdown.Replace("\r\n", "\n").Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal)))
        {
            var version = heading[3..].Split(' ')[0];
            // shortcut: a pre-release suffix is ignored (0.2.0-beta.1 compares as 0.2.0), enough for notes tied to released versions.
            if (ParseVersion(version) is not { } v || v <= seen || v > cur) continue;
            if (SectionOf(markdown, version) is not { Length: > 0 } body) continue;
            headings.Add(heading);
            bodies.Add(body);
        }
        return bodies.Count switch
        {
            0 => null,
            1 => bodies[0],
            _ => string.Join("\n\n", headings.Select((h, i) => h + "\n" + bodies[i])),
        };
    }

    private static Version? ParseVersion(string version) =>
        Version.TryParse(version.Split('-', '+')[0], out var v) ? v : null;
}
