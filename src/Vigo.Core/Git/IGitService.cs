using Vigo.Core.Models;
using Vigo.Core.Results;

namespace Vigo.Core.Git;

/// <summary>
/// Testable Git facade. Implemented in Vigo.Git: LibGit2Sharp for reads,
/// git.exe as a subprocess for network and complex operations (ADR-003).
/// Every method is asynchronous, cancelable and never performs I/O on the caller's thread.
/// </summary>
public interface IGitService
{
    /// <summary>Detected git.exe version (e.g. "2.50.1.windows.1").</summary>
    Task<GitResult<string>> GetGitVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the repository containing <paramref name="path"/> (searching parent directories).</summary>
    Task<GitResult<RepositoryInfo>> OpenAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Initializes a new repository.</summary>
    Task<GitResult<RepositoryInfo>> InitAsync(string path, string initialBranch = "main", CancellationToken cancellationToken = default);

    /// <summary>Global identity (or the repository one when <paramref name="repositoryPath"/> is given).</summary>
    Task<GitResult<GitIdentity>> GetIdentityAsync(string? repositoryPath = null, CancellationToken cancellationToken = default);

    /// <summary>Local and remote branches (with upstream tracking), tags, stashes and HEAD.</summary>
    Task<GitResult<RepositoryRefs>> GetRefsAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams the history reachable from every branch, remote branch, tag and HEAD, in topological order,
    /// by pages of <see cref="CommitLogOptions.PageSize"/> commits. Each page is delivered as soon as it is read;
    /// the next one is not read until <paramref name="onPage"/> completes. An empty repository yields no page.
    /// </summary>
    Task<GitResult> ReadLogAsync(string repositoryPath, CommitLogOptions options, CommitPageHandler onPage, CancellationToken cancellationToken = default);

