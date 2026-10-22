using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Default encoding of a repository (J7.29): <c>gui.encoding</c> decodes the files and blobs shown.</summary>
public sealed class RepositoryEncodingTests : IDisposable
{
    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();

    // "cafè" in Windows-1252 is "cafč" in Windows-1250: the same byte 0xE8.
    private static readonly byte[] Bytes = [.. "caf"u8, 0xE8, (byte)'\n'];

    public RepositoryEncodingTests()
    {
        _repo.Commit("one", ("a.txt", "1\n"));
        File.WriteAllBytes(Path.Combine(_repo.Path, "e.txt"), Bytes);
        _repo.Stage("e.txt");
        _repo.Commit("two");
    }

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string?> ReadAsync(FileVersion version) =>
        Assert.IsType<Success<string?>>((await _git.ReadFileTextAsync(_repo.Path, version, Ct)).Value).Value;

    [Fact]
    public async Task The_setting_round_trips_through_the_local_config()
    {
        Assert.Null(Assert.IsType<Success<string?>>((await _git.GetEncodingAsync(_repo.Path, Ct)).Value).Value);

        Assert.IsType<Done>((await _git.SetEncodingAsync(_repo.Path, "windows-1250", Ct)).Value);
        Assert.Equal("windows-1250", Assert.IsType<Success<string?>>((await _git.GetEncodingAsync(_repo.Path, Ct)).Value).Value);

        Assert.IsType<Done>((await _git.SetEncodingAsync(_repo.Path, null, Ct)).Value);
        Assert.Null(Assert.IsType<Success<string?>>((await _git.GetEncodingAsync(_repo.Path, Ct)).Value).Value);
        // Removing what is not there is not an error.
        Assert.IsType<Done>((await _git.SetEncodingAsync(_repo.Path, null, Ct)).Value);
    }

    [Fact]
    public async Task An_unknown_encoding_is_refused_and_nothing_is_written()
    {
        Assert.IsType<Failure>((await _git.SetEncodingAsync(_repo.Path, "no-such-charset", Ct)).Value);
        Assert.Null(Assert.IsType<Success<string?>>((await _git.GetEncodingAsync(_repo.Path, Ct)).Value).Value);
    }

    [Fact]
    public async Task Files_on_disk_and_blobs_are_decoded_with_the_chosen_encoding()
    {
        await _git.SetEncodingAsync(_repo.Path, "windows-1250", Ct);

        Assert.Equal("cafč\n", await ReadAsync(FileVersion.OnDisk("e.txt")));
        Assert.Equal("cafč\n", await ReadAsync(FileVersion.AtCommit("HEAD", "e.txt")));
    }

    [Fact]
    public async Task A_change_made_by_another_tool_is_seen_when_the_config_changes()
    {
        await _git.SetEncodingAsync(_repo.Path, "windows-1252", Ct);
        Assert.Equal("cafè\n", await ReadAsync(FileVersion.OnDisk("e.txt")));

        // Not through VIGO: the cache follows the config file.
        await Task.Delay(50, Ct);
        _repo.Git("config", "--local", "gui.encoding", "windows-1250");
        File.SetLastWriteTimeUtc(Path.Combine(_repo.Path, ".git", "config"), DateTime.UtcNow.AddSeconds(2));

        Assert.Equal("cafč\n", await ReadAsync(FileVersion.OnDisk("e.txt")));
    }

    [Fact]
    public void A_byte_order_mark_wins_over_the_chosen_encoding()
    {
        var bytes = new byte[] { 0xFF, 0xFE, (byte)'a', 0, (byte)'b', 0 };

        Assert.Equal("ab", TextDecoding.Decode(bytes, out _, Encoding.Latin1));
    }
}
