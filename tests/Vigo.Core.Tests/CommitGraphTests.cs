using Vigo.Core.Models;

namespace Vigo.Core.Tests;

public sealed class CommitGraphTests
{
    [Fact]
    public void Pages_of_one_commit_give_the_same_layout_as_a_single_page()
    {
        // M merges F into B; both fork from A.
        var commits = History(("m", ["b", "f"]), ("b", ["a"]), ("f", ["a"]), ("a", []));
        using var whole = new CommitGraph();
        using var paged = new CommitGraph();

        whole.Append(commits);
        foreach (var commit in commits)
        {
            paged.Append([commit]);
        }

        Assert.Equal(whole.Commits, paged.Commits);
        Assert.Equal(2, paged.Lanes.MaxLaneCount);
        for (var row = 0; row < commits.Length; row++)
        {
            Assert.Equal(whole.Lanes.GetNodeLane(row), paged.Lanes.GetNodeLane(row));
            Assert.Equal(whole.Lanes.GetIncomingLanes(row).ToArray(), paged.Lanes.GetIncomingLanes(row).ToArray());
            Assert.Equal(whole.Lanes.GetOutgoingLanes(row).ToArray(), paged.Lanes.GetOutgoingLanes(row).ToArray());
        }

        Assert.Equal([0, 1], paged.Lanes.GetIncomingLanes(3).ToArray());
    }

    [Fact]
    public void Parent_from_a_later_page_is_found_by_its_lane()
    {
        var commits = History(("c", ["b"]), ("b", ["a"]), ("a", []));
        using var graph = new CommitGraph();

        graph.Append(commits[..1]);

        Assert.False(graph.TryGetRow(Sha("b"), out _));
        Assert.Equal(1, graph.Lanes.OpenLaneCount);

        graph.Append(commits[1..]);

        Assert.True(graph.TryGetRow(Sha("b"), out var row));
        Assert.Equal(1, row);
        Assert.Equal([0], graph.Lanes.GetIncomingLanes(row).ToArray());
        Assert.Equal(0, graph.Lanes.OpenLaneCount);
    }

    [Fact]
    public void Appending_a_commit_twice_is_rejected()
    {
        var commits = History(("a", []));
        using var graph = new CommitGraph();
        graph.Append(commits);

        Assert.Throws<InvalidOperationException>(() => graph.Append(commits));
    }

    private static CommitInfo Stash(string name, string parent, int day) => new(
        Sha(name), [Sha(parent)], "Author", "author@example.com", new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), name) { IsStash = true };

    private static CommitInfo Dated(string name, string[] parents, int day) => new(
        Sha(name), [.. parents.Select(Sha)], "Author", "author@example.com", new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero), name);

    [Fact]
    public void A_stash_is_placed_among_the_commits_by_date_with_a_dotted_line_to_its_base()
    {
        CommitInfo[] log = [Dated("c", ["b"], 20), Dated("b", ["a"], 10), Dated("a", [], 1)];
        using var graph = new CommitGraph();
        graph.SetStashes([Stash("s", "b", 15)]);

        graph.Append(log);

        Assert.Equal(["c", "s", "b", "a"], graph.Commits.Select(c => c.Subject));
        Assert.True(graph.Commits[1].IsStash);
        Assert.Equal([Sha("s")], graph.WantedStashes);
        Assert.Equal([true], graph.Lanes.GetOutgoingDotted(1).ToArray());
        // c keeps lane 0 to b; the stash took lane 1 (dotted) and joins b from the right.
        Assert.Equal([false, true], graph.Lanes.GetIncomingDotted(2).ToArray());
        Assert.Equal(0, graph.Lanes.OpenLaneCount);
    }

    [Fact]
    public void A_stash_never_goes_below_its_base_even_with_a_skewed_clock()
    {
        // The stash looks older than its base: it still goes right above it.
        CommitInfo[] log = [Dated("c", ["b"], 20), Dated("b", ["a"], 10), Dated("a", [], 1)];
        using var graph = new CommitGraph();
        graph.SetStashes([Stash("s", "b", 5)]);

        graph.Append(log);

        Assert.Equal(["c", "s", "b", "a"], graph.Commits.Select(c => c.Subject));
    }

    [Fact]
    public void Stashes_are_placed_across_pages_and_a_stash_of_an_older_base_waits_for_its_date()
    {
        CommitInfo[] log = [Dated("c", ["b"], 20), Dated("b", ["a"], 10), Dated("a", [], 1)];
        using var graph = new CommitGraph();
        graph.SetStashes([Stash("new", "b", 30), Stash("mid", "a", 12)]);

        graph.Append(log[..1]);
        graph.Append(log[1..]);

        // "new" is newer than everything; "mid" (made on the root commit) goes above the first commit older than it.
        Assert.Equal(["new", "c", "mid", "b", "a"], graph.Commits.Select(c => c.Subject));
        Assert.Equal(0, graph.Lanes.OpenLaneCount);
    }

    private static CommitInfo[] History(params (string Name, string[] Parents)[] commits) =>
        [.. commits.Select(c => new CommitInfo(
            Sha(c.Name),
            [.. c.Parents.Select(Sha)],
            "Author",
            "author@example.com",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            c.Name))];

    // Readable names padded into valid SHA-1 ids.
    private static GitSha Sha(string name) => GitSha.Parse(Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes(name)).PadRight(40, '0'));
}
