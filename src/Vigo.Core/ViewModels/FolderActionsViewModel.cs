using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Right click menu of a folder of the working directory panel (tree mode): discard its changes, ignore it, create a file
/// in it. Stage / unstage, stash and patch are the ones of <see cref="WorkingTreeViewModel"/>, <see cref="StashActionsViewModel"/>
/// and <see cref="PatchActionsViewModel"/>, applied to the folder. Texts: Folder.* resources (ADR-075).
/// </summary>
public sealed partial class FolderActionsViewModel(
    IGitService git, string repositoryPath, WorkingTreeViewModel tree,
    IInputDialogService? inputs, IUserNotifier? notifier, CancellationToken lifetime)
{
    private StagingActions Actions => tree.Actions;

    /// <summary>Discards the unstaged changes of every file below the folder (untracked ones are deleted), after confirmation.</summary>
    [RelayCommand]
    private async Task DiscardFolderAsync(FolderRow folder)
    {
        if (folder.Group != FileGroup.Unstaged || Actions.IsBusy) return;
        var entries = tree.FilesIn(folder).Select(f => f.Entry).ToList();
        if (entries.Count == 0) return;
        var message = Loc.Format("Folder.Discard.Message", folder.Path, entries.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
        if (!await Actions.ConfirmAsync(Loc.Get("Folder.Discard.Title"), message, Loc.Get("Folder.Discard.Confirm"), isDestructive: true)) return;
        await Actions.RunAsync(Loc.Get("WorkingTree.DiscardFailed"), ct => git.DiscardAsync(repositoryPath, entries, ct));
    }

    /// <summary>Adds <c>folder/</c> to the <c>.gitignore</c> at the top level; files already tracked stay tracked (git's rule).</summary>
    [RelayCommand]
    private async Task IgnoreFolderAsync(FolderRow folder)
    {
        if (Actions.IsBusy) return;
        var pattern = folder.Path + "/";
        if (await Actions.RunAsync(Loc.Get("Folder.Ignore.Failed"), ct => git.IgnoreAsync(repositoryPath, [pattern], ct)))
            notifier?.Notify(NotificationSeverity.Success, Loc.Get("Folder.Ignore.Done"), Loc.Format("Folder.Ignore.DoneDetail", pattern));
    }

    /// <summary>"Créer un fichier dans ce dossier": asks the name, creates the empty file (the status then lists it as new).</summary>
    [RelayCommand]
    private async Task CreateFileAsync(FolderRow folder)
    {
        if (inputs is null || tree.FullPath(folder) is not { } directory) return;
        var dialog = new InputDialogViewModel(Loc.Get("Folder.NewFile.Title"), Loc.Get("Folder.NewFile.Confirm"), Loc.Get("Folder.NewFile.Header"), ValidateName)
        {
            Description = Loc.Format("Folder.NewFile.Description", folder.Path),
            NamePlaceholder = Loc.Get("Folder.NewFile.Placeholder"),
        };
        if (!await inputs.ShowAsync(dialog, lifetime)) return;

        var name = dialog.Value;
        var failure = await Task.Run(() => CreateEmptyFile(Path.Combine(directory, name)), lifetime);
        if (failure is not null) notifier?.Notify(NotificationSeverity.Error, Loc.Get("Folder.NewFile.Failed"), failure);
        else await tree.RefreshAsync();
    }

    /// <summary>A file name, not a path: no separator, no character Windows refuses.</summary>
    private static string? ValidateName(string name) =>
        name.Length == 0 ? string.Empty
        : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') ? Loc.Get("Folder.NewFile.Invalid")
        : null;

    /// <summary>Creates the file without replacing an existing one; null on success, else the reason.</summary>
    private static string? CreateEmptyFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            return null;
        }
        catch (IOException) when (File.Exists(path))
        {
            return Loc.Get("Folder.NewFile.Exists");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
