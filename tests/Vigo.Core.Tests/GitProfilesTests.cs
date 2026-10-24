using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

/// <summary>Git profiles (J6.7, ADR-051): folder rules, active profile, persistence, accounts that follow the profile.</summary>
public sealed class GitProfilesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class MemoryProfileStore(GitProfileSet? initial = null) : IGitProfileStore
    {
        public GitProfileSet Saved { get; private set; } = initial ?? GitProfileSet.Empty;

        public GitProfileSet Load() => Saved;

        public Task SaveAsync(GitProfileSet profiles, CancellationToken cancellationToken = default)
        {
            Saved = profiles;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(@"C:\dev\atlas\**", @"C:\dev\atlas\api", true)]
    [InlineData(@"C:\dev\atlas\**", @"C:\dev\atlas\api\src", true)]
    [InlineData(@"C:\dev\atlas\**", @"C:\dev\atlas", true)]
    [InlineData(@"C:\dev\atlas\**", @"C:\dev\atlas2\api", false)]
    [InlineData(@"C:\dev\atlas\**", @"C:\dev\perso\api", false)]
    [InlineData(@"c:/DEV/Atlas/**", @"C:\dev\atlas\api", true)]
    [InlineData(@"C:\dev\atlas", @"C:\dev\atlas\api", true)]
    [InlineData(@"C:\dev\atlas", @"C:\dev\atlas", true)]
    [InlineData(@"C:\dev\*\api", @"C:\dev\atlas\api", true)]
    [InlineData(@"C:\dev\*\api", @"C:\dev\atlas\x\api", false)]
    [InlineData(@"C:\dev\**\api", @"C:\dev\atlas\x\api", true)]
    [InlineData(@"C:\dev\**\api", @"C:\dev\api", true)]
    [InlineData(@"D:\**", @"C:\dev\atlas", false)]
    [InlineData("", @"C:\dev\atlas", false)]
    public void Folder_rule_matches_like_a_path_pattern(string rule, string path, bool expected) =>
        Assert.Equal(expected, FolderRule.Matches(rule, path));

    [Fact]
    public void Overrides_hold_the_filled_fields_and_the_signing_settings()
    {
        var unsigned = new GitProfile("a", "A", "Ada", "ada@example.com", "KEY", SignCommits: false);
        var signed = unsigned with { SignCommits = true };
        var empty = new GitProfile("b", "B", " ", string.Empty);

        Assert.Equal(["user.name=Ada", "user.email=ada@example.com", "user.signingkey=KEY"], unsigned.ConfigOverrides());
        Assert.Equal(
            ["user.name=Ada", "user.email=ada@example.com", "user.signingkey=KEY", "commit.gpgsign=true", "tag.gpgsign=true"],
            signed.ConfigOverrides());
        Assert.Empty(empty.ConfigOverrides());
    }

    [Fact]
    public void Without_profile_nothing_applies_and_everything_belongs_to_default()
    {
        using var profiles = new GitProfiles(new MemoryProfileStore());

        Assert.Null(profiles.ResolveFor(@"C:\dev\atlas"));
        Assert.Equal(SessionProfiles.Default, profiles.ActiveId);
        Assert.Equal(SessionProfiles.Default, profiles.IdFor(@"C:\dev\atlas"));
    }

    [Fact]
    public void First_profile_is_the_default_one_and_active()
    {
        using var profiles = new GitProfiles(new MemoryProfileStore());

        var first = profiles.Add("Travail", "Camille", "camille@atlas.dev");
        var second = profiles.Add("Perso", "Camille M.", "camille@proton.me");

        Assert.Equal(SessionProfiles.Default, first.Id);
        Assert.NotEqual(SessionProfiles.Default, second.Id);
        Assert.Equal(first.Id, profiles.Active?.Id);
    }

    [Fact]
    public void Folder_rule_wins_over_the_active_profile_and_the_first_match_wins()
    {
        using var profiles = new GitProfiles(new MemoryProfileStore());
        var work = profiles.Add("Travail", "Camille", "camille@atlas.dev");
        var perso = profiles.Add("Perso", "Camille M.", "camille@proton.me");
        profiles.Update(perso with { FolderRules = [@"C:\dev\perso\**"] });
        profiles.Update(work with { FolderRules = [@"C:\dev\**"] });

        Assert.Equal(work.Id, profiles.ResolveFor(@"C:\dev\atlas")?.Id);
        // Work is listed first and its rule is wider: it wins.
        Assert.Equal(work.Id, profiles.ResolveFor(@"C:\dev\perso\site")?.Id);
        Assert.Equal(work.Id, profiles.ResolveFor(@"D:\elsewhere")?.Id);

        profiles.SetActive(perso.Id);
        Assert.Equal(perso.Id, profiles.ResolveFor(@"D:\elsewhere")?.Id);
        Assert.Equal(work.Id, profiles.ResolveFor(@"C:\dev\atlas")?.Id);
    }

    [Fact]
    public void Removing_the_active_profile_activates_the_next_one()
    {
        using var profiles = new GitProfiles(new MemoryProfileStore());
        var first = profiles.Add("A", "a", "a@x");
        var second = profiles.Add("B", "b", "b@x");

        profiles.Remove(first.Id);

        Assert.Equal([second.Id], profiles.Profiles.Select(p => p.Id));
        Assert.Equal(second.Id, profiles.ActiveId);
    }

    [Fact]
    public async Task Profiles_are_saved_with_the_active_one()
    {
        var store = new MemoryProfileStore();
        using var profiles = new GitProfiles(store);
        var first = profiles.Add("A", "a", "a@x");
        var second = profiles.Add("B", "b", "b@x");
        profiles.SetActive(second.Id);
        await Task.Delay(100, Ct);

        Assert.Equal(second.Id, store.Saved.ActiveId);
        Assert.Equal([first.Id, second.Id], store.Saved.Profiles.Select(p => p.Id));
    }

    [Fact]
    public void Hand_edited_file_is_repaired()
    {
        var set = new GitProfileSet(
        [
            new GitProfile("a", "A", "a", "a@x"),
            new GitProfile("a", "Duplicate", "d", "d@x"),
            new GitProfile(" ", "Blank id", "b", "b@x"),
        ], "unknown");

        using var profiles = new GitProfiles(new MemoryProfileStore(set));

        Assert.Equal(["a"], profiles.Profiles.Select(p => p.Id));
        Assert.Equal("a", profiles.ActiveId);
    }

    [Fact]
    public async Task Json_store_round_trips_profiles_rules_and_active_profile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vigo-profiles-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonGitProfileStore(path, NullLogger<JsonGitProfileStore>.Instance);
            var written = new GitProfileSet(
                [new GitProfile("default", "Travail", "Camille", "c@atlas.dev", "4AEE", true, [@"C:\dev\atlas\**", @"D:\work"]), new GitProfile("p2", "Perso", "C", "c@x", Icon: "rocket")],
                "p2");

            await store.SaveAsync(written, Ct);
            var read = store.Load();

            Assert.Equal("p2", read.ActiveId);
            Assert.Equal(2, read.Profiles.Length);
            Assert.Equal("4AEE", read.Profiles[0].SigningKey);
            Assert.True(read.Profiles[0].SignCommits);
            Assert.Equal([@"C:\dev\atlas\**", @"D:\work"], read.Profiles[0].FolderRules);
            Assert.True(read.Profiles[1].FolderRules.IsEmpty);
            Assert.Equal("rocket", read.Profiles[1].Icon);
            Assert.Null(read.Profiles[0].Icon);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Json_store_ignores_a_corrupt_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vigo-profiles-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            var store = new JsonGitProfileStore(path, NullLogger<JsonGitProfileStore>.Instance);

            Assert.Empty(store.Load().Profiles);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
