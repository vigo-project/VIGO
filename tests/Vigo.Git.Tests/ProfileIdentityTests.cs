using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Git.Tests;

/// <summary>Git profiles reach git.exe as -c settings (J6.7, ADR-051): no configuration file is touched.</summary>
public sealed class ProfileIdentityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitProfile Work { get; } = new("work", "Travail", "Camille Marchand", "camille@atlas.dev");

    [Fact]
    public async Task Commit_is_authored_with_the_profile_of_the_repository()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");
        var git = new GitService(new GitCli { ProfileFor = _ => Work });

        var result = await git.CommitAsync(repo.Path, new CommitRequest("With a profile\n"), Ct);

        Assert.IsType<Success<GitSha>>(result.Value, exactMatch: true);
        Assert.Equal("Camille Marchand <camille@atlas.dev>", repo.Git("log", "-1", "--format=%an <%ae>").Trim());
        Assert.Equal("Camille Marchand <camille@atlas.dev>", repo.Git("log", "-1", "--format=%cn <%ce>").Trim());
    }

    [Fact]
    public async Task Profile_leaves_the_configuration_files_alone()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");
        var before = File.ReadAllText(Path.Combine(repo.Path, ".git", "config"));
        var git = new GitService(new GitCli { ProfileFor = _ => Work });

        await git.CommitAsync(repo.Path, new CommitRequest("Config untouched\n"), Ct);

        Assert.Equal(before, File.ReadAllText(Path.Combine(repo.Path, ".git", "config")));
    }

    [Fact]
    public async Task Without_profile_the_configuration_decides()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");
        var git = new GitService(new GitCli { ProfileFor = _ => null });

        await git.CommitAsync(repo.Path, new CommitRequest("No profile\n"), Ct);

        Assert.Equal(repo.Git("config", "user.email").Trim(), repo.Git("log", "-1", "--format=%ae").Trim());
    }

    [Fact]
    public async Task Empty_profile_fields_are_not_forced()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("init", ("a.txt", "a\n"));
        repo.WriteFile("a.txt", "a2\n").Stage("a.txt");
        var nameOnly = new GitProfile("p", "Nom seul", "Only Name", string.Empty);
        var git = new GitService(new GitCli { ProfileFor = _ => nameOnly });

        await git.CommitAsync(repo.Path, new CommitRequest("Name only\n"), Ct);

        Assert.Equal("Only Name", repo.Git("log", "-1", "--format=%an").Trim());
        Assert.Equal(repo.Git("config", "user.email").Trim(), repo.Git("log", "-1", "--format=%ae").Trim());
    }
}
