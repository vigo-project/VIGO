namespace Vigo.Git.Cli;

/// <summary>Locates git.exe (PATH, then standard install locations).</summary>
public static class GitExecutable
{
    private static readonly Lazy<string?> s_path = new(Locate);

    /// <summary>Full path of git.exe, or null when not found.</summary>
    public static string? Path => s_path.Value;

    private static string? Locate()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir.Trim('"'), "git.exe");
            if (File.Exists(candidate)) return candidate;
        }

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs",
        ];
        foreach (var root in roots)
        {
            var candidate = System.IO.Path.Combine(root, "Git", "cmd", "git.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
