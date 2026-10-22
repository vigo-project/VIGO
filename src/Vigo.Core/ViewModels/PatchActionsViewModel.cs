using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>Changes of one file (or folder) of the working directory to put in a patch.</summary>
public sealed record FilePatchRequest(string Path, PatchScope Scope = PatchScope.All);

/// <summary>
/// Patch files of a repository tab (J7.12, ADR-079): create one from a commit or from the working directory, apply one
/// (menu of a commit row, menus of the working directory panel, command palette).
/// </summary>
/// <remarks>
/// Creating a patch changes nothing in the repository. Applying one goes through <see cref="StagingActions"/> (one operation at a
/// time with the index operations, status reread): a mailbox (<c>git format-patch</c>) creates commits and, stopped on conflicts,
/// stays in progress (<see cref="RepositoryOperationKind.ApplyMailbox"/>: the banner of <see cref="MergeActionsViewModel"/>
/// continues, skips or aborts it); a plain diff changes the working directory and the index and may leave conflicts to resolve.
/// </remarks>
public sealed partial class PatchActionsViewModel : ObservableObject
{
    private const int MaxSlugLength = 52;

    private static IReadOnlyList<FileDialogFilter> PatchFilters =>
    [
        new(Loc.Get("Patch.Filter.Patch"), [".patch", ".diff"]),
        new(Loc.Get("Patch.Filter.All"), ["*"]),
    ];

    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly WorkingTreeViewModel _tree;
    private readonly IFileDialogs? _dialogs;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;

