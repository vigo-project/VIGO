using System.Diagnostics;
using System.Text;
using Vigo.Git.Cli;

namespace Vigo.TestUtilities;

/// <summary>
/// Temporary Git repository generated on the fly with git.exe (real behavior, isolated hooks and config).
/// Deleted on Dispose. Commit dates are deterministic so that SHAs are reproducible.
/// </summary>
public sealed class TestRepoBuilder : IDisposable
{
    private int _tick;

    private TestRepoBuilder(string path) => Path = path;

    public string Path { get; }

    /// <summary>Creates an empty repository on the given initial branch.</summary>
    public static TestRepoBuilder Create(string initialBranch = "main", bool bare = false)
    {
        // Copy of a repository initialized (and configured) once per process: a copy of a few files instead of 6 git.exe runs.
        var repo = new TestRepoBuilder(NewDirectory());
        CopyDirectory(Template(initialBranch, bare), repo.Path);
        return repo;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Branch, bool Bare), Lazy<string>> s_templates = new();

    /// <summary>The initialized repository every <see cref="Create"/> copies; built on first use, removed when the process ends.</summary>
    private static string Template(string initialBranch, bool bare) =>
        s_templates.GetOrAdd((initialBranch, bare), key => new Lazy<string>(() =>
        {
            var template = new TestRepoBuilder(NewDirectory());
            template.Git("init", "--initial-branch", key.Branch, key.Bare ? "--bare" : "--quiet");
            if (!key.Bare) template.Configure();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => template.Dispose();
            return template.Path;
        })).Value;

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, file)));
    }

    /// <summary>Clones <paramref name="source"/> (a path, e.g. a bare repository used as remote "origin").</summary>
    /// <param name="firstTick">First commit date offset, so that clones do not create identical commits.</param>
    public static TestRepoBuilder Clone(string source, int firstTick = 1_000)
    {
        var repo = new TestRepoBuilder(NewDirectory()) { _tick = firstTick };
        repo.Git("clone", "--quiet", source, ".");
        repo.Configure();
        return repo;
    }

    /// <summary>
    /// Lets git clone submodules from local paths (the "file" transport is refused for them otherwise, and the
    /// repository-local configuration does not lift that). Set for the test process through git's environment
    /// configuration, so that it also reaches the git.exe started by <c>GitService</c>.
    /// </summary>
    public static void AllowFileSubmodules()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
    }

    /// <summary>Writes a hook script (LF line endings, no BOM), executable outside Windows: git skips a hook that is not.</summary>
    public static void WriteHook(string path, string script)
    {
        File.WriteAllText(path, script.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>Wraps a repository created elsewhere (e.g. cloned by VIGO); disposing it deletes the folder.</summary>
    public static TestRepoBuilder Attach(string path)
    {
        var builder = new TestRepoBuilder(path);
        // A clone has no identity of its own: commits made through the builder must not depend on the machine's
        // configuration (a CI runner has none).
        builder.Git("config", "user.name", "Vigo Test");
        builder.Git("config", "user.email", "test@vigo.dev");
        builder.Git("config", "commit.gpgsign", "false");
        return builder;
    }

    private static string NewDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vigo-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Identity, no signing, no line-ending conversion; no fsync and no background maintenance (the tests do not need
    /// either, and they cost a process or a disk flush per operation). Written to the config file: no git.exe run.
    /// </summary>
    private void Configure() => File.AppendAllText(
        System.IO.Path.Combine(Path, ".git", "config"),
        "[user]\n\tname = Vigo Test\n\temail = test@vigo.dev\n[commit]\n\tgpgsign = false\n[tag]\n\tgpgsign = false\n"
        + "[core]\n\tautocrlf = false\n\tfsync = none\n[gc]\n\tauto = 0\n[maintenance]\n\tauto = false\n");

    /// <summary>Writes a file (relative path, '/' separators).</summary>
    public TestRepoBuilder WriteFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return this;
    }

    /// <summary>Deletes a file of the working directory (relative path, '/' separators).</summary>
    public TestRepoBuilder DeleteFile(string relativePath)
    {
        File.Delete(System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        return this;
    }

    /// <summary>Stages the given paths (<c>git add</c>).</summary>
    public TestRepoBuilder Stage(params string[] paths)
    {
        Git(["add", "--", .. paths]);
        return this;
    }

    /// <summary>Stages everything and creates a commit; returns its SHA.</summary>
    public string Commit(string message, params (string Path, string Content)[] files)
    {
        foreach (var (p, c) in files) WriteFile(p, c);
        Git("add", "--all");
        Git(NextDate(), "commit", "--allow-empty", "--quiet", "-m", message);
        return Head();
    }

    /// <summary>Merges <paramref name="branch"/> into the current branch with a merge commit; returns its SHA.</summary>
    public string Merge(string branch, string? message = null)
    {
        Git(NextDate(), "merge", "--no-ff", "--quiet", "-m", message ?? $"Merge branch '{branch}'", branch);
        return Head();
    }

    /// <summary>Creates a lightweight tag, or an annotated one when <paramref name="message"/> is given.</summary>
    public TestRepoBuilder Tag(string name, string? message = null, string target = "HEAD")
    {
        if (message is null) Git("tag", name, target);
        else Git(NextDate(), "tag", "--annotate", name, "-m", message, target);
        return this;
    }

    /// <summary>Stashes the working directory changes (tracked files must be modified first).</summary>
    public TestRepoBuilder Stash(string message)
    {
        Git(NextDate(), "stash", "push", "--quiet", "-m", message);
        return this;
    }

    public TestRepoBuilder Branch(string name, bool checkout = true)
    {
        Git(checkout ? ["checkout", "--quiet", "-b", name] : ["branch", name]);
        return this;
    }

    public TestRepoBuilder Checkout(string name)
    {
        Git("checkout", "--quiet", name);
        return this;
    }

    public string Head() => ReadHead() ?? Git("rev-parse", "HEAD").Trim();

    /// <summary>HEAD read from the files of the repository (a loose or packed ref, or a detached HEAD); null when unsure (git.exe then answers).</summary>
    private string? ReadHead()
    {
        var gitDirectory = System.IO.Path.Combine(Path, ".git");
        try
        {
            var head = File.ReadAllText(System.IO.Path.Combine(gitDirectory, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return IsSha(head) ? head : null;

            var reference = head[5..];
            var loose = System.IO.Path.Combine(gitDirectory, reference.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(loose)) return File.ReadAllText(loose).Trim() is var sha && IsSha(sha) ? sha : null;

            var packed = System.IO.Path.Combine(gitDirectory, "packed-refs");
            if (!File.Exists(packed)) return null;
            foreach (var line in File.ReadLines(packed))
            {
                if (line.EndsWith(' ' + reference, StringComparison.Ordinal) && IsSha(line[..^(reference.Length + 1)])) return line[..^(reference.Length + 1)];
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsSha(string text) => text.Length is 40 or 64 && text.All(char.IsAsciiHexDigit);

    /// <summary>Runs git and returns stdout; throws if the exit code is non-zero.</summary>
    public string Git(params string[] args) => Git(null, args);

    public string Git(IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo(GitExecutable.Path ?? "git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Isolate from the machine's global and system config.
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = System.IO.Path.Combine(Path, "..", "empty.gitconfig");
        psi.Environment["LC_ALL"] = "C";
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }

    /// <summary>Author and committer dates one minute after the previous ones.</summary>
    private Dictionary<string, string> NextDate()
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + 60 * _tick++)
            .ToString("yyyy-MM-ddTHH:mm:ssK", System.Globalization.CultureInfo.InvariantCulture);
        return new Dictionary<string, string> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date };
    }

    public void Dispose()
    {
        if (!TryDelete(Path)) s_undeleted.Add(Path);
    }

    // ---- Clean-up of the test folders ---------------------------------------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentBag<string> s_undeleted = [];

    /// <summary>Folders older than this belong to a run that was interrupted (a suite lasts well under that).</summary>
    private static readonly TimeSpan s_staleAfter = TimeSpan.FromHours(2);

    static TestRepoBuilder()
    {
        // A folder that could not be deleted (a watcher or a process still holds it) is tried again when the run ends.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var path in s_undeleted) TryDelete(path, attempts: 3);
        };
        // The folders of earlier, interrupted runs: removed in the background, the tests do not wait for it.
        _ = Task.Run(SweepStaleFolders);
    }

    private static void SweepStaleFolders()
    {
        try
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vigo-tests");
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > s_staleAfter) TryDelete(directory, attempts: 1);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deletes a folder, git's read-only objects included. A few tries with a short wait: a process that is still ending, or
    /// a watcher being disposed, releases the folder a moment later. False when it is still there.
    /// </summary>
    private static bool TryDelete(string path, int attempts = 6)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(entry, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (attempt < attempts) Thread.Sleep(50 * attempt);
        }
        return false;
    }
}
