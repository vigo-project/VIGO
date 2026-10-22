using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class GitCommandLogTests
{
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "vigo-log-tests");

    [Fact]
    public void Command_line_quotes_arguments_with_spaces()
    {
        Assert.Equal("git commit -m \"two words\" --amend", GitCommandLog.FormatCommandLine(["commit", "-m", "two words", "--amend"]));
        Assert.Equal("git commit -m \"say \\\"hi\\\"\"", GitCommandLog.FormatCommandLine(["commit", "-m", "say \"hi\""]));
    }

    [Fact]
    public void Credentials_are_masked_in_urls_and_authorization_settings()
    {
        Assert.Equal("git push https://***@github.com/a/b.git main",
            GitCommandLog.FormatCommandLine(["push", "https://user:secret@github.com/a/b.git", "main"]));
        Assert.Equal("git -c *** fetch", GitCommandLog.FormatCommandLine(["-c", "http.extraHeader=Authorization: Bearer abc", "fetch"]));
        Assert.Equal("fatal: unable to access 'https://***@host/r.git/'", GitCommandLog.FormatOutput(null, "fatal: unable to access 'https://tok@host/r.git/'"));
    }

    [Theory]
    [InlineData("status", "--porcelain=v2")]
    [InlineData("-c", "core.quotepath=off", "log", "--oneline")]
    [InlineData("config", "--get", "user.name")]
    [InlineData("for-each-ref")]
    public void Read_commands_are_recognized(params string[] arguments) => Assert.True(GitCommandLog.IsRead(arguments));

    [Theory]
    [InlineData("push", "origin", "main")]
    [InlineData("fetch", "--all")]
    [InlineData("config", "user.name", "Ada")]
    [InlineData("checkout", "main")]
    [InlineData("some-future-command")]
    public void Writes_network_and_unknown_commands_are_not_reads(params string[] arguments) => Assert.False(GitCommandLog.IsRead(arguments));

    [Fact]
    public void A_run_without_working_directory_is_not_listed()
    {
        var log = new GitCommandLog();
        var started = 0;
        log.Started += _ => started++;

        Assert.Null(log.Begin(null, ["clone", "url"]));
        Assert.Equal(0, started);
    }

    [Fact]
    public void Reads_print_nothing_when_they_succeed_but_keep_their_errors()
    {
        var log = new GitCommandLog();
        var ok = log.Begin(Repo, ["status"])!;
        ok.Complete(0, "huge listing", "");
        var failed = log.Begin(Repo, ["log"])!;
        failed.Complete(128, "", "fatal: bad revision");

        Assert.Equal(string.Empty, ok.Output);
        Assert.Equal("fatal: bad revision", failed.Output);
    }

    [Fact]
    public void Output_is_truncated()
    {
        var run = new GitCommandLog().Begin(Repo, ["push"])!;
        run.Complete(0, new string('x', 10_000));

        Assert.True(run.Output.Length < 4100);
        Assert.EndsWith("…", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void View_model_lists_the_commands_of_its_repository_only_and_follows_their_end()
    {
        var log = new GitCommandLog();
        using var sut = new GitCommandLogViewModel(log, Repo);

        var other = log.Begin(Path.Combine(Path.GetTempPath(), "vigo-other"), ["push"])!;
        var push = log.Begin(Repo + Path.DirectorySeparatorChar, ["push", "origin", "main"])!;
        Assert.Single(sut.Entries);
        Assert.True(sut.Entries[0].IsRunning);
        Assert.Equal("git push origin main", sut.Entries[0].CommandLine);

        push.Complete(1, "", "rejected");
        other.Complete(0);

        var entry = sut.Entries[0];
        Assert.False(entry.IsRunning);
        Assert.True(entry.IsFailed);
        Assert.StartsWith("code 1", entry.Summary, StringComparison.Ordinal);
        Assert.Equal("rejected", entry.Output);
        Assert.False(sut.IsEmpty);
    }

    [Fact]
    public void Reads_are_listed_only_when_everything_is_shown_and_clear_empties_the_list()
    {
        var log = new GitCommandLog();
        using var sut = new GitCommandLogViewModel(log, Repo);

        log.Begin(Repo, ["status"])!.Complete(0);
        Assert.Empty(sut.Entries);

        sut.ShowAll = true;
        log.Begin(Repo, ["status"])!.Complete(0);
        Assert.Single(sut.Entries);
        Assert.True(sut.Entries[0].IsSucceeded);

        sut.ClearCommand.Execute(null);
        Assert.Empty(sut.Entries);
        Assert.True(sut.IsEmpty);
    }

    [Fact]
    public void Disposed_view_model_stops_listening()
    {
        var log = new GitCommandLog();
        var sut = new GitCommandLogViewModel(log, Repo);
        sut.Dispose();

        log.Begin(Repo, ["push"]);

        Assert.Empty(sut.Entries);
    }
}