    /// <summary>Full message, signatures and changed files of a commit.</summary>
    Task<GitResult<CommitDetails>> GetCommitAsync(string repositoryPath, GitSha sha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unified diff of one file of a commit against its first parent (<paramref name="firstParent"/>, null for a
    /// root commit), 3 lines of context, renames followed.
    /// </summary>
    Task<GitResult<FileDiff>> GetFileDiffAsync(
        string repositoryPath, GitSha sha, GitSha? firstParent, ChangedFile file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whole text of one version of a file (side-by-side diff, editors), decoded as UTF-8; null when the file does
    /// not exist in that version (added or deleted file). Files over <see cref="MaxFileTextBytes"/> give a
    /// <see cref="Failure"/>.
    /// </summary>
    Task<GitResult<string?>> ReadFileTextAsync(string repositoryPath, FileVersion version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raw bytes of one version of a file (image diff); null when the file does not exist in that version. Files over
    /// <see cref="DiffImages.MaxBytes"/> give a <see cref="Failure"/>.
    /// </summary>
    Task<GitResult<byte[]?>> ReadFileBytesAsync(string repositoryPath, FileVersion version, CancellationToken cancellationToken = default);

    /// <summary>Largest file <see cref="ReadFileTextAsync"/> reads (20 MB).</summary>
    const long MaxFileTextBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Streams the commits that changed <paramref name="path"/> (repository-relative), newest first, from HEAD, by pages
    /// of <paramref name="pageSize"/> entries: merge commits left out, renames followed when <paramref name="follow"/>
    /// (<c>git log --follow</c>). Each page is delivered as soon as it is read.
    /// </summary>
    Task<GitResult> ReadFileHistoryAsync(
        string repositoryPath, string path, bool follow, int pageSize, FileHistoryPageHandler onPage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commit that last changed each line of <paramref name="path"/> at <paramref name="revision"/>, or of the file on
    /// disk when null (uncommitted lines get <see cref="BlameCommit.IsUncommitted"/>), with <c>git blame --incremental</c>:
    /// hunks are delivered in batches as git finds them, in no particular order.
    /// </summary>
    Task<GitResult> BlameAsync(
        string repositoryPath, string path, string? revision, BlameHunkHandler onHunks, CancellationToken cancellationToken = default);

    /// <summary>Files of the index (<c>git ls-files</c>), repository-relative paths.</summary>
    Task<GitResult<IReadOnlyList<string>>> ListFilesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the version of <paramref name="sourcePath"/> in <paramref name="commit"/> over <paramref name="path"/> in
    /// the working directory (both repository-relative; they differ for a file renamed since). The index is not
    /// touched; local changes of the file are lost.
    /// </summary>
    Task<GitResult> RestoreFileAsync(
        string repositoryPath, GitSha commit, string sourcePath, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Staged, unstaged, untracked (each file listed) and unmerged paths of the working directory, plus the
    /// current branch and its upstream. Ignored files are left out.
    /// </summary>
    Task<GitResult<WorkingTreeStatus>> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unified diff of a working-directory change: the staged part (HEAD → index) or the unstaged part
    /// (index → working tree; the whole file for an untracked one), 3 lines of context.
    /// </summary>
    Task<GitResult<FileDiff>> GetWorkingFileDiffAsync(
        string repositoryPath, StatusEntry entry, WorkingDiffSide side, CancellationToken cancellationToken = default);

    /// <summary>
    /// The staged changes as text for an AI engine (J7.9): diffstat then <c>diff --cached</c> with little context, binary
    /// files and lock files reduced to their stat line, cut at <paramref name="maxChars"/>; plus the last commit subjects.
    /// </summary>
    Task<GitResult<StagedPatch>> GetStagedPatchAsync(string repositoryPath, int maxChars = 60_000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages new, modified and deleted files (<c>git add -A</c>); staging an unmerged path marks it as resolved.
    /// Paths are repository-relative and literal.
    /// </summary>
    Task<GitResult> StageAsync(string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Stages every change of the working directory.</summary>
    Task<GitResult> StageAllAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the index entries of <paramref name="paths"/> back to HEAD (<c>git reset</c>; also works before the first
    /// commit). For a staged rename, pass both paths.
    /// </summary>
    Task<GitResult> UnstageAsync(string repositoryPath, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the unstaged changes of <paramref name="entries"/>: tracked files are restored from the index,
    /// untracked files are deleted. Staged changes are kept. Irreversible.
    /// </summary>
    Task<GitResult> DiscardAsync(string repositoryPath, IReadOnlyList<StatusEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds <paramref name="patterns"/> (as written in a <c>.gitignore</c>: <c>audit/</c>) to the <c>.gitignore</c> at the top
    /// level of the working directory, creating it; patterns already there are not added again.
    /// </summary>
    Task<GitResult> IgnoreAsync(string repositoryPath, IReadOnlyList<string> patterns, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a patch (<see cref="PatchBuilder"/>) with <c>git apply --recount</c>, to the index or to the working
    /// tree, possibly reversed.
    /// </summary>
    Task<GitResult> ApplyPatchAsync(
        string repositoryPath, string patch, PatchTarget target, bool reverse, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the index (<c>git commit</c>, message on stdin): the user's config applies (signing, cleanup mode) and
    /// the hooks run unless <see cref="CommitRequest.SkipHooks"/>. Fails when nothing is staged, except for an amend.
    /// Returns the new HEAD.
    /// </summary>
    Task<GitResult<GitSha>> CommitAsync(string repositoryPath, CommitRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches every remote (<c>git fetch --all</c>; <paramref name="prune"/> forces <c>--prune</c>, otherwise <c>fetch.prune</c> decides), reporting the progress.
    /// Credentials come from Git Credential Manager / SSH; no console prompt.
    /// </summary>
    Task<GitResult> FetchAsync(string repositoryPath, IProgress<GitProgress>? progress, bool prune, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches one remote (<c>git fetch &lt;remote&gt;</c>), like <see cref="FetchAsync"/>. A name starting with
    /// "-" is refused.
    /// </summary>
    Task<GitResult> FetchRemoteAsync(string repositoryPath, string remote, IProgress<GitProgress>? progress, bool prune, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="FetchAsync"/> without any interaction (automatic fetch): credential managers and SSH may not show a
    /// window; missing or refused credentials give an <see cref="AuthenticationFailed"/> at once.
    /// </summary>
    Task<GitResult> FetchInBackgroundAsync(string repositoryPath, bool prune, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clones a repository into <see cref="CloneRequest.Directory"/> (<c>git clone</c>), reporting the progress, and
    /// opens it. The destination must be missing or empty; when the clone fails or is canceled, what it wrote is
    /// removed (the destination folder too when the clone created it).
    /// </summary>
    Task<GitResult<RepositoryInfo>> CloneAsync(CloneRequest request, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pulls the upstream of the current branch. Merge conflicts give a <see cref="Conflict"/> (the merge or rebase
    /// stays in progress); diverged branches without a configured strategy give a <see cref="Failure"/> explaining it.
    /// </summary>
    Task<GitResult> PullAsync(string repositoryPath, PullMode mode, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes the current branch to its upstream; without one, to the only remote (or "origin") under the same name,
    /// which becomes the upstream. A rejected push (remote ahead, lease broken) gives a <see cref="Failure"/>.
    /// </summary>
    Task<GitResult> PushAsync(string repositoryPath, PushRequest request, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the working directory to a branch, to a new branch tracking a remote branch, or to a commit (detached
    /// HEAD) with <c>git switch</c>. Uncommitted changes are carried over when they do not conflict; otherwise git
    /// refuses and nothing changes (<see cref="Failure"/> explaining it).
    /// </summary>
    Task<GitResult> CheckoutAsync(string repositoryPath, CheckoutTarget target, CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="ancestor"/> is reachable from <paramref name="descendant"/> (or is it).</summary>
    Task<GitResult<bool>> IsAncestorAsync(string repositoryPath, GitSha ancestor, GitSha descendant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges <paramref name="source"/> (canonical ref name or full SHA) into the checked-out branch (<c>git merge
    /// --no-edit</c>): fast-forward or merge commit as the user's <c>merge.ff</c> says. Conflicts give a
    /// <see cref="Conflict"/> listing every unmerged path; the merge stays in progress (<see cref="WorkingTreeStatus.Operation"/>).
    /// </summary>
    Task<GitResult<MergeOutcome>> MergeAsync(string repositoryPath, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the local branch <paramref name="branch"/> forward to <paramref name="target"/> without checking it out
    /// (<c>git fetch . target:refs/heads/branch</c>). Refused when it is not a fast-forward or when the branch is checked
    /// out in a worktree.
    /// </summary>
    Task<GitResult> FastForwardBranchAsync(string repositoryPath, string branch, GitSha target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays the commits of <paramref name="branch"/> (short name; null: the checked-out branch, otherwise git switches
    /// to it first) on <paramref name="upstream"/> (canonical ref name or full SHA). Conflicts give a
    /// <see cref="Conflict"/>; the rebase stays in progress (<see cref="WorkingTreeStatus.Operation"/>).
    /// </summary>
    Task<GitResult<RebaseOutcome>> RebaseAsync(string repositoryPath, string upstream, string? branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits an interactive rebase of HEAD on <paramref name="upstream"/> (canonical ref name or full SHA) would
    /// replay, oldest first: those of HEAD not in <paramref name="upstream"/>, without the ones whose changes are already
    /// there (as <c>git rebase</c> leaves them out). Merge commits are listed (<see cref="RebaseCommit.IsMerge"/>).
    /// </summary>
    Task<GitResult<IReadOnlyList<RebaseCommit>>> GetRebaseCommitsAsync(string repositoryPath, string upstream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Interactive rebase of HEAD on <paramref name="upstream"/> (<c>git rebase -i</c>) following <paramref name="todo"/>
    /// (application order; every commit of <see cref="GetRebaseCommitsAsync"/>, dropped ones included). A reword applies
    /// its message without opening an editor; squashed messages are combined as git prepares them. Conflicts give a
    /// <see cref="Conflict"/>, an <see cref="RebaseAction.Edit"/> step <see cref="RebaseOutcome.Stopped"/>: the rebase stays
    /// in progress (<see cref="WorkingTreeStatus.Operation"/>).
    /// </summary>
    Task<GitResult<RebaseOutcome>> InteractiveRebaseAsync(
        string repositoryPath, string upstream, IReadOnlyList<RebaseTodoItem> todo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Goes on with a stopped operation once its conflicts are resolved and staged (<c>git rebase --continue</c>,
    /// <c>cherry-pick --continue</c>…): the prepared message is kept as is. Stopping again on conflicts gives a
    /// <see cref="Conflict"/>.
    /// </summary>
    Task<GitResult> ContinueOperationAsync(string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the commit a rebase, cherry-pick or revert stopped on and goes on (<c>git rebase --skip</c>…). A merge
    /// cannot be skipped (<see cref="Failure"/>).
    /// </summary>
    Task<GitResult> SkipOperationAsync(string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the changes of <paramref name="commit"/> on HEAD as a new commit (<c>git cherry-pick</c>; a merge commit
    /// against its first parent) and returns the new HEAD. Conflicts give a <see cref="Conflict"/>; the cherry-pick stays
    /// in progress. A commit whose changes are already there gives a <see cref="Failure"/> and changes nothing.
    /// </summary>
    Task<GitResult<GitSha>> CherryPickAsync(string repositoryPath, GitSha commit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a commit undoing <paramref name="commit"/> (<c>git revert --no-edit</c>; a merge commit against its first
    /// parent) and returns the new HEAD. Conflicts give a <see cref="Conflict"/>; the revert stays in progress.
    /// </summary>
    Task<GitResult<GitSha>> RevertAsync(string repositoryPath, GitSha commit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the checked-out branch (or the detached HEAD) to <paramref name="target"/> (<c>git reset</c>); what happens to
    /// the index and the working directory depends on <paramref name="mode"/>. The commits left behind stay in the reflog.
    /// </summary>
    Task<GitResult> ResetAsync(string repositoryPath, GitSha target, ResetMode mode, CancellationToken cancellationToken = default);

    /// <summary>HEAD, the local branches (with their upstream) and the tags, annotated tags not peeled (Undo / Redo).</summary>
    Task<GitResult<RefSnapshot>> GetRefSnapshotAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts HEAD, the local branches and the tags of <paramref name="current"/> back as in <paramref name="target"/>
    /// (Undo / Redo): checkout when HEAD changes branch, <c>git reset</c> (<paramref name="headMode"/>; <see cref="ResetMode.Hard"/>
    /// is not accepted) when the checked-out commit changes, a verified <c>git update-ref</c> transaction for the other
    /// references, branch renaming and upstream settings restored. Refs absent from both snapshots are not touched.
    /// Local changes in the way: <see cref="Failure"/> with <see cref="FailureReason.LocalChangesWouldBeOverwritten"/>.
    /// </summary>
    Task<GitResult> RestoreRefsAsync(
        string repositoryPath, RefSnapshot current, RefSnapshot target, ResetMode headMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unmerged file for the conflict editor: index stages read with <c>git ls-files -u</c>, text merged again in memory
    /// with <c>git merge-file --zdiff3 --object-id</c> (independent of the markers left in the working tree). A file
    /// missing on one side, or whose content git cannot merge (binary, link), comes without segments.
    /// </summary>
    Task<GitResult<ConflictFile>> GetConflictAsync(string repositoryPath, StatusEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a conflicted file (writes the content, takes one side's version or deletes the file) and stages it,
    /// which marks the conflict as resolved.
    /// </summary>
    Task<GitResult> ResolveConflictAsync(string repositoryPath, string path, ConflictResolution resolution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a conflicted file in an external merge tool (<c>git mergetool --no-prompt</c>) and waits for it to close;
    /// git stages the file when the tool reports a successful merge. <paramref name="tool"/>: a tool git knows
    /// (<c>bc</c>, <c>kdiff3</c>, <c>vscode</c>…); null for the one of the configuration (<c>merge.tool</c>).
    /// </summary>
    Task<GitResult> RunMergeToolAsync(string repositoryPath, string path, string? tool, CancellationToken cancellationToken = default);

    /// <summary>Number of commits reachable from <paramref name="until"/> and not from <paramref name="since"/> (<c>git rev-list --count since..until</c>).</summary>
    Task<GitResult<int>> CountCommitsAsync(string repositoryPath, GitSha since, GitSha until, CancellationToken cancellationToken = default);

    /// <summary>
    /// Abandons the operation in progress and puts the branch and the working directory back as they were before it
    /// (<c>git merge --abort</c>, <c>rebase --abort</c>…). Nothing in progress any more: <see cref="Done"/>.
    /// </summary>
    Task<GitResult> AbortOperationAsync(string repositoryPath, RepositoryOperationKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts the changes of the working directory and the index aside (<c>git stash push</c>) and returns the new stash
    /// commit (<c>stash@{0}</c>). Nothing to stash: <see cref="Failure"/>.
    /// </summary>
    Task<GitResult<GitSha>> StashAsync(string repositoryPath, StashRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the stash whose commit is <paramref name="stash"/> to the working directory (<c>git stash apply</c>),
    /// then drops it when <paramref name="drop"/> (<c>pop</c>). Conflicts give a <see cref="Conflict"/> listing the
    /// unmerged paths; the stash is then kept. Refused when local changes would be overwritten (nothing changes).
    /// </summary>
    Task<GitResult> ApplyStashAsync(string repositoryPath, GitSha stash, bool drop, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies only the changes the stash made to <paramref name="paths"/> (J7.16), merged with the local changes of those files
    /// (a three-way merge, as <c>git stash apply</c> does); the stash stays. Conflicts give a <see cref="Conflict"/> listing the
    /// unmerged paths, left with markers and unmerged in the index. Untracked files of the stash are not covered.
    /// </summary>
    Task<GitResult> ApplyStashFilesAsync(string repositoryPath, GitSha stash, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Deletes the stash whose commit is <paramref name="stash"/> (<c>git stash drop</c>). Irreversible.</summary>
    Task<GitResult> DropStashAsync(string repositoryPath, GitSha stash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a local branch without upstream (<c>git branch --no-track</c>), or creates it and switches to it in one
    /// step (<c>git switch --create</c>: when the switch is refused, the branch is not created either).
    /// </summary>
    Task<GitResult> CreateBranchAsync(string repositoryPath, CreateBranchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Renames a local branch (<c>git branch -m</c>); its upstream and reflog follow.</summary>
    Task<GitResult> RenameBranchAsync(string repositoryPath, string name, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a local branch even when it is not merged (<c>git branch -D</c>: the caller has warned the user with
    /// <see cref="CountExclusiveCommitsAsync"/>). The checked-out branch cannot be deleted.
    /// </summary>
    Task<GitResult> DeleteBranchAsync(string repositoryPath, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Number of commits reachable from <paramref name="canonicalNames"/> and from no other branch, remote branch or
    /// tag: the commits that deleting these references would leave reachable only through the reflog.
    /// </summary>
    Task<GitResult<int>> CountExclusiveCommitsAsync(string repositoryPath, IReadOnlyList<string> canonicalNames, CancellationToken cancellationToken = default);

    /// <summary>Creates a lightweight tag, or an annotated one (message on stdin) when the request has a message.</summary>
    Task<GitResult> CreateTagAsync(string repositoryPath, CreateTagRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a local tag (<c>git tag -d</c>); the remotes keep theirs.</summary>
    Task<GitResult> DeleteTagAsync(string repositoryPath, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pushes or deletes one reference of a remote (<see cref="RefPush"/>), reporting the progress. Credentials as for
    /// <see cref="PushAsync"/>; a rejected update gives a <see cref="Failure"/> explaining it.
    /// </summary>
    Task<GitResult> PushRefAsync(string repositoryPath, RefPush push, IProgress<GitProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>Adds a remote (<c>git remote add</c>, nothing fetched).</summary>
    Task<GitResult> AddRemoteAsync(string repositoryPath, string name, string url, CancellationToken cancellationToken = default);

    /// <summary>Renames a remote and its remote branches (<c>git remote rename</c>).</summary>
    Task<GitResult> RenameRemoteAsync(string repositoryPath, string name, string newName, CancellationToken cancellationToken = default);

    /// <summary>Removes a remote, its remote branches and its configuration (<c>git remote remove</c>); the server is not touched.</summary>
    Task<GitResult> RemoveRemoteAsync(string repositoryPath, string name, CancellationToken cancellationToken = default);

    /// <summary>Configured remotes with their fetch and push URLs (<c>git remote -v</c>).</summary>
    Task<GitResult<IReadOnlyList<GitRemote>>> GetRemotesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Replaces the URL of a remote (<c>git remote set-url</c>; a separate push URL is left as is).</summary>
    Task<GitResult> SetRemoteUrlAsync(string repositoryPath, string remoteName, string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands a username / secret (personal access token) for an HTTP(S) remote to the configured credential helpers
    /// (<c>git credential approve</c>, on stdin: never on a command line nor in a log). Refuses values containing line
    /// breaks or NUL (credential protocol injection) and fails when no helper is configured. Without a repository
    /// (clone), only the global and system configuration apply.
    /// </summary>
    Task<GitResult> StoreCredentialAsync(
        string? repositoryPath, RemoteUrl url, string userName, string secret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Default branch of the remote the current branch tracks (J7.11): the remote's <c>HEAD</c> (<c>refs/remotes/&lt;remote&gt;/HEAD</c>),
    /// else <c>&lt;remote&gt;/main</c>, else <c>&lt;remote&gt;/master</c>; the remote is the upstream's, <c>origin</c> without one. A
    /// <see cref="Failure"/> when there is none (detached HEAD, no such branch).
    /// </summary>
    Task<GitResult<ConflictTarget>> GetConflictTargetAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Files that would conflict when merging <paramref name="target"/> into <paramref name="head"/> (<c>git merge-tree
    /// --write-tree</c>, git 2.38+): no change to the working directory, index or refs (unreachable tree objects are written).
    /// Empty when the merge is clean; a <see cref="Failure"/> when git is too old or the histories are unrelated.
    /// </summary>
    Task<GitResult<IReadOnlyList<string>>> PredictConflictsAsync(
        string repositoryPath, GitSha head, GitSha target, CancellationToken cancellationToken = default);

    /// <summary>Paths changed on <paramref name="target"/> since its merge base with <paramref name="head"/> (<c>git diff --name-only</c>).</summary>
    Task<GitResult<IReadOnlyList<string>>> GetChangedFilesSinceMergeBaseAsync(
        string repositoryPath, GitSha head, GitSha target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the commits as one mbox patch file (<c>git format-patch --stdout --binary</c>, J7.12, ADR-079). The commits must
    /// form a linear chain (each one the only child of the previous); a merge commit has no patch. Nothing written on failure.
    /// </summary>
    Task<GitResult> CreatePatchFromCommitsAsync(
        string repositoryPath, IReadOnlyList<GitSha> commits, string patchPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the changes of the working directory as a diff file (<c>git diff --binary</c>), for all files or only
    /// <paramref name="paths"/>. Untracked files are not part of it. No change: <see cref="Failure"/>, nothing written.
    /// </summary>
    Task<GitResult> CreatePatchFromWorkingTreeAsync(
        string repositoryPath, PatchScope scope, IReadOnlyList<string>? paths, string patchPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a patch file: a mailbox (<c>git format-patch</c> output) with <c>git am --3way</c> (creates the commits; stopped
    /// on conflicts it stays in progress as <see cref="RepositoryOperationKind.ApplyMailbox"/>), a plain diff with
    /// <c>git apply --3way</c> (working directory and index). A patch that does not apply gives a <see cref="Failure"/>.
    /// </summary>
    Task<GitResult<PatchApplyOutcome>> ApplyPatchAsync(string repositoryPath, string patchPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Data of the "Résumé" page (J7.13): totals, the five most recently updated local branches, the five latest tags and
    /// commits of HEAD, and the README of the working directory. An empty repository (no commit) gives empty lists; a
    /// bare one has no README.
    /// </summary>
    Task<GitResult<RepositorySummary>> GetSummaryAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Statistics of the history of HEAD for the "Statistiques" page (J7.14): commits per month, contributors (the mailmap applies),
    /// weekday and hour of the authors. The log is read as a stream and aggregated on the fly, without keeping the commits;
    /// <paramref name="progress"/> receives the number of commits read so far. No commit: <see cref="RepositoryStats.Empty"/>.
    /// </summary>
    Task<GitResult<RepositoryStats>> ReadCommitStatsAsync(string repositoryPath, IProgress<int>? progress, CancellationToken cancellationToken = default);

    /// <summary>Working directories of the repository, the main one first (<c>git worktree list --porcelain</c>).</summary>
    Task<GitResult<IReadOnlyList<Worktree>>> GetWorktreesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a linked working directory (<c>git worktree add</c>) on an existing branch, on a new branch or on a detached
    /// commit. A branch already checked out elsewhere, or a directory that is not empty, gives a <see cref="Failure"/>.
    /// </summary>
    Task<GitResult> AddWorktreeAsync(string repositoryPath, AddWorktreeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a linked working directory and its administrative files (<c>git worktree remove</c>). Modifications or
    /// untracked files in it, or a lock, are refused unless <paramref name="force"/>.
    /// </summary>
    Task<GitResult> RemoveWorktreeAsync(string repositoryPath, string worktreePath, bool force, CancellationToken cancellationToken = default);

    /// <summary>Forgets the worktrees whose directory no longer exists (<c>git worktree prune</c>).</summary>
    Task<GitResult> PruneWorktreesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submodules declared by the repository with their state (<c>git submodule status</c> and <c>.gitmodules</c>);
    /// empty without <c>.gitmodules</c> (so for a bare repository).
    /// </summary>
    Task<GitResult<IReadOnlyList<Submodule>>> GetSubmodulesAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clones the missing submodules and checks out the recorded commit (<c>git submodule update --init --recursive</c>)
    /// for <paramref name="path"/>, or for all of them when it is null.
    /// </summary>
    Task<GitResult> UpdateSubmodulesAsync(string repositoryPath, string? path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties a submodule's working directory and unregisters it (<c>git submodule deinit</c>); its recorded commit and
    /// its entry in <c>.gitmodules</c> stay. Local modifications are refused unless <paramref name="force"/>.
    /// </summary>
    Task<GitResult> DeinitSubmoduleAsync(string repositoryPath, string path, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a submodule (<c>git submodule add</c>): clones <paramref name="url"/> into <paramref name="path"/> (relative
    /// to the repository), stages <c>.gitmodules</c> and the new entry. The commit is left to the user.
    /// </summary>
    Task<GitResult> AddSubmoduleAsync(string repositoryPath, string url, string? path, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the repository stores with Git LFS (J7.5): tracked patterns and the LFS files of the checked-out commit.
    /// <see cref="LfsInfo.NotUsed"/> when no attributes file mentions the LFS filter (git is not asked).
    /// </summary>
    Task<GitResult<LfsInfo>> GetLfsAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Routes files matching <paramref name="pattern"/> through LFS (<c>git lfs track</c>): writes <c>.gitattributes</c>;
    /// staging and committing it are left to the user.
    /// </summary>
    Task<GitResult> LfsTrackAsync(string repositoryPath, string pattern, CancellationToken cancellationToken = default);

    /// <summary>Stops routing <paramref name="pattern"/> through LFS (<c>git lfs untrack</c>); files already stored keep their pointers.</summary>
    Task<GitResult> LfsUntrackAsync(string repositoryPath, string pattern, CancellationToken cancellationToken = default);

    /// <summary>Downloads the missing LFS objects of the checked-out commit and puts them in the working directory (<c>git lfs pull</c>).</summary>
    Task<GitResult> LfsPullAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Git Flow settings of the repository (J7.8, ADR-069: <c>gitflow.*</c> keys of the repository config); null when it was
    /// not initialized for Git Flow (no production and development branch configured).
    /// </summary>
    Task<GitResult<GitFlowConfig?>> GetGitFlowAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Value of <c>gui.encoding</c> in the repository's own config (J7.29); null when not set.</summary>
    Task<GitResult<string?>> GetEncodingAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<string?>>(new Success<string?>(null));

    /// <summary>Sets <c>gui.encoding</c> in the repository's own config, or removes it when <paramref name="encoding"/> is null.</summary>
    Task<GitResult> SetEncodingAsync(string repositoryPath, string? encoding, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult>(Done.Instance);

    /// <summary>Hook folder, hooks of the effective folder and the "skip hooks" switch of the repository (J7.31).</summary>
    Task<GitResult<RepositoryHooks>> GetHooksAsync(string repositoryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult<RepositoryHooks>>(new Success<RepositoryHooks>(new RepositoryHooks(null, [], false)));

    /// <summary>Sets <c>core.hooksPath</c> in the local config; null or empty removes it (J7.31).</summary>
    Task<GitResult> SetHooksPathAsync(string repositoryPath, string? path, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult>(Done.Instance);

    /// <summary>Disables (renames to <c>.disabled</c>) or enables a hook of the effective folder; refuses when both files exist (J7.31).</summary>
    Task<GitResult> SetHookEnabledAsync(string repositoryPath, string name, bool enabled, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult>(Done.Instance);

    /// <summary>Sets <c>vigo.skiphooks</c> in the local config (J7.31).</summary>
    Task<GitResult> SetSkipHooksAsync(string repositoryPath, bool skip, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult>(Done.Instance);

    /// <summary>Why <paramref name="config"/> cannot be saved as it is: invalid, or a production / development branch that does not exist; null when it can (J7.30).</summary>
    Task<string?> GitFlowProblemAsync(string repositoryPath, GitFlowConfig config, CancellationToken cancellationToken = default) =>
        Task.FromResult(config.Validate());

    /// <summary>Rewrites the <c>gitflow.*</c> keys (J7.30). Unlike the initialization, never creates a branch: a missing one is refused.</summary>
    Task<GitResult> SetGitFlowAsync(string repositoryPath, GitFlowConfig config, CancellationToken cancellationToken = default) =>
        Task.FromResult<GitResult>(Done.Instance);

    /// <summary>
    /// Initializes Git Flow: creates the development branch on the production branch when it is missing and writes the
    /// <c>gitflow.*</c> keys. The production branch must exist (a first commit).
    /// </summary>
    Task<GitResult> GitFlowInitAsync(string repositoryPath, GitFlowConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a feature, release or hotfix: a new branch named with the kind's prefix, created on the development branch
    /// (production branch for a hotfix) and checked out (<c>git switch --create</c>: nothing is created when git refuses).
    /// </summary>
    Task<GitResult> GitFlowStartAsync(string repositoryPath, GitFlowConfig config, GitFlowStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finishes a Git Flow branch with merge commits (<c>--no-ff</c>): a feature into the development branch; a release or a
    /// hotfix into the production branch, tagged, then into the development branch. The branch is then deleted unless the
    /// request keeps it. A conflict stops there (<see cref="Conflict"/>, the merge in progress): the steps that follow are
    /// left to the user.
    /// </summary>
    Task<GitResult<GitFlowFinished>> GitFlowFinishAsync(string repositoryPath, GitFlowConfig config, GitFlowFinishRequest request, CancellationToken cancellationToken = default);

    /// <summary>Effective value of a config key (repository, global, system); null when it is not set.
    Task<GitResult<string?>> GetConfigValueAsync(string? repositoryPath, string key, CancellationToken cancellationToken = default);
}

/// <summary>Where <see cref="IGitService.ApplyPatchAsync"/> applies a patch.</summary>
public enum PatchTarget
{
    Index,
    WorkingTree,
}
