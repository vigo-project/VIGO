using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Markdown;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>A branch of the summary page: last commit, and how far it is from its upstream.</summary>
public sealed record SummaryBranchRow(GitSha Sha, string Name, string Subject, string Author, string DateText, string TrackText)
{
    public bool HasTrack => TrackText.Length > 0;
}

/// <summary>A tag of the summary page.</summary>
public sealed record SummaryTagRow(GitSha Target, string Name, string ShortSha, string DateText);

/// <summary>A commit of the summary page.</summary>
public sealed record SummaryCommitRow(GitSha Sha, string ShortSha, string Subject, string Author, string DateText);

/// <summary>
/// "Résumé" page of the left panel (J7.13, ADR-080), shown in place of the graph and of the right panel: the repository's
/// name, branch and remote, totals, the five latest branches, tags and commits, and its README. Read again when refs move
/// while the page is open. A click on a branch, tag or commit asks for it to be selected in the graph.
/// </summary>
public sealed partial class RepositorySummaryViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly WorkingTreeViewModel _tree;
    private readonly IUrlLauncher? _launcher;
    private readonly TimeProvider _time;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _loading;

    public RepositorySummaryViewModel(
        IGitService git, string repositoryPath, string repositoryName, WorkingTreeViewModel tree, IUrlLauncher? launcher,
        TimeProvider time, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        RepositoryName = repositoryName;
        _tree = tree;
        _launcher = launcher;
        _time = time;
        _lifetime = lifetime;
        _tree.RefsChanged += OnRefsChanged;
        _tree.StatusChanged += OnStatusChanged;
    }

    public string RepositoryName { get; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why nothing is shown (read error); null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Checked-out branch, or the short commit of a detached HEAD; empty without commit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBranch))]
    public partial string BranchName { get; private set; } = string.Empty;

    public bool HasBranch => BranchName.Length > 0;

    /// <summary>URL of the main remote (<c>origin</c>, else the first one); empty without remote.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemote))]
    public partial string RemoteUrl { get; private set; } = string.Empty;

    public bool HasRemote => RemoteUrl.Length > 0;

    [ObservableProperty]
    public partial string CommitCountText { get; private set; } = "0";

    [ObservableProperty]
    public partial string BranchCountText { get; private set; } = "0";

    [ObservableProperty]
    public partial string TagCountText { get; private set; } = "0";

    public ObservableCollection<SummaryBranchRow> Branches { get; } = [];

    public ObservableCollection<SummaryTagRow> Tags { get; } = [];

    public ObservableCollection<SummaryCommitRow> Commits { get; } = [];

    [ObservableProperty]
    public partial bool HasBranchRows { get; private set; }

    [ObservableProperty]
    public partial bool HasTagRows { get; private set; }

    [ObservableProperty]
    public partial bool HasCommitRows { get; private set; }

    /// <summary>README as blocks (<see cref="MarkdownView"/>); null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadme), nameof(NoReadme))]
    public partial IReadOnlyList<MarkdownBlock>? Readme { get; private set; }

    public bool HasReadme => Readme is not null;

    /// <summary>Nothing to show in the README card: read, and none found.</summary>
    public bool NoReadme => Readme is null && !IsLoading;

    [ObservableProperty]
    public partial string ReadmeName { get; private set; } = string.Empty;

    /// <summary>The README was cut at <see cref="RepositorySummary.MaxReadmeBytes"/>.</summary>
    [ObservableProperty]
    public partial bool IsReadmeTruncated { get; private set; }

    /// <summary>A branch, tag or commit was clicked: the tab closes the page and selects it in the graph.</summary>
    public event EventHandler<GitSha>? ShaRequested;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(NoReadme));

    /// <summary>Shows the page and reads its data.</summary>
    public async Task OpenAsync()
    {
        IsOpen = true;
        await LoadAsync();
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Cancel();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void Select(GitSha sha) => ShaRequested?.Invoke(this, sha);

    [RelayCommand]
    private async Task OpenRemoteAsync()
    {
        // A remote can be an ssh address: the launcher refuses what is not https.
        if (_launcher is not null && HasRemote) await _launcher.OpenAsync(RemoteUrl);
    }

    private void OnRefsChanged(object? sender, EventArgs e)
    {
        if (IsOpen) _ = LoadAsync();
    }

    // The checked-out branch can change without a ref signal (detached HEAD moved by an operation of the working tree).
    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (IsOpen) BranchName = CurrentBranchName();
    }

    private string CurrentBranchName() => _tree.Status is { } status ? status.Branch ?? status.Head?.ToShortString() ?? string.Empty : string.Empty;

    private void Cancel()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    private async Task LoadAsync()
    {
        Cancel();
        var source = _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var token = source.Token;
        IsLoading = true;
        try
        {
            var summary = _git.GetSummaryAsync(_repositoryPath, token);
            var remotes = _git.GetRemotesAsync(_repositoryPath, token);
            await Task.WhenAll(summary, remotes);
            if (token.IsCancellationRequested) return;

            switch (summary.Result.Value)
            {
                case Success<RepositorySummary> { Value: var data }:
                    Message = null;
                    Apply(data, remotes.Result.Value is Success<IReadOnlyList<GitRemote>> { Value: var list } ? list : []);
                    break;
                case Failure failure:
                    Message = failure.Message;
                    break;
                case Conflict or AuthenticationFailed:
                    Message = Loc.Get("Common.UnexpectedResult");
                    break;
                case Canceled:
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_loading == source) IsLoading = false;
        }
    }

    private void Apply(RepositorySummary data, IReadOnlyList<GitRemote> remotes)
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        string When(DateTimeOffset date) => RelativeTime.Format(date, now, zone);

        BranchName = CurrentBranchName();
        RemoteUrl = (remotes.FirstOrDefault(r => r.Name == "origin") ?? (remotes.Count > 0 ? remotes[0] : null))?.FetchUrl ?? string.Empty;
        CommitCountText = data.CommitCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        BranchCountText = data.BranchCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
        TagCountText = data.TagCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

        Replace(Branches, data.Branches.Select(b => new SummaryBranchRow(b.Sha, b.Name, b.Subject, b.Author, When(b.Date), TrackText(b))));
        Replace(Tags, data.Tags.Select(t => new SummaryTagRow(t.Target, t.Name, t.Target.ToShortString(), When(t.Date))));
        Replace(Commits, data.Commits.Select(c => new SummaryCommitRow(c.Sha, c.Sha.ToShortString(), c.Subject, c.Author, When(c.Date))));
        HasBranchRows = Branches.Count > 0;
        HasTagRows = Tags.Count > 0;
        HasCommitRows = Commits.Count > 0;

        Readme = data.Readme is { } readme ? MarkdownParser.Parse(readme.Text) : null;
        ReadmeName = data.Readme?.FileName ?? string.Empty;
        IsReadmeTruncated = data.Readme?.IsTruncated ?? false;
    }

    /// <summary>"↑2 ↓1" (commits ahead of and behind the upstream); empty without upstream or when they are level.</summary>
    internal static string TrackText(SummaryBranch branch) => (branch.Ahead, branch.Behind) switch
    {
        (> 0, > 0) => $"↑{branch.Ahead} ↓{branch.Behind}",
        (> 0, _) => $"↑{branch.Ahead}",
        (_, > 0) => $"↓{branch.Behind}",
        _ => string.Empty,
    };

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        var next = items.ToList();
        if (collection.SequenceEqual(next)) return;
        collection.Clear();
        foreach (var item in next) collection.Add(item);
    }

    public void Dispose()
    {
        _tree.RefsChanged -= OnRefsChanged;
        _tree.StatusChanged -= OnStatusChanged;
        Cancel();
    }
}
