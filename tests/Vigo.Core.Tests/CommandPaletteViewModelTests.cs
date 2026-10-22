using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class CommandPaletteViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSource(params PaletteEntry[] entries) : IPaletteSource
    {
        public List<string> Commits { get; } = [];

        public IReadOnlyList<PaletteEntry> Entries { get; } = entries;

        public IEnumerable<PaletteEntry> FindCommits(Func<string, bool> matches, int limit) =>
            Commits.Where(matches).Take(limit).Select(c => Entry(PaletteGroup.Commits, c));
    }

    private static PaletteEntry Entry(PaletteGroup group, string label, Func<Task>? run = null, string detail = "") =>
        new(group, "circle", label, run ?? (() => Task.CompletedTask), detail);

    private static List<string> Labels(CommandPaletteViewModel sut) => [.. sut.Items.Select(i => i.Entry.Label)];

    [Theory]
    [InlineData("Pull", "pu", 0)]
    [InlineData("Fetch de tous les distants", "tous", 1)]
    [InlineData("Basculer le mode compact", "bmc", 2)]
    [InlineData("Préférences", "prefer", 2)]
    [InlineData("Push", "pl", -1)]
    public void Matches_prefix_then_substring_then_characters_in_order(string value, string text, int expected) =>
        Assert.Equal(expected, CommandPaletteViewModel.FuzzyMatch(value, text));

    [Theory]
    [InlineData("> pull", PaletteGroup.Commands, "pull")]
    [InlineData("@main", PaletteGroup.Branches, "main")]
    [InlineData(" #fix ", PaletteGroup.Commits, "fix")]
    public void A_prefix_restricts_the_search_to_one_group(string query, PaletteGroup scope, string text) =>
        Assert.Equal((scope, text), CommandPaletteViewModel.ParseQuery(query));

    [Fact]
    public void Without_a_query_every_command_is_listed_then_a_few_of_each_group_with_their_titles()
    {
        var source = new FakeSource(
        [
            .. Enumerable.Range(1, 10).Select(i => Entry(PaletteGroup.Commands, $"Commande {i}")),
            .. Enumerable.Range(1, 10).Select(i => Entry(PaletteGroup.Branches, $"branche-{i}")),
        ]);
        source.Commits.AddRange(Enumerable.Range(1, 10).Select(i => $"Commit {i}"));

        var sut = new CommandPaletteViewModel(source);

        Assert.Equal(10 + 6 + 6, sut.Items.Count);
        Assert.Equal(["Commandes", "Branches", "Commits"], sut.Items.Where(i => i.HasGroupTitle).Select(i => i.GroupTitle!));
        Assert.Equal(0, sut.SelectedIndex);
    }

    [Fact]
    public void Typing_filters_and_ranks_prefix_matches_first()
    {
        var sut = new CommandPaletteViewModel(new FakeSource(
            Entry(PaletteGroup.Commands, "Fetch de tous les distants"),
            Entry(PaletteGroup.Commands, "Push"),
            Entry(PaletteGroup.Commands, "Pull"),
            Entry(PaletteGroup.Branches, "feature/pull-cache"),
            Entry(PaletteGroup.Branches, "main")));

        sut.Query = "pu";

        Assert.Equal(["Push", "Pull", "feature/pull-cache"], Labels(sut));

        sut.Query = "@pu";
        Assert.Equal(["feature/pull-cache"], Labels(sut));
    }

    [Fact]
    public void Detail_is_searched_too_after_the_label_matches()
    {
        var sut = new CommandPaletteViewModel(new FakeSource(
            Entry(PaletteGroup.Files, "Program.cs", detail: "src/Api/Program.cs"),
            Entry(PaletteGroup.Files, "api.md", detail: "docs/api.md")));

        sut.Query = "api";

        Assert.Equal(["api.md", "Program.cs"], Labels(sut));
    }

    [Fact]
    public void No_result_is_reported_with_the_query()
    {
        var sut = new CommandPaletteViewModel(new FakeSource(Entry(PaletteGroup.Commands, "Pull")), "zzz");

        Assert.True(sut.HasNoResult);
        Assert.Equal(-1, sut.SelectedIndex);
        Assert.Equal("Aucun résultat pour « zzz »", sut.NoResultText);
    }

    [Fact]
    public async Task Arrows_wrap_around_and_enter_closes_then_runs_the_entry()
    {
        var events = new List<string>();
        var sut = new CommandPaletteViewModel(new FakeSource(
            Entry(PaletteGroup.Commands, "Pull", () => { events.Add("pull"); return Task.CompletedTask; }),
            Entry(PaletteGroup.Commands, "Push", () => { events.Add("push"); return Task.CompletedTask; })));
        sut.Closed += (_, _) => events.Add("closed");

        sut.MoveSelection(-1);
        Assert.Equal(1, sut.SelectedIndex);
        sut.MoveSelection(1);
        Assert.Equal(0, sut.SelectedIndex);
        sut.MoveSelection(1);

        await sut.RunSelectedAsync();

        Assert.Equal(["closed", "push"], events);
    }

    [Fact]
    public async Task A_repository_tab_offers_its_branches_files_and_commits()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("Initial commit", ("a.txt", "1"));
        repo.Branch("feature/cache", checkout: false);
        repo.Commit("Fix the cache TTL", ("a.txt", "2"));
        repo.WriteFile("b.txt", "new");
        var info = new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false);
        using var tab = new RepositoryTabViewModel(info, new GitService(new GitCli()), new FakeTimeProvider(Now), NullLogger.Instance);
        await tab.LoadAsync();

        var entries = tab.GetPaletteEntries().ToList();
        Assert.Contains(entries, e => e is { Group: PaletteGroup.Commands, Label: "Stash des modifications" });
        // The current branch cannot be checked out again.
        Assert.Equal(["feature/cache"], entries.Where(e => e.Group == PaletteGroup.Branches).Select(e => e.Label));
        Assert.Equal(["b.txt"], entries.Where(e => e.Group == PaletteGroup.Files).Select(e => e.Label));

        var commit = Assert.Single(tab.FindCommits(s => s.Contains("cache", StringComparison.OrdinalIgnoreCase), 10));
        await commit.Run();
        Assert.Equal(GitSha.Parse(repo.Head()), tab.Graph.SelectedSha);

        await entries.Single(e => e.Label == "feature/cache").Run();
        Assert.Equal("feature/cache", repo.Git("branch", "--show-current").Trim());
    }
}
