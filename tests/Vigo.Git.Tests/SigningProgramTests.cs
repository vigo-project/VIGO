using Vigo.Git.Cli;

namespace Vigo.Git.Tests;

public sealed class SigningProgramTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vigo-signing-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name, params string[] files)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        foreach (var file in files) File.WriteAllBytes(Path.Combine(dir, file), []);
        return dir;
    }

    [Fact]
    public void Default_program_is_resolved_on_the_PATH_skipping_the_MSYS_build_of_Git()
    {
        var gitUsrBin = Dir("git-usr-bin", "gpg.exe", "msys-2.0.dll");
        var gpg4win = Dir("gnupg-bin", "gpg.exe");
        var searchPath = string.Join(Path.PathSeparator, Dir("empty"), gitUsrBin, $"\"{gpg4win}\"");

        Assert.Equal(Path.Combine(gpg4win, "gpg.exe"), SigningProgram.Resolve(null, searchPath));
        // As read from "git config --get gpg.program": trailing newline.
        Assert.Equal(Path.Combine(gpg4win, "gpg.exe"), SigningProgram.Resolve("gpg\n", searchPath));
    }

    [Fact]
    public void Bare_names_other_than_gpg_are_resolved_too()
    {
        var gnupg = Dir("gnupg2", "gpg2.exe");

        Assert.Equal(Path.Combine(gnupg, "gpg2.exe"), SigningProgram.Resolve("gpg2", gnupg));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\GnuPG\bin\gpg.exe")]
    [InlineData("/usr/bin/gpg")]
    public void Explicit_path_is_left_to_git(string configured)
    {
        var gnupg = Dir("gnupg-bin", "gpg.exe");

        Assert.Null(SigningProgram.Resolve(configured, gnupg));
    }

    [Fact]
    public void Only_an_MSYS_build_on_the_PATH_keeps_git_resolution()
    {
        var cygwin = Dir("cygwin-bin", "gpg.exe", "cygwin1.dll");

        Assert.Null(SigningProgram.Resolve("gpg", string.Join(Path.PathSeparator, cygwin, Dir("missing"))));
    }

    [Theory]
    [InlineData("commit", true)]
    [InlineData("merge", true)]
    [InlineData("tag", true)]
    [InlineData("rebase", true)]
    [InlineData("show", true)]
    [InlineData("status", false)]
    [InlineData("config", false)]
    public void Only_commands_that_may_sign_get_the_program(string subcommand, bool expected) =>
        Assert.Equal(expected, SigningProgram.MaySign(subcommand));
}
