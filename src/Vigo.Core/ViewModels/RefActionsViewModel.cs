using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Branch, tag and remote management of a repository tab (J4.2): create a branch or a tag on a commit, rename and
/// delete branches, delete tags, push or delete them on a remote, add / edit / remove remotes. Used by the left panel
/// menus, the graph menu and the toolbar.
/// </summary>
/// <remarks>
/// Local operations go through <see cref="StagingActions"/> (one at a time with the index operations, toast on
/// failure); remote ones through <see cref="RemoteActionsViewModel"/> (progress card, authentication dialog). The refs
/// are read again after each one.
/// </remarks>
public sealed partial class RefActionsViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly StagingActions _actions;
    private readonly RemoteActionsViewModel _remote;
    private readonly IInputDialogService? _inputs;
    private readonly IUserNotifier? _notifier;
    private readonly bool _hasWorkingTree;
    private readonly CancellationToken _lifetime;
    private readonly Preferences? _preferences;
    private bool _checkoutNewBranch = true;

    public RefActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, StagingActions actions, RemoteActionsViewModel remote,
        bool hasWorkingTree, IInputDialogService? inputs, IUserNotifier? notifier, CancellationToken lifetime,
        Preferences? preferences = null)
    {
        _preferences = preferences;
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _actions = actions;
        _remote = remote;
        _hasWorkingTree = hasWorkingTree;
        _inputs = inputs;
        _notifier = notifier;
        _lifetime = lifetime;
    }

    /// <summary>
    /// "Extraire après création" of the new branch dialog: checked by default, the last choice is kept in the
    /// preferences (in this instance only, without them).
    /// </summary>
    internal bool CheckoutNewBranch
    {
        get => _preferences?.Current.CheckoutNewBranch ?? _checkoutNewBranch;
        set
        {
            _checkoutNewBranch = value;
            if (_preferences is not null) _ = _preferences.UpdateAsync(p => p with { CheckoutNewBranch = value });
        }
    }

    /// <summary>Configured remotes (<c>git remote -v</c>), read at opening and after each change.</summary>
    public IReadOnlyList<GitRemote> Remotes { get; private set; } = [];

    /// <summary>The list of remotes or their URLs changed.</summary>
    public event EventHandler? RemotesChanged;

    /// <summary>The name dialogs can be shown (not in tests or headless).</summary>
    public bool CanPrompt => _inputs is not null;

    /// <summary>Remote used when none is implied: "origin", or the only one.</summary>
    public string? DefaultRemote =>
        Remotes.Any(r => r.Name == "origin") ? "origin" : Remotes.Count == 1 ? Remotes[0].Name : null;

    private RepositoryRefs? Refs => _graph.Refs;

    public async Task LoadRemotesAsync()
    {
        var result = await _git.GetRemotesAsync(_repositoryPath, _lifetime);
        if (_lifetime.IsCancellationRequested || result.Value is not Success<IReadOnlyList<GitRemote>> remotes) return;
        Remotes = remotes.Value;
        RemotesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Branches -------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Nouvelle branche ici…" (Ctrl+B): on <paramref name="at"/>, or on the selected commit, or on HEAD. The dialog
    /// offers to switch to it at once (checked by default).
    /// </summary>
    [RelayCommand]
    private async Task CreateBranchAsync(GitSha? at)
    {
        if (_inputs is null || TargetOf(at) is not { } target) return;
        var locals = Names(GitRefKind.LocalBranch);
        var dialog = new InputDialogViewModel(Loc.Get("RefActions.NewBranch.Title"), Loc.Get("RefActions.NewBranch.Confirm"), Loc.Get("RefActions.NewBranch.Header"),
            name => RefNames.Check(name) ?? RefNames.CheckConflict(name, locals, RefNameKind.Branch))
        {
            SpacesBecomeDashes = true,
            Description = Describe(target),
            NamePlaceholder = Loc.Get("RefActions.NewBranch.Placeholder"),
            OptionText = _hasWorkingTree ? Loc.Get("RefActions.NewBranch.Checkout") : null,
            OptionChecked = _hasWorkingTree && CheckoutNewBranch,
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;
        if (_hasWorkingTree) CheckoutNewBranch = dialog.OptionChecked;

        var request = new CreateBranchRequest(dialog.Value, target, dialog.OptionChecked);
        await RunLocalAsync(Loc.Format("RefActions.NewBranch.Failed", request.Name), ct => _git.CreateBranchAsync(_repositoryPath, request, ct),
            new UndoStep(Loc.Format("RefActions.NewBranch.Undo", request.Name)));
    }

    /// <summary>"Renommer…" (F2) of a local branch; its upstream is kept.</summary>
    [RelayCommand]
    private async Task RenameBranchAsync(GitRef branch)
    {
        if (_inputs is null || branch.Kind != GitRefKind.LocalBranch) return;
        var others = Names(GitRefKind.LocalBranch).Where(n => n != branch.Name).ToList();
        var dialog = new InputDialogViewModel(Loc.Format("RefActions.Rename.Title", branch.Name), Loc.Get("RefActions.Rename.Confirm"), Loc.Get("RefActions.Rename.Header"),
            name => name == branch.Name ? string.Empty : RefNames.Check(name) ?? RefNames.CheckConflict(name, others, RefNameKind.Branch),
            branch.Name)
        {
            SpacesBecomeDashes = true,
            Description = branch.Upstream is { } upstream ? Loc.Format("RefActions.Rename.Upstream", upstream) : string.Empty,
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;
        await RunLocalAsync(Loc.Format("RefActions.Rename.Failed", branch.Name), ct => _git.RenameBranchAsync(_repositoryPath, branch.Name, dialog.Value, ct),
            new UndoStep(Loc.Format("RefActions.Rename.Undo", branch.Name, dialog.Value)));
    }

    /// <summary>
    /// "Supprimer…" (Suppr) of a local branch (not the checked-out one): says how many commits would be left without a
    /// branch, offers to delete its remote branch too (mockup), then deletes.
    /// </summary>
    [RelayCommand]
    private async Task DeleteBranchAsync(GitRef branch)
    {
        if (branch.Kind != GitRefKind.LocalBranch || branch.IsCurrent || _actions.IsBusy) return;
        var upstream = branch.Upstream is { } name ? Refs?.OfKind(GitRefKind.RemoteBranch).FirstOrDefault(r => r.Name == name) : null;

        var exclusive = await CountExclusiveAsync([branch.CanonicalName]);
        var withUpstream = upstream is null ? exclusive : await CountExclusiveAsync([branch.CanonicalName, upstream.CanonicalName]);
        var message = (exclusive, withUpstream) switch
        {
            (1, _) => Loc.Format("RefActions.DeleteBranch.ExclusiveOne", branch.Name),
            (> 0, _) => Loc.Format("RefActions.DeleteBranch.ExclusiveMany", exclusive, branch.Name),
            (0, 1) => Loc.Format("RefActions.DeleteBranch.WithUpstreamOne", upstream!.Name),
            (0, > 0) => Loc.Format("RefActions.DeleteBranch.WithUpstreamMany", upstream!.Name, withUpstream),
            (0, _) => Loc.Get("RefActions.DeleteBranch.Shared"),
            _ => Loc.Get("RefActions.DeleteBranch.Unknown"),
        };

        var answer = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("RefActions.DeleteBranch.Title", branch.Name), message, Loc.Get("RefActions.Common.Delete"),
            OptionText: upstream is null ? null : Loc.Format("RefActions.DeleteBranch.AlsoUpstream", upstream.Name), IsDestructive: true));
        if (!answer.Confirmed) return;

        if (!await RunLocalAsync(Loc.Format("RefActions.DeleteBranch.Failed", branch.Name), ct => _git.DeleteBranchAsync(_repositoryPath, branch.Name, ct),
                new UndoStep(Loc.Format("RefActions.DeleteBranch.Undo", branch.Name)))) return;
        _notifier?.Notify(NotificationSeverity.Info, Loc.Format("RefActions.DeleteBranch.Done", branch.Name),
            Loc.Format("RefActions.Common.PointedAt", branch.Target.ToShortString()));
        if (answer.OptionChecked && upstream is not null) await DeleteOnRemoteAsync(upstream);
    }

    /// <summary>"Supprimer du distant…" of a remote branch (<c>git push --delete</c>), after confirmation.</summary>
    [RelayCommand]
    private async Task DeleteRemoteBranchAsync(GitRef remoteBranch)
    {
        if (remoteBranch is not { Kind: GitRefKind.RemoteBranch, Remote: { } remote }) return;
        var exclusive = await CountExclusiveAsync([remoteBranch.CanonicalName]);
        var message = Loc.Format("RefActions.DeleteRemoteBranch.Message", RemoteBranchName(remoteBranch), remote);
        if (exclusive == 1) message += " " + Loc.Get("RefActions.DeleteRemoteBranch.OnlyHereOne");
        else if (exclusive > 0) message += " " + Loc.Format("RefActions.DeleteRemoteBranch.OnlyHereMany", exclusive);

        var answer = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("RefActions.DeleteRemoteBranch.Title", remoteBranch.Name), message,
            Loc.Get("RefActions.Common.Delete"), IsDestructive: true));
        if (answer.Confirmed) await DeleteOnRemoteAsync(remoteBranch);
    }

    /// <summary>Text of the push entry of a local branch's menu; null when there is nowhere to push it.</summary>
    public string? PushBranchLabel(GitRef branch) => PushOf(branch) switch
    {
        { SetUpstream: true } push => Loc.Format("RefActions.PushBranch.Publish", branch.Name, push.Remote),
        { } => Loc.Format("RefActions.PushBranch.Push", branch.Name, branch.Upstream),
        null => null,
    };

    /// <summary>
    /// Pushes a local branch to its upstream, or publishes it on the default remote under the same name (it then
    /// tracks it). The checked-out branch goes through the toolbar push (same rules, ahead / behind toasts).
    /// </summary>
    [RelayCommand]
    private async Task PushBranchAsync(GitRef branch)
    {
        if (branch.IsCurrent)
        {
            if (_remote.PushCommand.CanExecute(null)) await _remote.PushCommand.ExecuteAsync(null);
            return;
        }
        if (PushOf(branch) is not { } push) return;
        var target = $"{push.Remote}/{push.Destination["refs/heads/".Length..]}";
        await _remote.PushRefAsync(push,
            push.SetUpstream ? Loc.Format("RefActions.PushBranch.PublishTitle", branch.Name) : Loc.Format("RefActions.PushBranch.PushTitle", branch.Name, target),
            Loc.Get("RefActions.PushBranch.Failed"), Loc.Get("RefActions.PushBranch.Done"),
            push.SetUpstream ? Loc.Format("RefActions.PushBranch.Published", branch.Name, target) : Loc.Format("RefActions.PushBranch.UpToDate", target));
    }

    private RefPush? PushOf(GitRef branch)
    {
        if (branch.Kind != GitRefKind.LocalBranch) return null;
        if (branch.Upstream is { } upstream
            && Refs?.OfKind(GitRefKind.RemoteBranch).FirstOrDefault(r => r.Name == upstream) is { Remote: { } remote } tracked)
        {
            return new RefPush(remote, branch.CanonicalName, "refs/heads/" + RemoteBranchName(tracked));
        }
        // An upstream that is gone (deleted on the server and pruned) is published again under its own name.
        return DefaultRemote is { } fallback ? new RefPush(fallback, branch.CanonicalName, branch.CanonicalName, SetUpstream: true) : null;
    }

    // ---- Tags ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Nouveau tag ici…" (Ctrl+T): on <paramref name="at"/>, the selected commit or HEAD. A message makes it annotated;
    /// it can be pushed to the default remote right away.
    /// </summary>
    [RelayCommand]
    private async Task CreateTagAsync(GitSha? at)
    {
        if (_inputs is null || TargetOf(at) is not { } target) return;
        var tags = Names(GitRefKind.Tag);
        var remote = DefaultRemote;
        var dialog = new InputDialogViewModel(Loc.Get("RefActions.NewTag.Title"), Loc.Get("RefActions.NewTag.Confirm"), Loc.Get("RefActions.NewTag.Header"),
            name => RefNames.Check(name) ?? RefNames.CheckConflict(name, tags, RefNameKind.Tag))
        {
            SpacesBecomeDashes = true,
            Description = Describe(target),
            NamePlaceholder = "v1.0.0",
            SecondHeader = Loc.Get("RefActions.NewTag.MessageHeader"),
            SecondPlaceholder = Loc.Get("RefActions.NewTag.MessagePlaceholder"),
            IsSecondMultiline = true,
            OptionText = remote is null ? null : Loc.Format("RefActions.NewTag.Push", remote),
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        var request = new CreateTagRequest(dialog.Value, target, dialog.SecondValue.Length > 0 ? dialog.SecondText : null);
        if (!await RunLocalAsync(Loc.Format("RefActions.NewTag.Failed", request.Name), ct => _git.CreateTagAsync(_repositoryPath, request, ct),
                new UndoStep(Loc.Format("RefActions.NewTag.Undo", request.Name)))) return;
        if (dialog.OptionChecked && remote is not null) await PushTagToAsync(request.Name, remote);
    }

    /// <summary>Text of the push entry of a tag's menu; null without remote.</summary>
    public string? PushTagLabel(GitRef tag) => DefaultRemote is { } remote ? Loc.Format("RefActions.PushBranch.Push", tag.Name, remote) : null;

    [RelayCommand]
    private async Task PushTagAsync(GitRef tag)
    {
        if (tag.Kind == GitRefKind.Tag && DefaultRemote is { } remote) await PushTagToAsync(tag.Name, remote);
    }

    private Task<bool> PushTagToAsync(string name, string remote) =>
        _remote.PushRefAsync(new RefPush(remote, "refs/tags/" + name, "refs/tags/" + name), Loc.Format("RefActions.PushTag.Title", name, remote),
            Loc.Get("RefActions.PushTag.Failed"), Loc.Get("RefActions.PushTag.Done"), Loc.Format("RefActions.PushTag.Detail", name, remote));

    /// <summary>"Supprimer…" of a tag; it can be deleted on the default remote too (unchecked: others may use it).</summary>
    [RelayCommand]
    private async Task DeleteTagAsync(GitRef tag)
    {
        if (tag.Kind != GitRefKind.Tag || _actions.IsBusy) return;
        var remote = DefaultRemote;
        var exclusive = await CountExclusiveAsync([tag.CanonicalName]);
        var message = exclusive switch
        {
            1 => Loc.Get("RefActions.DeleteTag.ExclusiveOne"),
            > 0 => Loc.Format("RefActions.DeleteTag.ExclusiveMany", exclusive),
            _ => Loc.Get("RefActions.DeleteTag.Shared"),
        };

        var answer = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("RefActions.DeleteTag.Title", tag.Name), message, Loc.Get("RefActions.Common.Delete"),
            OptionText: remote is null ? null : Loc.Format("RefActions.DeleteTag.AlsoRemote", remote), IsDestructive: true));
        if (!answer.Confirmed) return;

        if (!await RunLocalAsync(Loc.Format("RefActions.DeleteTag.Failed", tag.Name), ct => _git.DeleteTagAsync(_repositoryPath, tag.Name, ct),
                new UndoStep(Loc.Format("RefActions.DeleteTag.Undo", tag.Name)))) return;
        _notifier?.Notify(NotificationSeverity.Info, Loc.Format("RefActions.DeleteTag.Done", tag.Name),
            Loc.Format("RefActions.Common.PointedAt", tag.Target.ToShortString()));
        if (answer.OptionChecked && remote is not null)
        {
            await _remote.PushRefAsync(new RefPush(remote, null, tag.CanonicalName), Loc.Format("RefActions.DeleteTag.RemoteTitle", tag.Name, remote),
                Loc.Get("RefActions.DeleteTag.RemoteFailed"), Loc.Get("RefActions.DeleteTag.RemoteDone"), Loc.Format("RefActions.DeleteTag.RemoteDetail", tag.Name, remote));
        }
    }

    // ---- Remotes ---------------------------------------------------------------------------------------------------

    /// <summary>"Ajouter un distant…": name and URL, then a fetch of its branches (checked by default).</summary>
    [RelayCommand]
    private async Task AddRemoteAsync()
    {
        if (_inputs is null) return;
        var names = Remotes.Select(r => r.Name).ToList();
        var dialog = new InputDialogViewModel(Loc.Get("RefActions.AddRemote.Title"), Loc.Get("RefActions.AddRemote.Confirm"), Loc.Get("RefActions.Common.NameHeader"),
            name => RefNames.Check(name) ?? (names.Contains(name) ? Loc.Format("RefActions.Common.RemoteExists", name) : null),
            names.Count == 0 ? "origin" : string.Empty, CheckUrl)
        {
            SpacesBecomeDashes = true,
            NamePlaceholder = "upstream",
            SecondHeader = Loc.Get("RefActions.Common.UrlHeader"),
            SecondPlaceholder = Loc.Get("RefActions.AddRemote.UrlPlaceholder"),
            OptionText = Loc.Get("RefActions.AddRemote.Fetch"),
            OptionChecked = true,
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        var added = await RunLocalAsync(Loc.Format("RefActions.AddRemote.Failed", dialog.Value),
            ct => _git.AddRemoteAsync(_repositoryPath, dialog.Value, dialog.SecondValue, ct));
        await LoadRemotesAsync();
        if (added && dialog.OptionChecked && _remote.FetchCommand.CanExecute(null)) await _remote.FetchCommand.ExecuteAsync(null);
    }

    /// <summary>"Modifier…" of a remote: rename it (its remote branches follow) and / or change its URL.</summary>
    [RelayCommand]
    private async Task EditRemoteAsync(string name)
    {
        if (_inputs is null || Remotes.FirstOrDefault(r => r.Name == name) is not { } remote) return;
        var others = Remotes.Select(r => r.Name).Where(n => n != name).ToList();
        var dialog = new InputDialogViewModel(Loc.Format("RefActions.EditRemote.Title", name), Loc.Get("RefActions.EditRemote.Confirm"), Loc.Get("RefActions.Common.NameHeader"),
            value => value == name ? null : RefNames.Check(value) ?? (others.Contains(value) ? Loc.Format("RefActions.Common.RemoteExists", value) : null),
            name, CheckUrl)
        {
            SpacesBecomeDashes = true,
            SecondHeader = Loc.Get("RefActions.Common.UrlHeader"),
            SecondText = remote.FetchUrl,
            Description = remote.PushUrl is { } push ? Loc.Format("RefActions.EditRemote.PushUrl", push) : string.Empty,
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        var newName = dialog.Value;
        if (newName != name
            && !await RunLocalAsync(Loc.Format("RefActions.EditRemote.RenameFailed", name), ct => _git.RenameRemoteAsync(_repositoryPath, name, newName, ct)))
        {
            return;
        }
        if (dialog.SecondValue != remote.FetchUrl)
        {
            await RunLocalAsync(Loc.Format("RefActions.EditRemote.UrlFailed", newName), ct => _git.SetRemoteUrlAsync(_repositoryPath, newName, dialog.SecondValue, ct));
        }
        await LoadRemotesAsync();
    }

    /// <summary>"Supprimer le distant…": its remote branches disappear locally; nothing changes on the server.</summary>
    [RelayCommand]
    private async Task RemoveRemoteAsync(string name)
    {
        if (_actions.IsBusy) return;
        var url = Remotes.FirstOrDefault(r => r.Name == name)?.FetchUrl;
        var count = Refs?.OfKind(GitRefKind.RemoteBranch).Count(r => r.Remote == name) ?? 0;
        var message = Loc.Format("RefActions.RemoveRemote.Message",
            count == 1 ? Loc.Get("RefActions.RemoveRemote.BranchesOne") : Loc.Format("RefActions.RemoveRemote.BranchesMany", count),
            url is null ? string.Empty : Loc.Format("RefActions.RemoveRemote.Url", url));
        var answer = await _actions.ConfirmAsync(new ConfirmationRequest(Loc.Format("RefActions.RemoveRemote.Title", name), message,
            Loc.Get("RefActions.Common.Delete"), IsDestructive: true));
        if (!answer.Confirmed) return;

        await RunLocalAsync(Loc.Format("RefActions.RemoveRemote.Failed", name), ct => _git.RemoveRemoteAsync(_repositoryPath, name, ct));
        await LoadRemotesAsync();
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static string? CheckUrl(string url) => url.Length == 0
        ? Loc.Get("RefActions.Url.Required")
        : url.StartsWith('-') ? Loc.Get("RefActions.Url.StartsWithDash") : null;

    /// <summary>Commit a new ref goes on: the one given, the selected one, HEAD; null (with a toast) in an empty repository.</summary>
    private GitSha? TargetOf(GitSha? at)
    {
        if ((at ?? _graph.SelectedSha ?? _graph.Head) is { } target) return target;
        _notifier?.Notify(NotificationSeverity.Warning, Loc.Get("RefActions.NoCommit.Title"), Loc.Get("RefActions.NoCommit.Detail"));
        return null;
    }

    /// <summary>"Depuis a1b2c3d · Fix cache TTL".</summary>
    private string Describe(GitSha target) =>
        _graph.FindCommit(target) is { } commit
            ? Loc.Format("RefActions.FromCommitSubject", target.ToShortString(), commit.Subject)
            : Loc.Format("RefActions.FromCommit", target.ToShortString());

    private List<string> Names(GitRefKind kind) => Refs?.OfKind(kind).Select(r => r.Name).ToList() ?? [];

    /// <summary>"origin/feature/x" → "feature/x".</summary>
    private static string RemoteBranchName(GitRef remoteBranch) =>
        remoteBranch.Remote is { } remote && remoteBranch.Name.StartsWith(remote + "/", StringComparison.Ordinal)
            ? remoteBranch.Name[(remote.Length + 1)..]
            : remoteBranch.Name;

    private Task<bool> DeleteOnRemoteAsync(GitRef remoteBranch)
    {
        var remote = remoteBranch.Remote!;
        return _remote.PushRefAsync(new RefPush(remote, null, "refs/heads/" + RemoteBranchName(remoteBranch)),
            Loc.Format("RefActions.DeleteOnRemote.Title", remoteBranch.Name), Loc.Get("RefActions.DeleteOnRemote.Failed"),
            Loc.Get("RefActions.DeleteOnRemote.Done"), Loc.Format("RefActions.DeleteOnRemote.Detail", RemoteBranchName(remoteBranch), remote));
    }

    /// <summary>
    /// The local branch called <paramref name="name"/> when it can go with the source branch of a merged pull request (it exists and is not
    /// checked out), and whether all its commits are also on another branch; null when there is none (J6.15).
    /// </summary>
    internal async Task<(GitRef Branch, bool Merged)?> FindLocalBranchAsync(string name, string remote)
    {
        if (Refs?.OfKind(GitRefKind.LocalBranch).FirstOrDefault(b => b.Name == name) is not { IsCurrent: false } branch) return null;
        var names = new List<string> { branch.CanonicalName };
        if (Refs.OfKind(GitRefKind.RemoteBranch).FirstOrDefault(r => r.Name == $"{remote}/{name}") is { } upstream) names.Add(upstream.CanonicalName);
        return (branch, await CountExclusiveAsync(names) == 0);
    }

    /// <summary>After a source branch was deleted on the service: deletes its local branch when asked, then fetches the remote with pruning (J6.15).</summary>
    internal async Task CleanUpAfterRemoteDeleteAsync(GitRef? local, string remote)
    {
        if (local is not null)
        {
            await RunLocalAsync(Loc.Format("RefActions.DeleteBranch.Failed", local.Name), ct => _git.DeleteBranchAsync(_repositoryPath, local.Name, ct),
                new UndoStep(Loc.Format("RefActions.DeleteBranch.Undo", local.Name)));
        }
        await _remote.FetchRemoteAsync(remote, prune: true);
    }

    /// <summary>Commits only reachable from <paramref name="names"/>; null when git could not tell.</summary>
    private async Task<int?> CountExclusiveAsync(IReadOnlyList<string> names) =>
        (await _git.CountExclusiveCommitsAsync(_repositoryPath, names, _lifetime)).Value is Success<int> s ? s.Value : null;

    /// <summary>Runs a local ref operation, then reads the refs again (badges, panel) without waiting for the watcher.</summary>
    private async Task<bool> RunLocalAsync(string failureTitle, Func<CancellationToken, Task<GitResult>> operation, UndoStep? undo = null)
    {
        var done = await _actions.RunAsync(failureTitle, operation, undo);
        if (!_lifetime.IsCancellationRequested) await _graph.RefreshAsync();
        return done;
    }
}
