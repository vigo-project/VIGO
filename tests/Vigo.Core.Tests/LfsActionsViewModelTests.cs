using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class LfsActionsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string RepoPath = @"C:\repos\lfs";

    private readonly FakeGit _git = new();
    private readonly FakeInputs _inputs = new();
    private readonly RecordingNotifier _notifier = new();

    private static LfsInfo Used(params LfsFile[] files) =>
        new(true, true, [new LfsPattern("*.psd", ".gitattributes")], files);

    private async Task<RepositoryTabViewModel> OpenAsync()
    {
        var info = new RepositoryInfo(RepoPath, Path.Combine(RepoPath, ".git"), "lfs", "main", null, false, false, false);
        _git.Repos[RepoPath] = new Success<RepositoryInfo>(info);
        var tab = new RepositoryTabViewModel(info, _git, new FakeTimeProvider(Now), NullLogger.Instance,
            watchers: null, _notifier, confirmations: null, authentication: null, _inputs);
        await tab.LoadAsync();
        return tab;
    }

    [Fact]
    public async Task A_repository_that_does_not_use_lfs_has_no_section()
    {
        using var tab = await OpenAsync();

        Assert.False(tab.Lfs.IsUsed);
        Assert.DoesNotContain(tab.Refs.Rows, r => r.Row.Section == RefSection.Lfs);
    }

    [Fact]
    public async Task The_section_lists_the_patterns_then_a_summary_of_the_files()
    {
        _git.Lfs = Used(new LfsFile("a.psd", "aaaaaaaaaa", true, "1 MB"), new LfsFile("b.psd", "bbbbbbbbbb", false, "2 MB"));
        using var tab = await OpenAsync();

        var rows = RefRowBuilder.BuildRows(tab.Graph.Refs, [], string.Empty, new HashSet<string>(), _ => null, null, lfs: tab.Lfs.Info);

        Assert.Contains(rows, r => r is { Kind: RefRowKind.Section, Section: RefSection.Lfs, Label: "Git LFS", Meta: "1" });
        var pattern = Assert.Single(rows, r => r.Kind == RefRowKind.LfsPattern);
        Assert.Equal("*.psd", pattern.Label);
        Assert.Equal(".gitattributes", pattern.Meta);
        Assert.Equal("2 fichiers LFS, 1 à récupérer", Assert.Single(rows, r => r.Key == "hint:lfs").Label);
        Assert.True(tab.Lfs.CanPull);
    }

    [Fact]
    public async Task The_section_is_collapsed_by_default()
    {
        _git.Lfs = Used();
        using var tab = await OpenAsync();

        Assert.False(tab.Refs.Rows.Single(r => r.Row is { Kind: RefRowKind.Section, Section: RefSection.Lfs }).Row.IsExpanded);
    }

    [Fact]
    public void The_summary_says_when_lfs_is_missing_or_unused()
    {
        Assert.Equal("Git LFS n'est pas installé sur ce poste.", RefRowBuilder.LfsSummary(new LfsInfo(true, false, [], [])));
        Assert.Equal("Aucun fichier LFS dans ce commit.", RefRowBuilder.LfsSummary(Used()));
        Assert.Equal("1 fichier LFS", RefRowBuilder.LfsSummary(Used(new LfsFile("a", "x", true, null))));
    }

    [Fact]
    public async Task A_filter_matches_the_pattern()
    {
        _git.Lfs = Used();
        using var tab = await OpenAsync();

        Assert.Contains(RefRowBuilder.BuildRows(tab.Graph.Refs, [], "psd", new HashSet<string>(), _ => null, null, lfs: tab.Lfs.Info),
            r => r.Kind == RefRowKind.LfsPattern);
        Assert.DoesNotContain(RefRowBuilder.BuildRows(tab.Graph.Refs, [], "zzz", new HashSet<string>(), _ => null, null, lfs: tab.Lfs.Info),
            r => r.Kind is RefRowKind.LfsPattern or RefRowKind.Hint && r.Section == RefSection.Lfs);
    }

    [Fact]
    public async Task Tracking_asks_for_a_pattern_and_shows_the_new_row()
    {
        _git.Lfs = Used();
        using var tab = await OpenAsync();
        _inputs.Answer = d => d.Name = "*.zip";

        await tab.Lfs.TrackCommand.ExecuteAsync(null);

        Assert.Equal(["*.zip"], _git.TrackedLfs);
        Assert.Equal(["*.psd", "*.zip"], tab.Lfs.Info.Patterns.Select(p => p.Pattern));
        Assert.Contains(_notifier.Items, n => n is { Severity: NotificationSeverity.Success, Title: "Git LFS" });
    }

    [Fact]
    public async Task Tracking_is_not_done_when_the_dialog_is_dismissed_or_the_pattern_is_invalid()
    {
        _git.Lfs = Used();
        using var tab = await OpenAsync();

        _inputs.Confirm = false;
        _inputs.Answer = d => d.Name = "*.zip";
        await tab.Lfs.TrackCommand.ExecuteAsync(null);

        _inputs.Confirm = true;
        _inputs.Answer = d => d.Name = "-bad";
        await tab.Lfs.TrackCommand.ExecuteAsync(null);

        Assert.Empty(_git.TrackedLfs);
    }

    [Fact]
    public async Task Untracking_removes_the_pattern_row()
    {
        _git.Lfs = Used();
        using var tab = await OpenAsync();
        var pattern = Assert.Single(tab.Lfs.Info.Patterns);

        await tab.Lfs.UntrackCommand.ExecuteAsync(pattern);

        Assert.Equal(["*.psd"], _git.UntrackedLfs);
        Assert.Empty(tab.Lfs.Info.Patterns);
    }

    [Fact]
    public async Task Pulling_downloads_the_missing_files_and_reports_how_many()
    {
        _git.Lfs = Used(new LfsFile("a.psd", "aaaaaaaaaa", false, null), new LfsFile("b.psd", "bbbbbbbbbb", false, null));
        using var tab = await OpenAsync();

        await tab.Lfs.PullCommand.ExecuteAsync(null);

        Assert.Equal(1, _git.LfsPulls);
        Assert.False(tab.Lfs.CanPull);
        Assert.Contains(_notifier.Items, n => n.Message == "2 fichiers récupérés.");
    }

    [Fact]
    public async Task Pulling_does_nothing_when_every_file_is_there()
    {
        _git.Lfs = Used(new LfsFile("a.psd", "aaaaaaaaaa", true, null));
        using var tab = await OpenAsync();

        await tab.Lfs.PullCommand.ExecuteAsync(null);

        Assert.Equal(0, _git.LfsPulls);
    }

    [Fact]
    public async Task The_menus_offer_what_applies()
    {
        _git.Lfs = Used(new LfsFile("a.psd", "aaaaaaaaaa", false, null));
        using var tab = await OpenAsync();
        var rows = RefRowBuilder.BuildRows(tab.Graph.Refs, [], string.Empty, new HashSet<string>(), _ => null, null, lfs: tab.Lfs.Info);

        var section = tab.Refs.MenuFor(rows.Single(r => r is { Kind: RefRowKind.Section, Section: RefSection.Lfs }))!;
        Assert.True(section.CanTrackLfs);
        Assert.True(section.CanPullLfs);
        Assert.False(section.CanUntrackLfs);

        var pattern = tab.Refs.MenuFor(rows.Single(r => r.Kind == RefRowKind.LfsPattern))!;
        Assert.True(pattern.CanUntrackLfs);
        Assert.Equal("*.psd", pattern.CopyText);
    }

    private sealed class FakeInputs : IInputDialogService
    {
        public Action<InputDialogViewModel> Answer { get; set; } = _ => { };

        public bool Confirm { get; set; } = true;

        public Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            Answer(dialog);
            return Task.FromResult(Confirm && dialog.CanConfirm);
        }
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string? Message)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title, message));
    }
}
