using System.Diagnostics;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

public sealed class LfsTests : IDisposable
{
    private static readonly Lazy<bool> LfsAvailable = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo(GitExecutable.Path ?? "git", "lfs version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(psi)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private readonly GitService _git = new(new GitCli());
    private readonly TestRepoBuilder _repo;

    public LfsTests()
    {
        _repo = TestRepoBuilder.Create();
        _repo.Commit("init", ("a.txt", "a\n"));
    }

    public void Dispose() => _repo.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertDone(GitResult result) =>
        Assert.True(result.Value is Done, result.Value is Failure f ? f.Details ?? f.Message : result.Value?.ToString());

    private async Task<LfsInfo> ReadAsync() =>
        Assert.IsType<Success<LfsInfo>>((await _git.GetLfsAsync(_repo.Path, Ct)).Value, exactMatch: false).Value;

    /// <summary>The repository gets the LFS filter (what <c>git lfs install</c> does globally), and one tracked pattern.</summary>
    private void SetUpLfs()
    {
        Assert.SkipUnless(LfsAvailable.Value, "git-lfs is not installed");
        _repo.Git("lfs", "install", "--local", "--skip-repo");
        _repo.Git("lfs", "track", "*.bin");
    }

    [Fact]
    public async Task A_repository_without_attributes_does_not_use_lfs()
    {
        var info = await ReadAsync();

        Assert.False(info.IsUsed);
        Assert.Empty(info.Patterns);
        Assert.Empty(info.Files);
    }

    [Fact]
    public async Task Tracking_a_pattern_writes_gitattributes_and_lists_it()
    {
        Assert.SkipUnless(LfsAvailable.Value, "git-lfs is not installed");

        AssertDone(await _git.LfsTrackAsync(_repo.Path, "*.psd", Ct));

        Assert.Contains("filter=lfs", File.ReadAllText(Path.Combine(_repo.Path, ".gitattributes")), StringComparison.Ordinal);
        var info = await ReadAsync();
        Assert.True(info.IsUsed);
        Assert.True(info.IsInstalled);
        var pattern = Assert.Single(info.Patterns);
        Assert.Equal("*.psd", pattern.Pattern);
        Assert.Equal(".gitattributes", pattern.Source);
    }

    [Fact]
    public async Task Untracking_a_pattern_removes_it()
    {
        SetUpLfs();
        AssertDone(await _git.LfsTrackAsync(_repo.Path, "*.psd", Ct));

        AssertDone(await _git.LfsUntrackAsync(_repo.Path, "*.bin", Ct));

        var info = await ReadAsync();
        Assert.Equal(["*.psd"], info.Patterns.Select(p => p.Pattern));
    }

    [Fact]
    public async Task A_committed_file_is_listed_with_its_state()
    {
        SetUpLfs();
        _repo.Commit("add attributes", (".gitattributes", File.ReadAllText(Path.Combine(_repo.Path, ".gitattributes"))));
        _repo.Commit("add binary", ("data/blob.bin", "not really big\n"));

        var file = Assert.Single((await ReadAsync()).Files);
        Assert.Equal("data/blob.bin", file.Path);
        Assert.True(file.IsDownloaded);
        Assert.NotNull(file.Size);

        // Without its object in the local store, only the pointer is left (as after a clone that skipped the smudge filter).
        Directory.Delete(Path.Combine(_repo.Path, ".git", "lfs", "objects"), recursive: true);
        File.WriteAllText(Path.Combine(_repo.Path, "data", "blob.bin"), _repo.Git("show", "HEAD:data/blob.bin"));
        var missing = Assert.Single((await ReadAsync()).Files);
        Assert.False(missing.IsDownloaded);
        Assert.Equal(1, (await ReadAsync()).MissingCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-x")]
    [InlineData("a\nb")]
    public async Task An_invalid_pattern_is_refused_without_calling_git(string pattern)
    {
        Assert.IsType<Failure>((await _git.LfsTrackAsync(_repo.Path, pattern, Ct)).Value);
        Assert.IsType<Failure>((await _git.LfsUntrackAsync(_repo.Path, pattern, Ct)).Value);
        Assert.False(File.Exists(Path.Combine(_repo.Path, ".gitattributes")));
    }

    [Fact]
    public void Patterns_are_parsed_from_the_track_listing()
    {
        var patterns = GitService.ParseLfsPatterns(
            "Listing tracked patterns\n    *.psd (.gitattributes)\n    my files/** (sub/.gitattributes)\nListing excluded patterns\n    *.txt (.gitattributes)\n");

        Assert.Equal([new LfsPattern("*.psd", ".gitattributes"), new LfsPattern("my files/**", "sub/.gitattributes")], patterns);
    }

    [Fact]
    public void Files_are_parsed_from_the_listing()
    {
        var files = GitService.ParseLfsFiles(
            "4d7a214614ab2935c943f9e0ff69d22eadbb8f32b1258daaa5e2ca24d17e2393 * big file (1).psd (1.2 MB)\n" +
            "f00dfeedcafe0123456789abcdef0123456789abcdef0123456789abcdef0123 - img.png (15 B)\n");

        Assert.Equal(
            [new LfsFile("big file (1).psd", "4d7a214614", true, "1.2 MB"), new LfsFile("img.png", "f00dfeedca", false, "15 B")],
            files);
    }
}
