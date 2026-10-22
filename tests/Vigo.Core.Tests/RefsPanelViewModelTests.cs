using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class RefsPanelViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly GitSha A = GitSha.Parse(new string('a', 40));
    private static readonly GitSha B = GitSha.Parse(new string('b', 40));

    private readonly GitService _git = new(new GitCli());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GitRef Local(string name, GitSha target, bool current = false, int ahead = 0, int behind = 0) =>
        new($"refs/heads/{name}", name, GitRefKind.LocalBranch, target) { IsCurrent = current, Ahead = ahead, Behind = behind, Upstream = ahead + behind > 0 ? "origin/" + name : null };

    private static GitRef Remote(string name, GitSha target) =>
        new($"refs/remotes/{name}", name, GitRefKind.RemoteBranch, target) { Remote = name[..name.IndexOf('/')] };

    private static GitRef Tag(string name) => new($"refs/tags/{name}", name, GitRefKind.Tag, A);

    private static readonly RepositoryRefs Sample = new(
    [
        Local("main", A),
        Local("feature/oauth-refresh", B, current: true, ahead: 2),
        Local("feature/metrics", A),
        Local("fix/cache-ttl", A),
        Remote("origin/main", A),
        Remote("origin/feature/oauth-refresh", B),
        Remote("upstream/main", A),
        Tag("v2.9.0"),
        Tag("v2.10.0"),
        Tag("v2.3.1"),
        new GitRef("stash@{0}", "stash@{0}", GitRefKind.Stash, B) { Message = "On main: try paging" },
    ], B, "feature/oauth-refresh");

    private static readonly GitRemote[] Remotes = [new("origin", "https://github.com/atlas/atlas-api.git", null)];

    private static List<RefRow> Build(RepositoryRefs? refs, string filter = "", params string[] collapsed) =>
        RefRowBuilder.BuildRows(refs, Remotes, filter, collapsed.ToHashSet(), _ => null);

    private static string Describe(RefRow r) => $"{new string(' ', r.IsSection ? 0 : 2 + 2 * r.Depth)}{r.Label}{(r.Meta.Length > 0 ? " [" + r.Meta + "]" : "")}{(r.IsHead ? " *" : "")}";

    [Fact]
    public void Rows_follow_the_mockup_sections_folders_and_order()
    {
        var rows = Build(Sample, collapsed: "section:Tags");

        Assert.Equal(
        [
            "Branches locales [4]",
            "  main",
            "  feature",
            "    metrics",
            "    oauth-refresh [↑2] *",
            "  fix",
            "    cache-ttl",
            "Distants [2]",
            "  origin [github.com]",
            "    main",
            "    feature",
            "      oauth-refresh",
            "  upstream",
            "    main",
            "Tags [3]",
            "Stashes [1]",
            "  try paging",
        ], rows.Select(Describe));
        Assert.True(rows.First(r => r.Label == "oauth-refresh").IsMetaAccent);
        Assert.Equal(44, rows.Single(r => r.Label == "metrics").Indent);
    }

    [Fact]
    public void Tags_are_sorted_by_version_descending()
    {
        var rows = Build(Sample);

        Assert.Equal(["v2.10.0", "v2.9.0", "v2.3.1"], rows.Where(r => r.Kind == RefRowKind.Tag).Select(r => r.Label));
    }

    [Fact]
    public void Collapsed_remote_and_folder_show_a_summary()
    {
        var rows = Build(Sample, "", "remote:origin", "folder:local/feature/");

        Assert.Equal("2 branches", rows.Single(r => r.Key == "remote:origin").Meta);
        var folder = rows.Single(r => r.Key == "folder:local/feature/");
        Assert.False(folder.IsExpanded);
        Assert.Equal("✓", folder.Meta);
        Assert.DoesNotContain(rows, r => r.Label == "oauth-refresh");
    }

    [Fact]
    public void Remote_branches_are_in_folders_like_the_local_ones_and_can_be_collapsed()
    {
        var rows = Build(Sample, "", "section:Local", "section:Tags", "folder:remote:origin/origin/feature/");

        var folder = rows.Single(r => r.Key == "folder:remote:origin/origin/feature/");
        Assert.Equal((RefRowKind.Folder, "feature", 1, false), (folder.Kind, folder.Label, folder.Depth, folder.IsExpanded));
        Assert.DoesNotContain(rows, r => r.Kind == RefRowKind.RemoteBranch && r.Label == "oauth-refresh");
        var open = Build(Sample, "", "section:Local", "section:Tags");
        var leaf = open.Single(r => r.Key == "rbranch:refs/remotes/origin/feature/oauth-refresh");
        Assert.Equal((RefRowKind.RemoteBranch, "oauth-refresh", 2), (leaf.Kind, leaf.Label, leaf.Depth));
    }

    [Fact]
    public void Filter_lists_matching_refs_flat_in_every_section()
    {
        var rows = Build(Sample, "MAIN", "section:Local", "section:Tags");

        Assert.Equal(
        [
            "Branches locales [4]",
            "  main",
            "Distants [2]",
            "  origin [github.com]",
            "    main",
            "  upstream",
            "    main",
            "Tags [3]",
            "Stashes [1]",
        ], rows.Select(Describe));
    }

    [Fact]
    public void Detached_and_unborn_heads_have_their_own_row()
    {
        var detached = Build(new RepositoryRefs([Local("main", A)], B, null));
        Assert.Equal(["HEAD détaché [bbbbbbb] *", "main"], detached.Where(r => r.Section == RefSection.Local && !r.IsSection).Select(Describe).Select(s => s.Trim()));

        var unborn = Build(new RepositoryRefs([], null, "main"));
        Assert.Equal("main [aucun commit] *", Describe(unborn[1]).Trim());
    }

    [Fact]
    public void Sync_keeps_the_items_of_remaining_keys_and_converges()
    {
        var target = new ObservableCollection<RefRowItem>(Build(Sample).Select(r => new RefRowItem(r)));
        var kept = target.Single(i => i.Row.Label == "cache-ttl");
        var tags = target.Single(i => i.Row.Key == "section:Tags");
        var replaced = 0;
        target.CollectionChanged += (_, e) => replaced += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace ? 1 : 0;
        var next = Build(Sample with
        {
            Refs = [.. Sample.Refs.Where(r => r.Name != "feature/metrics"), Local("zeta", A), Tag("v3.0.0")],
        });

        RefRowBuilder.SyncRows(target, next);

        Assert.Equal(next, target.Select(i => i.Row));
        Assert.Same(kept, target.Single(i => i.Row.Label == "cache-ttl"));
        // The tag count changed: same item (same list container), new row.
        Assert.Same(tags, target.Single(i => i.Row.Key == "section:Tags"));
        Assert.Equal("4", tags.Row.Meta);
        Assert.Equal(0, replaced);
    }

    [Fact]
    public async Task Click_selects_the_commit_and_checkout_switches_branch()
    {
        using var repo = TestRepoBuilder.Create();
        repo.Commit("root", ("a.txt", "1"));
        repo.Branch("feature");
        var feature = repo.Commit("feature", ("b.txt", "1"));
        repo.Checkout("main");
        var info = new RepositoryInfo(repo.Path, Path.Combine(repo.Path, ".git"), "repo", "main", null, false, false, false);
        using var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance);
        var graph = tab.Graph;
        var sut = tab.Refs;
        await graph.LoadAsync(Ct);

        var row = sut.Rows.Single(r => r.Row.Label == "feature").Row;
        Assert.Equal(graph.GetLane(GitSha.Parse(feature)) is { } lane ? Graph.LanePalette.ColorIndexFor(lane) : -1, row.ColorIndex);
        sut.ActivateCommand.Execute(row);
        Assert.Equal(GitSha.Parse(feature), graph.SelectedSha);

        await sut.CheckoutCommand.ExecuteAsync(row);

        Assert.Equal("feature", repo.Git("branch", "--show-current").Trim());
        Assert.True(sut.Rows.Single(r => r.Row.Label == "feature").Row.IsHead);
        Assert.False(sut.Rows.Single(r => r.Row.Label == "main").Row.IsHead);
    }

    [Fact]
    public void Checkout_targets_depend_on_the_kind_of_row()
    {
        var rows = Build(Sample);
        object? Target(Func<RefRow, bool> pick) => RefsPanelViewModel.ToCheckoutTarget(rows.Single(pick), Sample)?.Value;

        // A local "main" exists: checking out upstream/main switches to it rather than creating it again.
        Assert.Equal(new CheckoutBranch("main"), Target(r => r.Key == "rbranch:refs/remotes/upstream/main"));
        Assert.Equal(new CheckoutBranch("main"), Target(r => r.Key == "local:refs/heads/main"));
        Assert.Equal(new CheckoutCommit(A), Target(r => r.Label == "v2.9.0"));
        Assert.Null(Target(r => r.Kind == RefRowKind.Stash));
        Assert.Null(Target(r => r.Key == "local:refs/heads/feature/oauth-refresh"));
        Assert.Null(Target(r => r.Key == "rbranch:refs/remotes/origin/feature/oauth-refresh"));

        var withoutLocal = Sample with { Refs = [.. Sample.Refs.Where(r => r.Name != "main")] };
        Assert.Equal(new CheckoutRemoteBranch("upstream/main", "main"),
            RefsPanelViewModel.ToCheckoutTarget(rows.Single(r => r.Key == "rbranch:refs/remotes/upstream/main"), withoutLocal)?.Value);
    }
}
