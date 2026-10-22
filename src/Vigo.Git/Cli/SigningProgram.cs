using System.Collections.Concurrent;

namespace Vigo.Git.Cli;

/// <summary>
/// Resolves the OpenPGP program git.exe should sign with. Git for Windows puts its own <c>usr\bin</c> first on the
/// PATH of its subprocesses, so a bare <c>gpg.program</c> (the default <c>gpg</c>) runs the MSYS gpg bundled with
/// Git, whose keyring (<c>~/.gnupg</c>) is not the one of a Windows install such as Gpg4win (<c>%APPDATA%\gnupg</c>):
/// signing then fails with "No secret key". A bare name is therefore resolved on VIGO's own PATH (the Windows one),
/// skipping MSYS builds, and handed to git as a full path. An explicit path in the config is left untouched.
/// </summary>
internal static class SigningProgram
{
    /// <summary>Subcommands that may sign (commit.gpgSign, tag.gpgSign, -S) or verify signatures.</summary>
    private static readonly HashSet<string> SigningCommands =
        ["commit", "tag", "merge", "cherry-pick", "revert", "rebase", "pull", "am", "verify-commit", "verify-tag", "show"];

    private static readonly ConcurrentDictionary<string, string?> s_resolved = new(StringComparer.OrdinalIgnoreCase);

    public static bool MaySign(string subcommand) => SigningCommands.Contains(subcommand);

    /// <summary>
    /// Full path of the Windows program to use for the configured <c>gpg.program</c> (null when unset), or null when
    /// git's own resolution must be kept: explicit path, or no Windows (non-MSYS) build of that program on the PATH.
    /// </summary>
    public static string? Resolve(string? configured) =>
        Resolve(configured, Environment.GetEnvironmentVariable("PATH"));

    internal static string? Resolve(string? configured, string? searchPath)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "gpg" : configured.Trim();
        if (name.IndexOfAny(['/', '\\', ':']) >= 0) return null;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return s_resolved.GetOrAdd($"{name}|{searchPath}", _ => Find(name, searchPath));
    }

    private static string? Find(string fileName, string? searchPath)
    {
        foreach (var entry in (searchPath ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = entry.Trim().Trim('"');
            if (dir.Length == 0) continue;
            var candidate = Path.Combine(dir, fileName);
            // An MSYS build (Git's usr\bin, MSYS2, Cygwin) has the same keyring problem: what git would run anyway.
            if (File.Exists(candidate) && !IsMsysDirectory(dir)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static bool IsMsysDirectory(string dir) =>
        File.Exists(Path.Combine(dir, "msys-2.0.dll")) || File.Exists(Path.Combine(dir, "cygwin1.dll"));
}
