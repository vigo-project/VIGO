using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Core.Tests;

/// <summary>In-memory <see cref="IGitService"/>: repositories to open, scripted clones; the rest is not implemented.</summary>
internal sealed class FakeGit : IGitService
{
    public Dictionary<string, GitResult<RepositoryInfo>> Repos { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<GitResult<string>> GetGitVersionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<string>>(new Success<string>("2.50.0"));

    /// <summary>Called with each path <see cref="OpenAsync"/> is asked for (to cancel in the middle of a restoration).</summary>
    public Action<string>? OnOpen { get; set; }

    public Task<GitResult<RepositoryInfo>> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        OnOpen?.Invoke(path);
        return Task.FromResult(Repos.TryGetValue(path, out var r) ? r : new Failure("not a repo"));
    }

    public Task<GitResult<RepositoryInfo>> InitAsync(string path, string initialBranch = "main", CancellationToken cancellationToken = default) =>
        OpenAsync(path, cancellationToken);

    public Task<GitResult<GitIdentity>> GetIdentityAsync(string? repositoryPath = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<GitIdentity>>(new Success<GitIdentity>(new GitIdentity("Ada Lovelace", "ada@example.com")));

    /// <summary>What <see cref="GetRefsAsync"/> returns: no reference, on "main".</summary>
    public RepositoryRefs Refs { get; set; } = new([], null, "main");

    public Task<GitResult<RepositoryRefs>> GetRefsAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<RepositoryRefs>>(new Success<RepositoryRefs>(Refs));

    public CommitLogOptions? LastLogOptions { get; private set; }

    /// <summary>Replaces the empty history of <see cref="ReadLogAsync"/> (a synthetic one, streamed by pages).</summary>
    public Func<CommitLogOptions, CommitPageHandler, CancellationToken, Task<GitResult>>? ReadLog { get; set; }

    public Task<GitResult> ReadLogAsync(string repositoryPath, CommitLogOptions options, CommitPageHandler onPage, CancellationToken cancellationToken = default)
    {
        LastLogOptions = options;
        return ReadLog?.Invoke(options, onPage, cancellationToken) ?? Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult<CommitDetails>> GetCommitAsync(string repositoryPath, GitSha sha, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<CommitDetails>>(new Failure("not implemented"));

    public Task<GitResult<FileDiff>> GetFileDiffAsync(
        string repositoryPath, GitSha sha, GitSha? firstParent, ChangedFile file, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<FileDiff>>(new Failure("not implemented"));

    public Task<GitResult<WorkingTreeStatus>> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<WorkingTreeStatus>>(new Success<WorkingTreeStatus>(WorkingTreeStatus.Empty));

    /// <summary>File texts returned by <see cref="ReadFileTextAsync"/>, by version; a missing key means "no such file".</summary>
    public Dictionary<FileVersion, string> FileTexts { get; } = [];

    public Task<GitResult<string?>> ReadFileTextAsync(string repositoryPath, FileVersion version, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<string?>>(new Success<string?>(FileTexts.GetValueOrDefault(version)));

    public Task<GitResult<byte[]?>> ReadFileBytesAsync(string repositoryPath, FileVersion version, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<byte[]?>>(new Success<byte[]?>(null));

    public Task<GitResult<FileDiff>> GetWorkingFileDiffAsync(
        string repositoryPath, StatusEntry entry, WorkingDiffSide side, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<FileDiff>>(new Failure("not implemented"));

    public Task<GitResult> StageAsync(string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> StageAllAsync(string repositoryPath, CancellationToken cancellationToken = default) => NotImplemented();

    public Task<GitResult> UnstageAsync(string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> DiscardAsync(string repositoryPath, IReadOnlyList<StatusEntry> entries, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> IgnoreAsync(string repositoryPath, IReadOnlyList<string> patterns, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> ApplyPatchAsync(
        string repositoryPath, string patch, PatchTarget target, bool reverse, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult<GitSha>> CommitAsync(string repositoryPath, CommitRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<GitSha>>(new Failure("not implemented"));

    /// <summary>Values <see cref="GetConfigValueAsync"/> returns, whatever the repository.</summary>
    public Dictionary<string, string> Config { get; } = [];

    public Task<GitResult<string?>> GetConfigValueAsync(string? repositoryPath, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<string?>>(new Success<string?>(Config.GetValueOrDefault(key)));

    /// <summary>What <see cref="GetStagedPatchAsync"/> returns.</summary>
    public StagedPatch StagedPatch { get; set; } = new(string.Empty, false, []);

    public Task<GitResult<StagedPatch>> GetStagedPatchAsync(string repositoryPath, int maxChars = 60_000, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<StagedPatch>>(new Success<StagedPatch>(StagedPatch));

    public Task<GitResult> FetchAsync(string repositoryPath, IProgress<GitProgress>? progress, bool prune, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public List<string> FetchedRemotes { get; } = [];

    public Task<GitResult> FetchRemoteAsync(
        string repositoryPath, string remote, IProgress<GitProgress>? progress, bool prune, CancellationToken cancellationToken = default)
    {
        FetchedRemotes.Add(remote);
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public int BackgroundFetches { get; private set; }

    public bool? LastPrune { get; private set; }

    public Task<GitResult> FetchInBackgroundAsync(string repositoryPath, bool prune, CancellationToken cancellationToken = default)
    {
        BackgroundFetches++;
        LastPrune = prune;
        return Task.FromResult<GitResult>(Done.Instance);
    }

    /// <summary>Results of the next clones, in order; an exhausted queue clones successfully into the destination.</summary>
    public Queue<GitResult<RepositoryInfo>> CloneResults { get; } = new();

    public List<CloneRequest> Clones { get; } = [];

    public Task<GitResult<RepositoryInfo>> CloneAsync(
        CloneRequest request, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default)
    {
        Clones.Add(request);
        progress?.Report(new GitProgress(GitProgressPhase.Receiving, "Receiving objects:  50% (1/2)", 50, 1, 2));
        return Task.FromResult(CloneResults.TryDequeue(out var result)
            ? result
            : new Success<RepositoryInfo>(new RepositoryInfo(request.Directory, Path.Combine(request.Directory, ".git"),
                Path.GetFileName(request.Directory), "main", null, false, false, false)));
    }

    public Task<GitResult> PullAsync(
        string repositoryPath, PullMode mode, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> PushAsync(
        string repositoryPath, PushRequest request, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public List<GitRemote> Remotes { get; } = [];

    public Task<GitResult<IReadOnlyList<GitRemote>>> GetRemotesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IReadOnlyList<GitRemote>>>(new Success<IReadOnlyList<GitRemote>>([.. Remotes]));

    // Conflict prevention (J7.11): what the dry run finds, and how many times each question was asked.
    public ConflictTarget? ConflictTarget { get; set; }

    public IReadOnlyList<string> PredictedConflicts { get; set; } = [];

    public IReadOnlyList<string> ChangedOnTarget { get; set; } = [];

    public string? PredictFailure { get; set; }

    public int TargetCalls { get; private set; }

    public int PredictCalls { get; private set; }

    public Task<GitResult<ConflictTarget>> GetConflictTargetAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        TargetCalls++;
        return Task.FromResult<GitResult<ConflictTarget>>(ConflictTarget is { } target ? new Success<ConflictTarget>(target) : new Failure("no target"));
    }

    public Task<GitResult> CreatePatchFromCommitsAsync(string repositoryPath, IReadOnlyList<GitSha> commits, string patchPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult>(new Failure("not implemented"));

    public Task<GitResult> CreatePatchFromWorkingTreeAsync(
        string repositoryPath, PatchScope scope, IReadOnlyList<string>? paths, string patchPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult>(new Failure("not implemented"));

    public Task<GitResult<PatchApplyOutcome>> ApplyPatchAsync(string repositoryPath, string patchPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<PatchApplyOutcome>>(new Failure("not implemented"));

    public Task<GitResult<IReadOnlyList<string>>> PredictConflictsAsync(
        string repositoryPath, GitSha head, GitSha target, CancellationToken cancellationToken = default)
    {
        PredictCalls++;
        return Task.FromResult<GitResult<IReadOnlyList<string>>>(PredictFailure is { } reason ? new Failure(reason) : new Success<IReadOnlyList<string>>(PredictedConflicts));
    }

    public Task<GitResult<IReadOnlyList<string>>> GetChangedFilesSinceMergeBaseAsync(
        string repositoryPath, GitSha head, GitSha target, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IReadOnlyList<string>>>(new Success<IReadOnlyList<string>>(ChangedOnTarget));

    // Summary page (J7.13): what the next reads return, and how many were made.
    public RepositorySummary Summary { get; set; } = new(0, 0, 0, [], [], [], null);

    public string? SummaryFailure { get; set; }

    public int SummaryCalls { get; private set; }

    public Task<GitResult<RepositorySummary>> GetSummaryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        SummaryCalls++;
        return Task.FromResult<GitResult<RepositorySummary>>(SummaryFailure is { } reason ? new Failure(reason) : new Success<RepositorySummary>(Summary));
    }

    // Statistics page (J7.14): what the next read returns, and how many were made.
    public RepositoryStats Stats { get; set; } = RepositoryStats.Empty;

    public string? StatsFailure { get; set; }

    public int StatsCalls { get; private set; }

    /// <summary>When set, the read reports this many commits then waits for the token (cancellation test).</summary>
    public TaskCompletionSource? StatsGate { get; set; }

    /// <summary>The <c>includeSubmodules</c> argument of the last read.</summary>
    public bool? StatsIncludedSubmodules { get; private set; }

    /// <summary>The repository path of every read, in order.</summary>
    public List<string> StatsPaths { get; } = [];

    public async Task<GitResult<RepositoryStats>> ReadCommitStatsAsync(string repositoryPath, IProgress<int>? progress, bool includeSubmodules = false, CancellationToken cancellationToken = default)
    {
        StatsCalls++;
        StatsIncludedSubmodules = includeSubmodules;
        StatsPaths.Add(repositoryPath);
        progress?.Report(1_200);
        if (StatsGate is { } gate)
        {
            try
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Canceled.Instance;
            }
        }
        return StatsFailure is { } reason ? new Failure(reason) : new Success<RepositoryStats>(Stats);
    }

    public List<Worktree> Worktrees { get; } = [];

    public List<AddWorktreeRequest> AddedWorktrees { get; } = [];

    public List<(string Path, bool Force)> RemovedWorktrees { get; } = [];

    /// <summary>Result of the next <see cref="RemoveWorktreeAsync"/> calls; success when null.</summary>
    public GitResult? RemoveWorktreeResult { get; set; }

    public Task<GitResult<IReadOnlyList<Worktree>>> GetWorktreesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IReadOnlyList<Worktree>>>(new Success<IReadOnlyList<Worktree>>([.. Worktrees]));

    public Task<GitResult> AddWorktreeAsync(string repositoryPath, AddWorktreeRequest request, CancellationToken cancellationToken = default)
    {
        AddedWorktrees.Add(request);
        Worktrees.Add(new Worktree(request.Path, null, request.NewBranch ?? request.Branch, IsMain: false, IsDetached: request.Branch is null && request.NewBranch is null));
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> RemoveWorktreeAsync(string repositoryPath, string worktreePath, bool force, CancellationToken cancellationToken = default)
    {
        RemovedWorktrees.Add((worktreePath, force));
        if (RemoveWorktreeResult is { } refused && refused.Value is not Done) return Task.FromResult(refused);
        Worktrees.RemoveAll(w => w.Path == worktreePath);
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> PruneWorktreesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        Worktrees.RemoveAll(w => w.IsPrunable);
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public List<Submodule> Submodules { get; } = [];

    /// <summary>Paths given to <see cref="UpdateSubmodulesAsync"/> (null: all of them).</summary>
    public List<string?> UpdatedSubmodules { get; } = [];

    public List<(string Path, bool Force)> DeinitializedSubmodules { get; } = [];

    public List<(string Url, string? Path)> AddedSubmodules { get; } = [];

    /// <summary>Result of the next <see cref="DeinitSubmoduleAsync"/> calls without force; success when null.</summary>
    public GitResult? DeinitSubmoduleResult { get; set; }

    public Task<GitResult<IReadOnlyList<Submodule>>> GetSubmodulesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IReadOnlyList<Submodule>>>(new Success<IReadOnlyList<Submodule>>([.. Submodules]));

    public Task<GitResult> UpdateSubmodulesAsync(string repositoryPath, string? path, CancellationToken cancellationToken = default)
    {
        UpdatedSubmodules.Add(path);
        for (var i = 0; i < Submodules.Count; i++)
        {
            if (path is null || Submodules[i].Path == path) Submodules[i] = Submodules[i] with { State = SubmoduleState.UpToDate };
        }
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> DeinitSubmoduleAsync(string repositoryPath, string path, bool force, CancellationToken cancellationToken = default)
    {
        DeinitializedSubmodules.Add((path, force));
        if (!force && DeinitSubmoduleResult is { } refused && refused.Value is not Done) return Task.FromResult(refused);
        for (var i = 0; i < Submodules.Count; i++)
        {
            if (Submodules[i].Path == path) Submodules[i] = Submodules[i] with { State = SubmoduleState.NotInitialized };
        }
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> AddSubmoduleAsync(string repositoryPath, string url, string? path, CancellationToken cancellationToken = default)
    {
        AddedSubmodules.Add((url, path));
        var folder = path ?? url[(url.LastIndexOf('/') + 1)..];
        Submodules.Add(new Submodule(folder, folder, url, null, SubmoduleState.UpToDate, null));
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public LfsInfo Lfs { get; set; } = LfsInfo.NotUsed;

    public List<string> TrackedLfs { get; } = [];

    public List<string> UntrackedLfs { get; } = [];

    public int LfsPulls { get; private set; }

    public Task<GitResult<LfsInfo>> GetLfsAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<LfsInfo>>(new Success<LfsInfo>(Lfs));

    public Task<GitResult> LfsTrackAsync(string repositoryPath, string pattern, CancellationToken cancellationToken = default)
    {
        TrackedLfs.Add(pattern);
        Lfs = Lfs with { IsUsed = true, Patterns = [.. Lfs.Patterns, new LfsPattern(pattern, ".gitattributes")] };
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> LfsUntrackAsync(string repositoryPath, string pattern, CancellationToken cancellationToken = default)
    {
        UntrackedLfs.Add(pattern);
        Lfs = Lfs with { Patterns = [.. Lfs.Patterns.Where(p => p.Pattern != pattern)] };
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> LfsPullAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        LfsPulls++;
        Lfs = Lfs with { Files = [.. Lfs.Files.Select(f => f with { IsDownloaded = true })] };
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public GitFlowConfig? GitFlow { get; set; }

    public List<GitFlowConfig> GitFlowInits { get; } = [];

    public List<GitFlowStartRequest> GitFlowStarts { get; } = [];

    public List<GitFlowFinishRequest> GitFlowFinishes { get; } = [];

    /// <summary>What the next finish returns; the default is a success merged into the development branch.</summary>
    public GitResult<GitFlowFinished>? GitFlowFinishResult { get; set; }

    public Task<GitResult<GitFlowConfig?>> GetGitFlowAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<GitFlowConfig?>>(new Success<GitFlowConfig?>(GitFlow));

    public Task<GitResult> GitFlowInitAsync(string repositoryPath, GitFlowConfig config, CancellationToken cancellationToken = default)
    {
        GitFlowInits.Add(config);
        GitFlow = config;
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> GitFlowStartAsync(string repositoryPath, GitFlowConfig config, GitFlowStartRequest request, CancellationToken cancellationToken = default)
    {
        GitFlowStarts.Add(request);
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult<GitFlowFinished>> GitFlowFinishAsync(string repositoryPath, GitFlowConfig config, GitFlowFinishRequest request, CancellationToken cancellationToken = default)
    {
        GitFlowFinishes.Add(request);
        return Task.FromResult(GitFlowFinishResult
            ?? new Success<GitFlowFinished>(new GitFlowFinished([config.Develop], request.Kind == GitFlowKind.Feature ? null : config.TagOf(request.Name), !request.KeepBranch)));
    }

    public Task<GitResult> SetRemoteUrlAsync(string repositoryPath, string remoteName, string url, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> CheckoutAsync(string repositoryPath, CheckoutTarget target, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult<bool>> IsAncestorAsync(string repositoryPath, GitSha ancestor, GitSha descendant, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<bool>>(new Failure("not implemented"));

    public Task<GitResult<MergeOutcome>> MergeAsync(string repositoryPath, string source, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<MergeOutcome>>(new Failure("not implemented"));

    public Task<GitResult> FastForwardBranchAsync(string repositoryPath, string branch, GitSha target, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult<RebaseOutcome>> RebaseAsync(string repositoryPath, string upstream, string? branch, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<RebaseOutcome>>(new Failure("not implemented"));

    /// <summary>Commits returned by <see cref="GetRebaseCommitsAsync"/>, whatever the upstream.</summary>
    public List<RebaseCommit> RebaseCommits { get; } = [];

    /// <summary>Upstream and todo list of the last <see cref="InteractiveRebaseAsync"/>.</summary>
    public (string Upstream, IReadOnlyList<RebaseTodoItem> Todo)? LastInteractiveRebase { get; private set; }

    public GitResult<RebaseOutcome> InteractiveRebaseResult { get; set; } = new Success<RebaseOutcome>(RebaseOutcome.Rebased);

    public Task<GitResult<IReadOnlyList<RebaseCommit>>> GetRebaseCommitsAsync(string repositoryPath, string upstream, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IReadOnlyList<RebaseCommit>>>(new Success<IReadOnlyList<RebaseCommit>>([.. RebaseCommits]));

    public Task<GitResult<RebaseOutcome>> InteractiveRebaseAsync(
        string repositoryPath, string upstream, IReadOnlyList<RebaseTodoItem> todo, CancellationToken cancellationToken = default)
    {
        LastInteractiveRebase = (upstream, todo);
        return Task.FromResult(InteractiveRebaseResult);
    }

    public Task<GitResult> ContinueOperationAsync(string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> SkipOperationAsync(string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult<GitSha>> CherryPickAsync(string repositoryPath, GitSha commit, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<GitSha>>(new Failure("not implemented"));

    public Task<GitResult<GitSha>> RevertAsync(string repositoryPath, GitSha commit, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<GitSha>>(new Failure("not implemented"));

    public Task<GitResult> ResetAsync(string repositoryPath, GitSha target, ResetMode mode, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult<RefSnapshot>> GetRefSnapshotAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<RefSnapshot>>(new Failure("not implemented"));

    public Task<GitResult> RestoreRefsAsync(
        string repositoryPath, RefSnapshot current, RefSnapshot target, ResetMode headMode, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult<int>> CountCommitsAsync(string repositoryPath, GitSha since, GitSha until, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<int>>(new Failure("not implemented"));

    public Task<GitResult> AbortOperationAsync(string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default) =>
        NotImplemented();

    /// <summary>Conflicted files returned by <see cref="GetConflictAsync"/>, by path.</summary>
    public Dictionary<string, ConflictFile> Conflicts { get; } = [];

    /// <summary>Resolutions received by <see cref="ResolveConflictAsync"/>, in order.</summary>
    public List<(string Path, ConflictResolution Resolution)> Resolutions { get; } = [];

    /// <summary>Merge tools launched by <see cref="RunMergeToolAsync"/>, in order.</summary>
    public List<(string Path, string? Tool)> MergeTools { get; } = [];

    public Task<GitResult<ConflictFile>> GetConflictAsync(string repositoryPath, StatusEntry entry, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<ConflictFile>>(Conflicts.TryGetValue(entry.Path, out var file)
            ? new Success<ConflictFile>(file)
            : new Failure($"no conflict on {entry.Path}"));

    public Task<GitResult> ResolveConflictAsync(string repositoryPath, string path, ConflictResolution resolution, CancellationToken cancellationToken = default)
    {
        Resolutions.Add((path, resolution));
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> RunMergeToolAsync(string repositoryPath, string path, string? tool, CancellationToken cancellationToken = default)
    {
        MergeTools.Add((path, tool));
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult<GitSha>> StashAsync(string repositoryPath, StashRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<GitSha>>(new Failure("not implemented"));

    public Task<GitResult> ApplyStashAsync(string repositoryPath, GitSha stash, bool drop, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> ApplyStashFilesAsync(string repositoryPath, GitSha stash, IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> DropStashAsync(string repositoryPath, GitSha stash, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> CreateBranchAsync(string repositoryPath, CreateBranchRequest request, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> RenameBranchAsync(string repositoryPath, string name, string newName, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> DeleteBranchAsync(string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult<int>> CountExclusiveCommitsAsync(
        string repositoryPath, IReadOnlyList<string> canonicalNames, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<int>>(new Failure("not implemented"));

    public Task<GitResult> CreateTagAsync(string repositoryPath, CreateTagRequest request, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> DeleteTagAsync(string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> PushRefAsync(
        string repositoryPath, RefPush push, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> AddRemoteAsync(string repositoryPath, string name, string url, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> RenameRemoteAsync(string repositoryPath, string name, string newName, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> RemoveRemoteAsync(string repositoryPath, string name, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public List<(RemoteUrl Url, string UserName, string Secret)> StoredCredentials { get; } = [];

    public Task<GitResult> StoreCredentialAsync(
        string? repositoryPath, RemoteUrl url, string userName, string secret, CancellationToken cancellationToken = default)
    {
        StoredCredentials.Add((url, userName, secret));
        return Task.FromResult<GitResult>(Done.Instance);
    }

    public Task<GitResult> ReadFileHistoryAsync(
        string repositoryPath, string path, bool follow, int pageSize, FileHistoryPageHandler onPage, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult> BlameAsync(
        string repositoryPath, string path, string? revision, BlameHunkHandler onHunks, CancellationToken cancellationToken = default) =>
        NotImplemented();

    public Task<GitResult<IReadOnlyList<string>>> ListFilesAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<IReadOnlyList<string>>>(new Failure("not implemented"));

    public Task<GitResult> RestoreFileAsync(
        string repositoryPath, GitSha commit, string sourcePath, string path, CancellationToken cancellationToken = default) =>
        NotImplemented();

    private static Task<GitResult> NotImplemented() => Task.FromResult<GitResult>(new Failure("not implemented"));
}
