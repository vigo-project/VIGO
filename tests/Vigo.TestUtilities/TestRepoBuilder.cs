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
        var repo = new TestRepoBuilder(NewDirectory());
        repo.Git("init", "--initial-branch", initialBranch, bare ? "--bare" : "--quiet");
        if (!bare) repo.Configure();
        return repo;
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

    private void Configure()
    {
        Git("config", "user.name", "Vigo Test");
        Git("config", "user.email", "test@vigo.dev");
        Git("config", "commit.gpgsign", "false");
        Git("config", "tag.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

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

    public string Head() => Git("rev-parse", "HEAD").Trim();

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
        try
        {
            // Git objects are read-only: clear the attribute before deleting.
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { /* file still locked: the temp folder will be purged later */ }
        catch (UnauthorizedAccessException) { }
    }
}