    public PatchActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, WorkingTreeViewModel tree, IFileDialogs? dialogs, IUserNotifier? notifier,
        CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _tree = tree;
        _dialogs = dialogs;
        _notifier = notifier;
        _lifetime = lifetime;
    }

    private StagingActions Actions => _tree.Actions;

    /// <summary>File dialogs are available (no window to show them in a test or a headless host).</summary>
    public bool CanPrompt => _dialogs is not null;

    /// <summary>Creating a patch of a commit only reads the repository: a bare one is fine.</summary>
    public bool CanCreateFromCommit => CanPrompt;

    /// <summary>Changes of the working directory to export: a working directory with something modified.</summary>
    public bool CanCreateFromWorkingTree => CanPrompt && _tree.IsAvailable && (_tree.Unstaged.Count > 0 || _tree.Staged.Count > 0);

    /// <summary>Applying needs a working directory, no operation in progress and nothing else running.</summary>
    public bool CanApply => CanPrompt && _tree.IsAvailable && _tree.Status?.Operation is null && !Actions.IsBusy;

    // ---- Create ------------------------------------------------------------------------------------------------------

    /// <summary>"Créer un patch…" on a commit row: one <c>format-patch</c> file.</summary>
    [RelayCommand]
    private Task CreateCommitPatchAsync(GitSha commit) => CreateFromCommitsAsync([commit]);

    /// <summary>Several commits of a linear chain (oldest first or in any order) in one mbox file.</summary>
    public async Task CreateFromCommitsAsync(IReadOnlyList<GitSha> commits)
    {
        if (!CanCreateFromCommit || commits.Count == 0) return;
        var subject = commits.Count == 1 ? _graph.FindCommit(commits[0])?.Subject : null;
        var name = commits.Count == 1 ? SuggestedCommitName(subject, commits[0]) : Loc.Format("Patch.Name.Commits", commits.Count);
        await SaveAsync(name, Loc.Get("Patch.Create.Failed"), path => _git.CreatePatchFromCommitsAsync(_repositoryPath, commits, path, _lifetime));
    }

    /// <summary>"Créer un patch des modifications…": the unstaged, the staged or all the changes.</summary>
    [RelayCommand]
    private async Task CreateWorkingTreePatchAsync(PatchScope scope)
    {
        if (!CanCreateFromWorkingTree) return;
        await SaveAsync(SuggestedChangesName(_tree.Status?.Branch ?? _graph.HeadBranch, scope), Loc.Get("Patch.Create.Failed"),
            path => _git.CreatePatchFromWorkingTreeAsync(_repositoryPath, scope, null, path, _lifetime));
    }

    /// <summary>"Créer un patch de ce fichier…" on a file of the working directory panel.</summary>
    [RelayCommand]
    private async Task CreateFilePatchAsync(FilePatchRequest request)
    {
        if (!CanCreateFromWorkingTree) return;
        var file = System.IO.Path.GetFileName(request.Path.TrimEnd('/', '\\'));
        await SaveAsync(Slug(file) + ".patch", Loc.Get("Patch.Create.Failed"),
            path => _git.CreatePatchFromWorkingTreeAsync(_repositoryPath, request.Scope, [request.Path], path, _lifetime));
    }

    private async Task SaveAsync(string suggestedName, string failureTitle, Func<string, Task<GitResult>> write)
    {
        if (_dialogs is null) return;
        var path = await _dialogs.PickSaveFileAsync(suggestedName, PatchFilters, _lifetime);
        if (path is null || _lifetime.IsCancellationRequested) return;

        switch ((await write(path)).Value)
        {
            case Done:
                _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Patch.Create.Done"), path);
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Error, failureTitle, f.Message);
                break;
            case AuthenticationFailed a:
                _notifier?.Notify(NotificationSeverity.Error, failureTitle, a.Message);
                break;
            case Conflict c:
                _notifier?.Notify(NotificationSeverity.Error, failureTitle, string.Join(", ", c.Paths));
                break;
            case Canceled:
                break;
        }
    }

    // ---- Apply -------------------------------------------------------------------------------------------------------

    /// <summary>"Appliquer un patch…": asks for the file, then applies it (after a confirmation when changes are in progress).</summary>
    [RelayCommand]
    private async Task ApplyPatchAsync()
    {
        if (!CanApply || _dialogs is null) return;
        var path = await _dialogs.PickOpenFileAsync(PatchFilters, _lifetime);
        if (path is null || _lifetime.IsCancellationRequested) return;
        await ApplyFileAsync(path);
    }

    /// <summary>Applies <paramref name="patchPath"/> (also the entry point of a patch file dropped on the repository view).</summary>
    public async Task ApplyFileAsync(string patchPath)
    {
        if (!CanApply) return;
        if ((_tree.Staged.Count > 0 || _tree.Unstaged.Count > 0)
            && !await Actions.ConfirmAsync(Loc.Get("Patch.Apply.DirtyTitle"), Loc.Get("Patch.Apply.DirtyMessage"), Loc.Get("Patch.Apply.Confirm")))
        {
            return;
        }

        var name = System.IO.Path.GetFileName(patchPath);
        await Actions.RunAsync<PatchApplyOutcome>(Loc.Format("Patch.Apply.Failed", name),
            ct => _git.ApplyPatchAsync(_repositoryPath, patchPath, ct),
            outcome => _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Patch.Apply.Done"), outcome == PatchApplyOutcome.Committed
                ? Loc.Format("Patch.Apply.DoneCommitted", name)
                : Loc.Format("Patch.Apply.DoneChanges", name)),
            conflict => NotifyConflict(conflict, name),
            new UndoStep(Loc.Format("Patch.Apply.Undo", name)));
        if (!_lifetime.IsCancellationRequested) await _graph.RefreshAsync();
        if (_tree.Status?.Operation is not null || _tree.HasConflicts) _graph.SelectWorkInProgress();
    }

    private void NotifyConflict(Conflict conflict, string name)
    {
        var count = conflict.Paths.Count == 1 ? Loc.Get("Common.ConflictCount.One") : Loc.Format("Common.ConflictCount.Many", conflict.Paths.Count);
        _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("Common.ConflictDetected"), Loc.Format("Patch.Apply.ConflictDetail", name, count));
    }

    // ---- Names -------------------------------------------------------------------------------------------------------

    /// <summary>"0001-fix-the-cache.patch": what <c>git format-patch</c> names the file of a commit.</summary>
    internal static string SuggestedCommitName(string? subject, GitSha commit) =>
        Slug(subject) is { Length: > 0 } slug ? $"0001-{slug}.patch" : $"0001-{commit.ToShortString()}.patch";

    /// <summary>"feature-x-changes.patch" (all or unstaged), "feature-x-staged.patch".</summary>
    internal static string SuggestedChangesName(string? branch, PatchScope scope)
    {
        var slug = Slug(branch);
        if (slug.Length == 0) slug = "working-tree";
        return scope == PatchScope.Staged ? $"{slug}-staged.patch" : $"{slug}-changes.patch";
    }

    /// <summary>
    /// Lowercase ASCII letters and digits separated by single dashes, 52 characters at most (what <c>git format-patch</c> keeps
    /// of a subject); empty when nothing is left (a subject without a letter).
    /// </summary>
    internal static string Slug(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(Math.Min(text.Length, MaxSlugLength));
        var pendingDash = false;
        foreach (var c in text)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or (>= 'A' and <= 'Z'))
            {
                if (pendingDash && builder.Length > 0) builder.Append('-');
                pendingDash = false;
                builder.Append(char.ToLowerInvariant(c));
                if (builder.Length >= MaxSlugLength) break;
            }
            else
            {
                pendingDash = true;
            }
        }
        return builder.ToString().TrimEnd('-');
    }
}
