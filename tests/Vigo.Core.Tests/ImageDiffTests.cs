using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>Image diff (J5.7, ADR-043): both versions of an image file read as bytes instead of a text diff.</summary>
public sealed class ImageDiffTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly GitService _git = new(new GitCli());

    private RepositoryTabViewModel CreateTab(TestRepoBuilder repo) => new(
        new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false),
        _git, new FakeTimeProvider(Now), NullLogger.Instance);

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("dir/Photo.JPG", true)]
    [InlineData("x.webp", true)]
    [InlineData("icon.ico", true)]
    [InlineData("vector.svg", false)]
    [InlineData("data.bin", false)]
    [InlineData("png", false)]
    [InlineData("archive.png.zip", false)]
    public void Recognizes_image_paths_by_extension(string path, bool expected) =>
        Assert.Equal(expected, DiffImages.IsImagePath(path));

    [Fact]
    public async Task A_modified_image_of_a_commit_reads_both_versions()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.png", "OLD\0\u0001"));
        var sha = repo.Commit("change", ("a.png", "NEW\0\u0002\u0003"));
        using var tab = CreateTab(repo);
        var details = Assert.IsType<Success<CommitDetails>>(
            (await _git.GetCommitAsync(repo.Path, GitSha.Parse(sha), TestContext.Current.CancellationToken)).Value).Value;
        var diff = tab.Diff;

        await diff.OpenAsync(details, details.Files.Single());

        Assert.True(diff.IsImageDiff);
        Assert.Null(diff.PlaceholderMessage);
        Assert.Equal("a.png", diff.Images?.Path);
        Assert.Equal(Bytes("OLD\0\u0001"), diff.Images?.Old);
        Assert.Equal(Bytes("NEW\0\u0002\u0003"), diff.Images?.New);

        diff.CloseCommand.Execute(null);
        Assert.Null(diff.Images);
    }

    [Fact]
    public async Task An_added_image_has_no_old_version_and_a_deleted_one_no_new_version()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("gone.png", "GONE\0"));
        var sha = repo.Commit("change", ("added.png", "ADDED\0"));
        repo.Git("rm", "--quiet", "gone.png");
        var removed = repo.Commit("remove");
        using var tab = CreateTab(repo);
        var diff = tab.Diff;

        var added = Assert.IsType<Success<CommitDetails>>(
            (await _git.GetCommitAsync(repo.Path, GitSha.Parse(sha), TestContext.Current.CancellationToken)).Value).Value;
        await diff.OpenAsync(added, added.Files.Single());
        Assert.Null(diff.ErrorMessage);
        Assert.True(diff.IsImageDiff);
        Assert.Null(diff.Images?.Old);
        Assert.Equal(Bytes("ADDED\0"), diff.Images?.New);

        var deleted = Assert.IsType<Success<CommitDetails>>(
            (await _git.GetCommitAsync(repo.Path, GitSha.Parse(removed), TestContext.Current.CancellationToken)).Value).Value;
        await diff.OpenAsync(deleted, deleted.Files.Single());
        Assert.Equal(Bytes("GONE\0"), diff.Images?.Old);
        Assert.Null(diff.Images?.New);
    }

    [Fact]
    public async Task An_unstaged_image_compares_the_index_with_the_disk()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.png", "ONE\0"));
        repo.WriteFile("a.png", "TWO\0");
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();

        await tab.Diff.OpenWorkingAsync(tab.WorkingTree.Unstaged.Single().Entry, WorkingDiffSide.Unstaged);

        Assert.Equal(Bytes("ONE\0"), tab.Diff.Images?.Old);
        Assert.Equal(Bytes("TWO\0"), tab.Diff.Images?.New);
    }

    [Fact]
    public async Task A_binary_file_that_is_not_an_image_keeps_the_placeholder()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.bin", "ONE\0"));
        repo.WriteFile("a.bin", "TWO\0");
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();

        await tab.Diff.OpenWorkingAsync(tab.WorkingTree.Unstaged.Single().Entry, WorkingDiffSide.Unstaged);

        Assert.False(tab.Diff.IsImageDiff);
        Assert.Null(tab.Diff.Images);
        Assert.Equal("Fichier binaire : aperçu indisponible.", tab.Diff.PlaceholderMessage);
    }

    [Fact]
    public async Task The_comparison_mode_survives_from_one_image_to_the_next()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.png", "ONE\0"));
        repo.WriteFile("a.png", "TWO\0");
        using var tab = CreateTab(repo);
        await tab.WorkingTree.StartAsync();
        var diff = tab.Diff;

        Assert.Equal(ImageCompareMode.SideBySide, diff.ImageMode);
        diff.ImageMode = ImageCompareMode.Slider;
        await diff.OpenWorkingAsync(tab.WorkingTree.Unstaged.Single().Entry, WorkingDiffSide.Unstaged);

        Assert.Equal(ImageCompareMode.Slider, diff.ImageMode);
    }
}
