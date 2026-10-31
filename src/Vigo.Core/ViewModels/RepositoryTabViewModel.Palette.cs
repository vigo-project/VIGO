using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

// Command palette (J4.6): what a repository tab offers. Texts come from the .resw resources (Palette.*, J8.19).
public sealed partial class RepositoryTabViewModel
{
    // Commits read at most per search: the first rows of the history (the palette is for recent work; the full
    // history search comes with the toolbar search box).
    private const int MaxSearchedCommits = 50_000;

    /// <summary>Commands of the repository that can run now, then its branches and modified files.</summary>
    public IEnumerable<PaletteEntry> GetPaletteEntries()
    {
        static IEnumerable<PaletteEntry> When(bool condition, PaletteEntry entry) => condition ? [entry] : [];

        IEnumerable<PaletteEntry> commands =
        [
            .. When(Undo.CanUndo, Command("arrow_undo", Loc.Format("Palette.Cmd.Undo", Undo.UndoLabel), Undo.UndoCommand, ShortcutMap.Current.Display(ShortcutIds.Undo))),
            .. When(Undo.CanRedo, Command("arrow_redo", Loc.Format("Palette.Cmd.Redo", Undo.RedoLabel), Undo.RedoCommand, ShortcutMap.Current.Display(ShortcutIds.Redo))),
            .. When(Remote.PullCommand.CanExecute(null), Command("arrow_download", Loc.Get("Palette.Cmd.Pull"), Remote.PullCommand, ShortcutMap.Current.Display(ShortcutIds.Pull))),
            .. When(Remote.PushCommand.CanExecute(null), Command("arrow_upload", Loc.Get("Palette.Cmd.Push"), Remote.PushCommand, ShortcutMap.Current.Display(ShortcutIds.Push))),
            .. When(Remote.FetchCommand.CanExecute(null), Command("arrow_sync", Loc.Get("Palette.Cmd.Fetch"), Remote.FetchCommand, ShortcutMap.Current.Display(ShortcutIds.Fetch))),
            .. When(Remote.PullRebaseCommand.CanExecute(null), Command("arrow_download", Loc.Get("Palette.Cmd.RebasePull"), Remote.PullRebaseCommand)),
            .. When(Remote.ForcePushCommand.CanExecute(null), Command("arrow_upload", Loc.Get("Palette.Cmd.ForcePush"), Remote.ForcePushCommand)),
            .. When(RefActions.CreateBranchCommand.CanExecute(null), Command("branch", Loc.Get("Palette.Cmd.NewBranch"), RefActions.CreateBranchCommand, ShortcutMap.Current.Display(ShortcutIds.NewBranch))),
            .. When(RefActions.CreateTagCommand.CanExecute(null), Command("tag", Loc.Get("Palette.Cmd.NewTag"), RefActions.CreateTagCommand)),
            .. When(Stash.StashCommand.CanExecute(null), Command("archive", Loc.Get("Palette.Cmd.StashPush"), Stash.StashCommand, ShortcutMap.Current.Display(ShortcutIds.Stash))),
            .. When(Stash.StashWithMessageCommand.CanExecute(null), Command("archive", Loc.Get("Palette.Cmd.StashMessage"), Stash.StashWithMessageCommand)),
            .. When(Stash.PopLatestCommand.CanExecute(null), Command("archive_arrow_back", Loc.Get("Palette.Cmd.StashPop"), Stash.PopLatestCommand)),
            .. When(WorkingTree.Unstaged.Count > 0, Command("add", Loc.Get("Palette.Cmd.StageAll"), WorkingTree.StageAllCommand)),
            .. When(RefActions.AddRemoteCommand.CanExecute(null), Command("cloud", Loc.Get("Palette.Cmd.AddRemote"), RefActions.AddRemoteCommand)),
            .. When(WorkingTree.IsAvailable && Submodules.CanPrompt, Command("add", Loc.Get("Palette.Cmd.AddSubmodule"), Submodules.AddCommand)),
            .. When(Submodules.HasSubmodules, Command("arrow_sync", Loc.Get("Palette.Cmd.UpdateSubmodules"), Submodules.UpdateAllCommand)),
            .. When(WorkingTree.IsAvailable && Lfs.CanPrompt, Command("document_add", Loc.Get("Palette.Cmd.LfsTrack"), Lfs.TrackCommand)),
            .. When(Lfs.CanPull, Command("arrow_download", Loc.Get("Palette.Cmd.LfsPull"), Lfs.PullCommand)),
            .. PatchEntries(),
            .. TerminalEntries(),
            .. GitFlowEntries(),
            .. PullRequestEntries(),
            .. When(WorkingTree.IsAvailable && !Info.IsEmpty, PickFile("history", Loc.Get("Palette.Cmd.FileHistory"), FileInspectionKind.History)),
            .. When(WorkingTree.IsAvailable && !Info.IsEmpty, PickFile("person", Loc.Get("Palette.Cmd.FileBlame"), FileInspectionKind.Blame)),
        ];
        return commands.Concat(BranchEntries()).Concat(FileEntries());
    }

