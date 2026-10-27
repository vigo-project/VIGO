using System.Collections.ObjectModel;
using System.Text;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Builds the rows of the left panel from the refs and the state of the sections (pure, tested without a view model), and
/// brings the displayed items to a new list of rows.
/// </summary>
internal static class RefRowBuilder
{
    internal static string SectionKey(RefSection section) => "section:" + section;

    /// <summary>Rows of the panel for <paramref name="refs"/> (pure, tested).</summary>
    /// <param name="laneOf">Lane of a commit in the graph, null when it is not loaded.</param>
    internal static List<RefRow> BuildRows(
        RepositoryRefs? refs, IReadOnlyList<GitRemote> remotes, string filter, IReadOnlySet<string> collapsed, Func<GitSha, int?> laneOf,
        HostingSnapshot? hosting = null, IReadOnlyList<Worktree>? worktrees = null, Func<Worktree, bool>? isCurrentWorktree = null,
        IReadOnlyList<Submodule>? submodules = null, LfsInfo? lfs = null, GitFlowConfig? gitFlow = null, HiddenRefs? hidden = null)
    {
        var rows = new List<RefRow>();
        // The checked-out branch is always drawn (HEAD), so it has no eye.
        string? HideKeyOf(GitRef r) => r.IsCurrent ? null : HiddenRefs.KeyOf(r);
        RefRow WithEye(RefRow row, GitRef r)
        {
            var key = HideKeyOf(r);
            return key is null ? row : row with { HideKey = key, IsHidden = hidden?.IsHidden(key) == true };
        }
        var filtering = filter.Length > 0;
        bool Matches(string name) => !filtering || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        bool IsOpen(string key) => filtering || !collapsed.Contains(key);
        int ColorOf(GitRef r) => laneOf(r.Target) is { } lane ? Graph.LanePalette.ColorIndexFor(lane) : StableColor.IndexFor(r.Name);

        var all = refs?.Refs ?? [];

        // Git Flow (J7.8): shown once the repository is initialized, above the local branches; the features, releases and
        // hotfixes in progress.
        if (gitFlow is not null)
        {
            var flowBranches = GitFlowActionsViewModel.BranchesOf(gitFlow, refs).ToList();
            var flowRows = flowBranches
                .Where(b => Matches(b.Branch.Name))
                .Select(b => WithEye(new RefRow("flow:" + b.Branch.CanonicalName, RefRowKind.FlowBranch, b.Name, 0, GitFlowActionsViewModel.KindLabel(b.Kind),
                    IsHead: b.Branch.IsCurrent, ColorIndex: ColorOf(b.Branch),
                    ToolTip: Loc.Format("Refs.GitFlow.FromTooltip", b.Branch.Name, gitFlow.BaseOf(b.Kind)), Ref: b.Branch, Section: RefSection.GitFlow), b.Branch))
                .ToList();
            if (!filtering && flowBranches.Count == 0)
                flowRows.Add(new RefRow("hint:gitflow", RefRowKind.Hint, Loc.Get("Refs.GitFlow.None"), 0, Section: RefSection.GitFlow));
            AddSection(rows, RefSection.GitFlow, "Git Flow", flowBranches.Count, flowRows, IsOpen);
        }

        // Local branches, in folders split on '/' (flat while filtering).
        var locals = all.Where(r => r.Kind == GitRefKind.LocalBranch).ToList();
        var localRows = new List<RefRow>();
        if (refs is { CurrentBranch: null, Head: { } detached } && Matches("HEAD"))
        {
            localRows.Add(new RefRow("head:detached", RefRowKind.DetachedHead, Loc.Get("Refs.Detached"), 0, detached.ToShortString(),
                IsHead: true, ColorIndex: laneOf(detached) is { } lane ? Graph.LanePalette.ColorIndexFor(lane) : 0,
                ToolTip: Loc.Format("Refs.DetachedTooltip", detached.ToShortString())));
        }
        else if (refs is { CurrentBranch: { } unborn, Head: null } && locals.Count == 0 && Matches(unborn))
        {
            // Empty repository: the branch exists only in HEAD until the first commit.
            localRows.Add(new RefRow("unborn:" + unborn, RefRowKind.LocalBranch, unborn, 0, Loc.Get("Refs.NoCommit"), IsHead: true,
                ColorIndex: StableColor.IndexFor(unborn), ToolTip: unborn));
        }

        var matchingLocals = locals.Where(r => Matches(r.Name)).ToList();
        AddTree(localRows, matchingLocals, flat: filtering, depth: 0, prefix: "", "local", collapsed, branch =>
        {
            var (meta, accent) = AheadBehind(branch);
            return (meta, accent, branch.IsCurrent, ColorOf(branch), LocalToolTip(branch));
        }, decorate: WithEye);
        AddSection(rows, RefSection.Local, Loc.Get("Refs.Section.Local"), locals.Count, localRows, IsOpen);

        // Remotes, each with its branches (full names, as in the mockup).
        var remoteBranches = all.Where(r => r.Kind == GitRefKind.RemoteBranch).ToList();
        var remoteNames = remotes.Select(r => r.Name)
            .Concat(remoteBranches.Select(r => r.Remote ?? string.Empty).Where(n => n.Length > 0).Order(StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var remoteRows = new List<RefRow>();
        foreach (var name in remoteNames)
        {
            var branches = remoteBranches.Where(r => r.Remote == name && Matches(r.Name))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (filtering && branches.Count == 0) continue;
            var key = "remote:" + name;
            var open = IsOpen(key);
            var url = remotes.FirstOrDefault(r => r.Name == name)?.FetchUrl;
            var total = remoteBranches.Count(r => r.Remote == name);
            var meta = open ? RemoteUrl.TryParse(url)?.Host ?? string.Empty : Loc.Format(total == 1 ? "Refs.RemoteBranches.One" : "Refs.RemoteBranches.Many", total);
            remoteRows.Add(new RefRow(key, RefRowKind.Remote, name, 0, meta, open, ToolTip: url ?? name, Section: RefSection.Remote));
            if (!open) continue;
            // Like the local branches: "origin/feature/J1" is the branch J1 in the folder feature of the remote origin.
            AddTree(remoteRows, branches, flat: filtering, depth: 1, prefix: name + "/", "remote:" + name, collapsed,
                branch => (string.Empty, false, false, ColorOf(branch), branch.Name), RefRowKind.RemoteBranch, "rbranch:", WithEye);
        }
        AddSection(rows, RefSection.Remote, Loc.Get("Refs.Section.Remote"), remoteNames.Count, remoteRows, IsOpen);

        // Pull requests and issues of the hosting service (J6.2): hidden when no remote is hosted.
        if (hosting is { State: not HostingState.Unavailable })
        {
            var pullRows = new List<RefRow>();
            foreach (var pull in hosting.PullRequests)
            {
                var label = $"#{pull.Number} {pull.Title}";
                if (!Matches(label)) continue;
                // The remote branch of a pull request from the repository itself: selects its commit, can be checked out.
                var head = pull.HeadRepository is null || string.Equals(pull.HeadRepository, hosting.RepositoryName, StringComparison.OrdinalIgnoreCase)
                    ? remoteBranches.FirstOrDefault(r => r.Name == $"{hosting.RemoteName}/{pull.HeadBranch}")
                    : null;
                PullRequestStatus? status = null;
                hosting.Statuses?.TryGetValue(pull.Number, out status);
                pullRows.Add(new RefRow($"pr:{pull.Number}", RefRowKind.PullRequest, label, 0, PullRequestMeta(pull, status),
                    ColorIndex: head is null ? null : ColorOf(head),
                    ToolTip: $"{label}\n{pull.HeadBranch} → {pull.BaseBranch} · {pull.Author}{PullRequestStatusText(status)}",
                    Ref: head, Section: RefSection.PullRequests, Url: pull.Url));
            }

            var workItems = hosting.Provider == HostingProvider.AzureDevOps;
            var issueRows = new List<RefRow>();
            var filterSummary = IssueFilterSummary(hosting.IssueFilter);
            if (filterSummary.Length > 0)
            {
                issueRows.Add(new RefRow(RefsPanelViewModel.IssueFilterKey, RefRowKind.Hint, filterSummary, 0,
                    ToolTip: Loc.Get("Refs.IssueFilter.ClearTooltip"), Section: RefSection.Issues));
            }
            foreach (var issue in hosting.Issues)
            {
                var label = $"#{issue.Number} {issue.Title}";
                if (!Matches(label)) continue;
                issueRows.Add(new RefRow($"issue:{issue.Number}", RefRowKind.Issue, label, 0, issue.Author,
                    ToolTip: $"{label}\n{(issue.Type is { Length: > 0 } type ? $"{type} · " : string.Empty)}{issue.Author}",
                    Section: RefSection.Issues, Url: issue.Url, ItemType: issue.Type));
            }

            if (!filtering)
            {
                var service = hosting.Provider;
                var none = pullRows.Count == 0 ? HintFor(hosting, Loc.Get("Refs.Pull.None"), service) : null;
                if (none is not null) pullRows.Add(new RefRow("hint:pr", RefRowKind.Hint, none, 0, ToolTip: none, Section: RefSection.PullRequests));
                if (hosting.Issues.Count == 0 && hosting.State is HostingState.Ready)
                {
                    issueRows.Add(new RefRow("hint:issues", RefRowKind.Hint,
                        Loc.Get(filterSummary.Length > 0 ? "Refs.Issues.NoMatch" : workItems ? "Refs.Issues.NoWorkItem" : "Refs.Issues.None"), 0, Section: RefSection.Issues));
                }
            }

            if (hosting is { IssuesHaveMore: true, State: HostingState.Ready })
            {
                var more = Loc.Get(hosting.IsLoadingMoreIssues ? "Common.Loading" : "Refs.LoadMore.Label");
                issueRows.Add(new RefRow(RefsPanelViewModel.MoreIssuesKey, RefRowKind.Hint, more, 0, ToolTip: Loc.Get(workItems ? "Refs.LoadMore.WorkItemsTooltip" : "Refs.LoadMore.IssuesTooltip"), Section: RefSection.Issues));
            }

            var counted = hosting.State is HostingState.Ready or HostingState.Loading;
            var issueCount = hosting.Issues.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) + (hosting.IssuesHaveMore ? "+" : string.Empty);
            AddSection(rows, RefSection.PullRequests, Loc.Get("Refs.Section.PullRequests"), hosting.PullRequests.Count, pullRows, IsOpen, counted ? null : string.Empty);
            AddSection(rows, RefSection.Issues, Loc.Get(workItems ? "Refs.Section.WorkItems" : "Refs.Section.Issues"), hosting.Issues.Count, issueRows, IsOpen, counted ? issueCount : string.Empty);
        }

        // Tags, most recent version first.
        var tags = all.Where(r => r.Kind == GitRefKind.Tag).ToList();
        var tagRows = tags.Where(t => Matches(t.Name))
            .OrderByDescending(t => t.Name, NaturalComparer.Instance)
            .Select(t => WithEye(new RefRow("tag:" + t.CanonicalName, RefRowKind.Tag, t.Name, 0,
                ToolTip: t.Message is { Length: > 0 } m ? $"{t.Name} — {m}" : t.Name, Ref: t, Section: RefSection.Tags), t))
            .ToList();
        AddSection(rows, RefSection.Tags, Loc.Get("Refs.Section.Tags"), tags.Count, tagRows, IsOpen);

        // Stashes, newest first (git order).
        var stashes = all.Where(r => r.Kind == GitRefKind.Stash).ToList();
        var stashRows = stashes
            // The subject alone: the index (stash@{n}) shifts and tells nothing; the name stays in the tooltip, and stands in for an empty subject.
            .Select(s => (Stash: s, Label: RefsPanelViewModel.StashSubject(s.Message) is { Length: > 0 } subject ? subject : s.Name))
            .Where(s => Matches(s.Label))
            .Select(s => WithEye(new RefRow("stash:" + s.Stash.CanonicalName, RefRowKind.Stash, s.Label, 0,
                ToolTip: s.Stash.Message ?? s.Stash.Name, Ref: s.Stash, Section: RefSection.Stashes), s.Stash))
            .ToList();
        AddSection(rows, RefSection.Stashes, Loc.Get("Refs.Section.Stashes"), stashes.Count, stashRows, IsOpen);

        // Worktrees (J7.1): shown once they are read; the main one first (git order).
        if (worktrees is { Count: > 0 })
        {
            var worktreeRows = worktrees
                .Select(w => (Worktree: w, Label: w.Name))
                .Where(w => Matches(w.Label) || (w.Worktree.Branch is { } b && Matches(b)))
                .Select(w => WorktreeRow(w.Worktree, isCurrentWorktree?.Invoke(w.Worktree) == true, laneOf))
                // Its branch is what the graph draws: the eye of the worktree is the one of that branch (the checked-out one has none).
                .Select(row => row.Worktree is { Branch: { Length: > 0 } branch, IsMain: false } && !row.IsHead
                        && all.FirstOrDefault(r => r.Kind == GitRefKind.LocalBranch && r.Name == branch) is { IsCurrent: false } reference
                    ? WithEye(row, reference)
                    : row)
                .ToList();
            AddSection(rows, RefSection.Worktrees, Loc.Get("Refs.Section.Worktrees"), worktrees.Count, worktreeRows, IsOpen);
        }

        // Submodules (J7.2): shown when the repository declares some, in .gitmodules order (git status order).
        if (submodules is { Count: > 0 })
        {
            var submoduleRows = submodules
                .Where(s => Matches(s.Path) || Matches(s.Name))
                .Select(SubmoduleRow)
                .ToList();
            AddSection(rows, RefSection.Submodules, Loc.Get("Refs.Section.Submodules"), submodules.Count, submoduleRows, IsOpen);
        }

        // Git LFS (J7.5): shown when an attributes file routes patterns through it; one row per pattern, then a summary line.
        if (lfs is { IsUsed: true })
        {
            var lfsRows = lfs.Patterns
                .Where(p => Matches(p.Pattern))
                .Select(p => new RefRow("lfs:" + p.Pattern, RefRowKind.LfsPattern, p.Pattern, 0, p.Source,
                    ToolTip: Loc.Format("Refs.Lfs.PatternTooltip", p.Pattern, p.Source), Section: RefSection.Lfs, LfsPattern: p))
                .ToList();
            if (!filtering)
            {
                var summary = LfsSummary(lfs);
                lfsRows.Add(new RefRow("hint:lfs", RefRowKind.Hint, summary, 0, ToolTip: summary, Section: RefSection.Lfs));
            }
            AddSection(rows, RefSection.Lfs, "Git LFS", lfs.Patterns.Count, lfsRows, IsOpen);
        }

        return rows;
    }

    /// <summary>The line under the Git LFS patterns: not installed, or how many LFS files there are and how many lack their content.</summary>
    internal static string LfsSummary(LfsInfo lfs)
    {
        if (!lfs.IsInstalled) return Loc.Get("Refs.Lfs.NotInstalled");
        if (lfs.Files.Count == 0) return Loc.Get("Refs.Lfs.NoFile");
        var files = Loc.Format(lfs.Files.Count == 1 ? "Refs.Lfs.FileOne" : "Refs.Lfs.FileMany", lfs.Files.Count);
        return lfs.MissingCount switch
        {
            0 => files,
            1 => Loc.Format("Refs.Lfs.ToPullOne", files),
            var n => Loc.Format("Refs.Lfs.ToPullMany", files, n),
        };
    }

    /// <summary>Row of a submodule: its folder, its state (or the short commit it is on) on the right.</summary>
    private static RefRow SubmoduleRow(Submodule submodule)
    {
        var shortHead = submodule.Head?.ToShortString() ?? string.Empty;
        var (meta, accent, state) = submodule.State switch
        {
            SubmoduleState.NotInitialized => (Loc.Get("Refs.Submodule.NotInitialized"), true, Loc.Get("Refs.Submodule.NotInitializedTooltip")),
            SubmoduleState.OutOfSync => (shortHead.Length > 0 ? $"≠ {shortHead}" : Loc.Get("Refs.Submodule.Modified"), true,
                Loc.Get("Refs.Submodule.OutOfSyncTooltip")),
            SubmoduleState.Conflict => (Loc.Get("Refs.Submodule.Conflict"), true, Loc.Get("Refs.Submodule.ConflictTooltip")),
            _ => (shortHead, false, Loc.Get("Refs.Submodule.InSyncTooltip")),
        };
        var details = new StringBuilder(submodule.Path);
        if (submodule.Url is { Length: > 0 } url) details.Append('\n').Append(url);
        if (submodule.Branch is { Length: > 0 } branch) details.Append('\n').Append(Loc.Format("Refs.Submodule.TrackedBranch", branch));
        details.Append('\n').Append(state);
        return new RefRow("submodule:" + submodule.Path, RefRowKind.Submodule, submodule.Label, 0, meta, IsMetaAccent: accent,
            ColorIndex: StableColor.IndexFor(submodule.Path), ToolTip: details.ToString(), Section: RefSection.Submodules, Submodule: submodule);
    }

    /// <summary>Row of a worktree: its folder, the branch it has checked out (or the detached commit) on the right.</summary>
    private static RefRow WorktreeRow(Worktree worktree, bool isCurrent, Func<GitSha, int?> laneOf)
    {
        var meta = worktree.IsPrunable ? Loc.Get("Refs.Worktree.Missing")
            : worktree.Branch ?? (worktree.IsBare ? Loc.Get("Refs.Worktree.Bare") : worktree.Head is { } head ? Loc.Format("Refs.Worktree.Detached", head.ToShortString()) : Loc.Get("Refs.NoCommit"));
        var details = new StringBuilder(worktree.Path);
        if (worktree.IsMain) details.Append('\n').Append(Loc.Get("Refs.Worktree.Main"));
        if (isCurrent) details.Append('\n').Append(Loc.Get("Refs.Worktree.ShownHere"));
        if (worktree.IsLocked) details.Append('\n').Append(worktree.LockReason is { Length: > 0 } lockReason ? Loc.Format("Refs.Worktree.LockedWithReason", lockReason) : Loc.Get("Refs.Worktree.Locked"));
        if (worktree.IsPrunable) details.Append('\n').Append(Loc.Get("Refs.Worktree.Prunable"));
        var color = worktree.Head is { } tip && laneOf(tip) is { } lane ? Graph.LanePalette.ColorIndexFor(lane) : StableColor.IndexFor(worktree.Path);
        return new RefRow("worktree:" + worktree.Path, RefRowKind.Worktree, worktree.Name, 0, meta, IsHead: isCurrent,
            IsMetaAccent: worktree.IsPrunable, ColorIndex: color, ToolTip: details.ToString(), Section: RefSection.Worktrees, Worktree: worktree);
    }

    /// <summary>The line under the pull request section while it has no pull request to list; null when it has nothing to say.</summary>
    private static string? HintFor(HostingSnapshot hosting, string whenEmpty, HostingProvider provider) => hosting.State switch
    {
        HostingState.SignedOut => Loc.Format("Refs.Hosting.SignedOut", provider),
        HostingState.Loading => Loc.Get("Common.Loading"),
        HostingState.Failed => hosting.Error,
        HostingState.Ready => whenEmpty,
        _ => null,
    };

    /// <summary>
    /// Right-hand text of a pull request row (J6.6): a mark for its checks, then "draft", its review decision or its
    /// author.
    /// </summary>
    internal static string PullRequestMeta(PullRequest pull, PullRequestStatus? status)
    {
        var mark = status?.Checks switch
        {
            CheckState.Success => "✓ ",
            CheckState.Failure => "✕ ",
            CheckState.Pending => "● ",
            _ => string.Empty,
        };
        var text = pull.IsDraft ? Loc.Get("Refs.Pull.Draft") : status?.Review switch
        {
            ReviewState.Approved => Loc.Get("Refs.Pull.Approved"),
            ReviewState.ChangesRequested => Loc.Get("Refs.Pull.ChangesNeeded"),
            _ => pull.Author,
        };
        return mark + text;
    }

    /// <summary>Tooltip lines about checks and review ("\nChecks : 3/4 réussis · échec : build\nRevue : approuvée"); empty when unknown.</summary>
    internal static string PullRequestStatusText(PullRequestStatus? status)
    {
        if (status is null) return string.Empty;

        var text = new StringBuilder();
        if (status.Runs.Count > 0)
        {
            var passed = status.Runs.Count(r => r.State == CheckState.Success);
            text.Append('\n').Append(Loc.Format("Refs.Pull.ChecksPassed", passed, status.Runs.Count));
            var failed = status.Runs.Where(r => r.State is CheckState.Failure or CheckState.Canceled).Select(r => r.Name).Take(3).ToList();
            if (failed.Count > 0) text.Append(" · ").Append(Loc.Format("Refs.Pull.ChecksFailed", string.Join(", ", failed)));
            var pending = status.Runs.Count(r => r.State == CheckState.Pending);
            if (pending > 0) text.Append(" · ").Append(Loc.Format("Refs.Pull.ChecksPending", pending));
        }
        switch (status.Review)
        {
            case ReviewState.Approved: text.Append('\n').Append(Loc.Get("Refs.Pull.ReviewApproved")); break;
            case ReviewState.ChangesRequested: text.Append('\n').Append(Loc.Get("Refs.Pull.ReviewChangesRequested")); break;
        }
        return text.ToString();
    }

    /// <param name="meta">Text of the count column; null for <paramref name="count"/>.</param>
    private static void AddSection(
        List<RefRow> rows, RefSection section, string title, int count, List<RefRow> children, Func<string, bool> isOpen, string? meta = null)
    {
        var key = SectionKey(section);
        var open = isOpen(key);
        rows.Add(new RefRow(key, RefRowKind.Section, title, 0, meta ?? count.ToString(System.Globalization.CultureInfo.CurrentCulture), open, Section: section));
        if (open) rows.AddRange(children.Select(c => c with { Section = section }));
    }

    /// <summary>
    /// Branches of one folder level: branches first, then sub-folders (mockup: "main" above "feature/", "fix/"…),
    /// each sorted by name.
    /// </summary>
    private static void AddTree(
        List<RefRow> rows, List<GitRef> branches, bool flat, int depth, string prefix, string scope, IReadOnlySet<string> collapsed,
        Func<GitRef, (string Meta, bool Accent, bool IsHead, int Color, string ToolTip)> describe,
        RefRowKind leafKind = RefRowKind.LocalBranch, string leafKeyPrefix = "local:", Func<RefRow, GitRef, RefRow>? decorate = null)
    {
        var leaves = flat ? branches : branches.Where(b => b.Name.IndexOf('/', prefix.Length) < 0).ToList();
        foreach (var branch in leaves.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (meta, accent, isHead, color, toolTip) = describe(branch);
            var label = branch.Name[prefix.Length..];
            var row = new RefRow(leafKeyPrefix + branch.CanonicalName, leafKind, label, depth, meta,
                IsHead: isHead, IsMetaAccent: accent, ColorIndex: color, ToolTip: toolTip, Ref: branch);
            rows.Add(decorate is null ? row : decorate(row, branch));
        }
        if (flat) return;

        var folders = branches.Except(leaves)
            .GroupBy(b => b.Name[prefix.Length..b.Name.IndexOf('/', prefix.Length)], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var path = prefix + folder.Key + "/";
            var key = $"folder:{scope}/{path}";
            var open = !collapsed.Contains(key);
            // A folder holding the current branch says so when collapsed.
            var holdsHead = folder.Any(b => b.IsCurrent);
            rows.Add(new RefRow(key, RefRowKind.Folder, folder.Key, depth, !open && holdsHead ? "✓" : string.Empty, open,
                IsMetaAccent: holdsHead, ToolTip: path.TrimEnd('/')));
            if (open) AddTree(rows, [.. folder], flat, depth + 1, path, scope, collapsed, describe, leafKind, leafKeyPrefix, decorate);
        }
    }

    private static (string Meta, bool Accent) AheadBehind(GitRef branch)
    {
        var parts = new List<string>(2);
        if (branch.Ahead > 0) parts.Add($"↑{branch.Ahead}");
        if (branch.Behind > 0) parts.Add($"↓{branch.Behind}");
        return (string.Join(' ', parts), branch.Ahead > 0 || branch.Behind > 0);
    }

    private static string LocalToolTip(GitRef branch) => branch.Upstream is { } upstream
        ? $"{branch.Name} → {upstream}" + (branch.Ahead > 0 || branch.Behind > 0 ? " " + Loc.Format("Refs.Upstream.AheadBehind", branch.Ahead, branch.Behind) : string.Empty)
        : Loc.Format("Refs.Upstream.None", branch.Name);

    /// <summary>
    /// Brings <paramref name="target"/> to <paramref name="source"/> with few changes, so the list keeps its scroll
    /// position and its containers: an item whose key is still there keeps its container (only its row is updated),
    /// others are inserted or removed.
    /// </summary>
    internal static void SyncRows(ObservableCollection<RefRowItem> target, IReadOnlyList<RefRow> source, Action<RefRow>? toggleHidden = null)
    {
        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            if (i < target.Count && target[i].Row.Key != row.Key)
            {
                var later = -1;
                for (var j = i + 1; j < target.Count; j++)
                {
                    if (target[j].Row.Key == row.Key) { later = j; break; }
                }

                if (later < 0)
                {
                    target.Insert(i, new RefRowItem(row, toggleHidden));
                    continue;
                }

                // Rows before it are gone (or moved down: they are inserted again further on).
                for (var k = later - 1; k >= i; k--) target.RemoveAt(k);
            }

            if (i >= target.Count) target.Add(new RefRowItem(row, toggleHidden));
            else if (!target[i].Row.Equals(row)) target[i].Row = row;
        }

        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }

