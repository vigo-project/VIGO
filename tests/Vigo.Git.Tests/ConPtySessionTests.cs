using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Vigo.Core.Models;
using Vigo.Git.Terminal;

namespace Vigo.Git.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConPtySessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>Reads the output until <paramref name="text"/> appears (or the output ends, or 20 s pass).</summary>
    private static async Task<string> ReadUntilAsync(ConPtySession session, string text)
    {
        var all = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await foreach (var chunk in session.Output.ReadAllAsync(timeout.Token))
            {
                all.Append(Encoding.UTF8.GetString(chunk));
                if (all.ToString().Contains(text, StringComparison.Ordinal)) break;
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            // Reported by the assertion with what was read.
        }
        return all.ToString();
    }

    [Fact]
    public async Task Command_output_comes_back_and_exit_code_is_reported()
    {
        using var session = ConPtySession.Start($"\"{Cmd}\" /c echo vigo-hello & exit 3", Environment.CurrentDirectory, 80, 24);

        var output = await ReadUntilAsync(session, "vigo-hello");

        Assert.Contains("vigo-hello", output);
        Assert.Equal(3, await session.Exited.WaitAsync(TimeSpan.FromSeconds(20), Ct));
    }

    [Fact]
    public async Task Typed_input_reaches_the_shell()
    {
        using var session = ConPtySession.Start($"\"{Cmd}\"", Environment.CurrentDirectory, 80, 24);
        await ReadUntilAsync(session, ">");

        session.Write("echo vigo-$((6*7))x\r"u8);
        // cmd does not evaluate $(( )): the echoed line appears as typed, which proves the input path.
        var output = await ReadUntilAsync(session, "vigo-$((6*7))x");

        Assert.Contains("vigo-$((6*7))x", output);
    }

    [Fact]
    public async Task Working_directory_is_the_one_given()
    {
        var directory = Directory.CreateTempSubdirectory("vigo-conpty-");
        try
        {
            using var session = ConPtySession.Start($"\"{Cmd}\" /c cd", directory.FullName, 80, 24);

            var output = await ReadUntilAsync(session, directory.Name);

            Assert.Contains(directory.Name, output);
            // The shell holds the folder as its current directory until it is gone.
            await session.Exited.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        }
        finally
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    directory.Delete(recursive: true);
                    break;
                }
                catch (IOException)
                {
                    // Released a moment after the process exit.
                    await Task.Delay(100, Ct);
                }
            }
        }
    }

    [Fact]
    public async Task Resize_is_seen_by_the_shell()
    {
        using var session = ConPtySession.Start($"\"{Cmd}\"", Environment.CurrentDirectory, 80, 24);
        await ReadUntilAsync(session, ">");

        session.Resize(120, 40);
        session.Write("mode con\r"u8);
        var output = await ReadUntilAsync(session, "120");

        Assert.Contains("120", output);
    }

    [Fact]
    public async Task Disposing_kills_the_shell_and_what_it_started()
    {
        var marker = $"vigo-marker-{Guid.NewGuid():N}";
        // The shell starts a child that outlives it unless the job object kills it.
        var session = ConPtySession.Start($"\"{Cmd}\" /c ping -n 120 127.0.0.1 -w 1000 > nul & rem {marker}", Environment.CurrentDirectory, 80, 24);
        var ping = await WaitForProcessAsync(session);
        Assert.NotNull(ping);

        session.Dispose();

        Assert.True(await ping.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct).ContinueWith(t => t.IsCompletedSuccessfully, Ct));
        ping.Dispose();
    }

    // The ping started by the test shell: the newest ping.exe whose parent is a cmd of this test (found by polling).
    private static async Task<Process?> WaitForProcessAsync(ConPtySession session)
    {
        _ = session;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var started = Process.GetProcessesByName("ping").OrderByDescending(p => p.StartTime).FirstOrDefault();
            if (started is not null && DateTime.Now - started.StartTime < TimeSpan.FromSeconds(30)) return started;
            await Task.Delay(100, Ct);
        }
        return null;
    }

    [Fact]
    public void A_missing_program_fails_with_IOException()
    {
        Assert.Throws<IOException>(() => ConPtySession.Start(@"C:\this\does\not\exist.exe", Environment.CurrentDirectory, 80, 24));
    }

    [Fact]
    public void Detected_shells_have_unique_ids_and_the_command_prompt_is_there()
    {
        var shells = new WindowsShellLocator().Detect();

        Assert.Contains(shells, s => s.Id == TerminalShellIds.Cmd);
        Assert.Equal(shells.Count, shells.Select(s => s.Id).Distinct().Count());
        Assert.All(shells, s => Assert.False(string.IsNullOrWhiteSpace(s.CommandLine)));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe", "\"C:\\Program Files\\Git\\bin\\bash.exe\"")]
    [InlineData(@"C:\Windows\System32\cmd.exe", @"C:\Windows\System32\cmd.exe")]
    public void Paths_with_spaces_are_quoted(string path, string expected) => Assert.Equal(expected, WindowsShellLocator.Quote(path));
}