    /// <summary>Patch files (J7.12): create one from the changes of the working directory, apply one.</summary>
    private IEnumerable<PaletteEntry> PatchEntries()
    {
        if (!Patches.CanPrompt || !WorkingTree.IsAvailable) yield break;
        if (Patches.CanCreateFromWorkingTree)
            yield return new PaletteEntry(PaletteGroup.Commands, "document_text", Loc.Get("Palette.Cmd.PatchCreate"), () => Patches.CreateWorkingTreePatchCommand.ExecuteAsync(PatchScope.All));
        if (Patches.CanApply) yield return Command("document_text", Loc.Get("Palette.Cmd.PatchApply"), Patches.ApplyPatchCommand);
    }

    /// <summary>Integrated terminal (J7.10): show / hide the panel, start a terminal with another shell.</summary>
    private IEnumerable<PaletteEntry> TerminalEntries()
    {
        if (!Terminal.IsAvailable) yield break;
        yield return Command("window_console", Loc.Get("Palette.Cmd.TerminalToggle"), Terminal.ToggleCommand, ShortcutMap.Current.Display(ShortcutIds.Terminal));
        foreach (var shell in Terminal.Shells)
        {
            var chosen = shell;
            yield return new PaletteEntry(PaletteGroup.Commands, "window_console", Loc.Format("Palette.Cmd.TerminalNew", shell.Name), () =>
            {
                Terminal.OpenTab(chosen);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>Git Flow (J7.8): initialize, start a feature / release / hotfix, finish the checked-out Git Flow branch.</summary>
    private IEnumerable<PaletteEntry> GitFlowEntries()
    {
        if (!WorkingTree.IsAvailable || !GitFlow.CanPrompt) yield break;
        if (GitFlow.CanInitialize)
        {
            yield return Command("branch_fork", Loc.Get("Palette.Cmd.GitFlowInit"), GitFlow.InitializeCommand);
            yield break;
        }
        if (!GitFlow.IsInitialized) yield break;

        yield return new PaletteEntry(PaletteGroup.Commands, "branch_fork", Loc.Get("Palette.Cmd.GitFlowFeature"), () => GitFlow.StartCommand.ExecuteAsync(GitFlowKind.Feature));
        yield return new PaletteEntry(PaletteGroup.Commands, "branch_fork", Loc.Get("Palette.Cmd.GitFlowRelease"), () => GitFlow.StartCommand.ExecuteAsync(GitFlowKind.Release));
        yield return new PaletteEntry(PaletteGroup.Commands, "branch_fork", Loc.Get("Palette.Cmd.GitFlowHotfix"), () => GitFlow.StartCommand.ExecuteAsync(GitFlowKind.Hotfix));
        if (GitFlow.CurrentBranch is { } branch)
            yield return new PaletteEntry(PaletteGroup.Commands, "branch_fork", Loc.Format("Palette.Cmd.GitFlowFinish", branch.Name), () => GitFlow.FinishCommand.ExecuteAsync(branch));
    }

    /// <summary>Pull requests (J6.2): create one for the checked-out branch, read the lists again.</summary>
    private IEnumerable<PaletteEntry> PullRequestEntries()
    {
        if (!Hosting.IsConnected) yield break;
        if (Graph.Refs?.OfKind(GitRefKind.LocalBranch).FirstOrDefault(b => b.IsCurrent) is { } branch && Hosting.CanCreatePullRequest(branch))
        {
            yield return new PaletteEntry(PaletteGroup.Commands, "branch_request", Loc.Format("Palette.Cmd.PrCreate", branch.Name),
                () => Hosting.CreatePullRequestCommand.ExecuteAsync(branch));
        }
        yield return Command("arrow_sync", Loc.Get("Palette.Cmd.PrRefresh"), Hosting.RefreshCommand);
    }

    /// <summary>Loaded commits whose subject or SHA match, in history order.</summary>
    public IEnumerable<PaletteEntry> FindCommits(Func<string, bool> matches, int limit)
    {
        var found = 0;
        var rows = Math.Min(Graph.RowCount, MaxSearchedCommits);
        for (var row = 0; row < rows && found < limit; row++)
        {
            if (Graph.IsWorkInProgressRow(row)) continue;
            var commit = Graph.GetCommit(row);
            var sha = commit.Sha.ToShortString();
            if (!matches(commit.Subject) && !matches(sha)) continue;
            found++;
            var target = commit.Sha;
            yield return new PaletteEntry(PaletteGroup.Commits, "circle", commit.Subject, () =>
            {
                Graph.TrySelect(target);
                return Task.CompletedTask;
            }, sha);
        }
    }

    /// <summary>Branches to check out: local ones first (not the current one), then the remote ones.</summary>
    private IEnumerable<PaletteEntry> BranchEntries()
    {
        if (!WorkingTree.IsAvailable || Graph.Refs is not { } refs) yield break;
        foreach (var branch in refs.OfKind(GitRefKind.LocalBranch).Concat(refs.OfKind(GitRefKind.RemoteBranch)))
        {
            if (RefsPanelViewModel.ToCheckoutTarget(branch, refs) is not { } target) continue;
            var detail = target.Value is CheckoutRemoteBranch r ? Loc.Format("Palette.Detail.CheckoutNew", r.LocalName) : Loc.Get("Palette.Detail.Checkout");
            var reference = branch;
            yield return new PaletteEntry(PaletteGroup.Branches, branch.Kind == GitRefKind.RemoteBranch ? "cloud" : "branch", branch.Name,
                () => Refs.CheckoutRefAsync(reference), detail);
        }
    }

    /// <summary>Modified files (conflicts excepted: the conflict editor comes in J5): opens their diff.</summary>
    private IEnumerable<PaletteEntry> FileEntries()
    {
        foreach (var item in WorkingTree.Unstaged.Concat(WorkingTree.Staged))
        {
            var file = item;
            yield return new PaletteEntry(PaletteGroup.Files, "document", file.Name, () =>
            {
                // The working directory panel must be shown: select the work-in-progress row first.
                if (Graph.HasWorkInProgress) Graph.SelectedRow = 0;
                WorkingTree.OpenFileCommand.Execute(file);
                return Task.CompletedTask;
            }, file.Side == WorkingDiffSide.Staged ? Loc.Format("Palette.Detail.Staged", file.Path) : file.Path);
        }
    }

    /// <summary>
    /// Command whose second step lists the files of the index (J5.6): the chosen one opens in the history or the blame
    /// (the file on disk, uncommitted lines included).
    /// </summary>
    private PaletteEntry PickFile(string icon, string label, FileInspectionKind kind) =>
        new(PaletteGroup.Commands, icon, label, () => Task.CompletedTask, Picker: new PalettePicker(
            Loc.Get("Palette.Picker.Title"),
            Loc.Get(kind == FileInspectionKind.History ? "Palette.Picker.History" : "Palette.Picker.Blame"),
            async cancellationToken =>
            {
                var path = Info.WorkingDirectory ?? Info.GitDirectory;
                if (await _git.ListFilesAsync(path, cancellationToken) is not { Value: Success<IReadOnlyList<string>> files }) return [];
                return [.. files.Value.Select(file => new PaletteEntry(PaletteGroup.Files, "document", file[(file.LastIndexOf('/') + 1)..],
                    () => InspectAsync(new FileInspection(kind, file)), file))];
            }));

    private static PaletteEntry Command(string icon, string label, ICommand command, string? shortcut = null) =>
        new(PaletteGroup.Commands, icon, label, () => command is IAsyncRelayCommand asyncCommand ? asyncCommand.ExecuteAsync(null) : Execute(command), Shortcut: shortcut);

    private static Task Execute(ICommand command)
    {
        command.Execute(null);
        return Task.CompletedTask;
    }
}