    /// <summary>Text of the row that recalls the active issue filters; empty for the default ones.</summary>
    internal static string IssueFilterSummary(IssueQuery? query)
    {
        if (query is null || query.IsDefault) return string.Empty;

        var parts = new List<string>();
        if (query.State != IssueStateFilter.Open) parts.Add(Loc.Get(query.State == IssueStateFilter.Closed ? "Refs.IssueFilter.SummaryClosed" : "Refs.IssueFilter.SummaryAll"));
        if (query.AssignedToMe) parts.Add(Loc.Get("Refs.IssueFilter.SummaryAssignedToMe"));
        if (query.CreatedByMe) parts.Add(Loc.Get("Refs.IssueFilter.SummaryCreatedByMe"));
        if (query.Search.Length > 0) parts.Add(Loc.Format("Refs.IssueFilter.SummaryQuoted", query.Search));
        return Loc.Format("Refs.IssueFilter.Summary", string.Join(" · ", parts));
    }

    /// <summary>Compares names with their digit runs as numbers: v2.10 after v2.9.</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null) return string.CompareOrdinal(x, y);
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var a = x.AsSpan(si, i - si).TrimStart('0');
                    var b = y.AsSpan(sj, j - sj).TrimStart('0');
                    var byLength = a.Length.CompareTo(b.Length);
                    if (byLength != 0) return byLength;
                    var byDigits = a.SequenceCompareTo(b);
                    if (byDigits != 0) return byDigits;
                    continue;
                }

                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0) return byChar;
                i++;
                j++;
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
