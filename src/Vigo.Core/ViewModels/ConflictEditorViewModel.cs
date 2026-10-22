using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>What "Prendre ce bloc" / the buttons of an unresolved conflict keep.</summary>
public enum ConflictTake
{
    Ours,
    Theirs,

    /// <summary>Ours then theirs ("Les deux").</summary>
    Both,
}

/// <summary>Choice made for one conflict: the lines of each side kept in the result.</summary>
public sealed class ConflictChoice(ConflictHunk hunk)
{
    private readonly bool[] _ours = new bool[hunk.Ours.Count];
    private readonly bool[] _theirs = new bool[hunk.Theirs.Count];

    public ConflictHunk Hunk { get; } = hunk;

    /// <summary>A side, a block or a line was chosen (keeping no line at all is a choice too).</summary>
    public bool IsResolved { get; private set; }

    public bool IsKept(ConflictSide side, int line) => (side == ConflictSide.Ours ? _ours : _theirs)[line];

    public void Take(ConflictTake take)
    {
        Array.Fill(_ours, take != ConflictTake.Theirs);
        Array.Fill(_theirs, take != ConflictTake.Ours);
        IsResolved = true;
    }

    /// <summary>Includes or excludes one line of a side (mockup: check box of a line).</summary>
    public void Toggle(ConflictSide side, int line)
    {
        var lines = side == ConflictSide.Ours ? _ours : _theirs;
        if (line < 0 || line >= lines.Length) return;
        lines[line] = !lines[line];
        IsResolved = true;
    }

    public void Reset()
    {
        Array.Clear(_ours);
        Array.Clear(_theirs);
        IsResolved = false;
    }

    /// <summary>Lines of the result for this conflict: the kept lines of ours, then those of theirs, in file order.</summary>
    public IReadOnlyList<(string Text, ConflictSide Side)> ResultLines
    {
        get
        {
            if (!IsResolved) return [];
            var lines = new List<(string, ConflictSide)>();
            for (var i = 0; i < _ours.Length; i++)
            {
                if (_ours[i]) lines.Add((Hunk.Ours[i], ConflictSide.Ours));
            }
            for (var i = 0; i < _theirs.Length; i++)
            {
                if (_theirs[i]) lines.Add((Hunk.Theirs[i], ConflictSide.Theirs));
            }
            return lines;
        }
    }
}

/// <summary>
/// Conflict editor of a repository tab (J5.4, mockup VIGO Outils.dc.html, "Conflits" screen), shown in place of the
/// graph: panes Ours / Theirs with a check box per line and "Prendre ce bloc", editable result, "Marquer comme résolu".
/// Files git cannot merge line by line (deleted on one side, binary) get a choice of a whole version instead. An
/// external merge tool chosen in the preferences replaces the editor (<c>git mergetool</c>).
/// </summary>
/// <remarks>
/// The conflicts come from the index stages (<see cref="IGitService.GetConflictAsync"/>), not from the markers of the
/// working tree. The result text is edited in the view (code editor); this class keeps the choices and builds the
/// lines of each conflict.
/// </remarks>
public sealed partial class ConflictEditorViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly WorkingTreeViewModel _tree;
    private readonly MergeActionsViewModel _merge;
    private readonly Preferences? _preferences;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _loading;
    private CancellationTokenSource? _tool;
    private bool _resolving;

    public ConflictEditorViewModel(
        IGitService git, string repositoryPath, WorkingTreeViewModel tree, MergeActionsViewModel merge,
        Preferences? preferences, IUserNotifier? notifier, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _tree = tree;
        _merge = merge;
        _preferences = preferences;
        _notifier = notifier;
        _lifetime = lifetime;
        _tree.StatusChanged += OnStatusChanged;
        _tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkingTreeViewModel.HasConflicts)) OpenFirstCommand.NotifyCanExecuteChanged();
        };
    }

    private StagingActions Actions => _tree.Actions;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>File shown; null while loading or closed.</summary>
    [ObservableProperty]
    public partial ConflictFile? File { get; private set; }

    /// <summary>Path of the file shown (set as soon as it is opened, before it is read).</summary>
    [ObservableProperty]
    public partial string? Path { get; private set; }

    /// <summary>One choice per conflict of <see cref="File"/>, in file order.</summary>
    public IReadOnlyList<ConflictChoice> Choices { get; private set; } = [];

    /// <summary>Changes each time the whole content must be shown again (file read, "Réinitialiser").</summary>
    [ObservableProperty]
    public partial int Version { get; private set; }

    /// <summary>Conflict the view shows and the navigation starts from; -1 when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial int CurrentBlock { get; private set; } = -1;

    /// <summary>Choices made or result edited since the file was read: closing asks first.</summary>
    [ObservableProperty]
    public partial bool HasChanges { get; private set; }

    /// <summary>An external merge tool is open on the file (<see cref="ExternalToolName"/>); the view waits for it.</summary>
    [ObservableProperty]
    public partial bool IsToolRunning { get; private set; }

    /// <summary>Raised when the choice of a conflict changed (its index): the view replaces its lines in the result.</summary>
    public event EventHandler<int>? BlockChanged;

    public bool IsText => File is { IsText: true };

    /// <summary>File without line-by-line merge: a whole version is chosen (deleted on one side, binary).</summary>
    public bool IsWholeFile => File is { IsText: false };

    public int UnresolvedCount => Choices.Count(c => !c.IsResolved);

    public bool CanMarkResolved => IsText && UnresolvedCount == 0 && !IsLoading && !Actions.IsBusy;

    // ---- Texts of the header (mockup) -------------------------------------------------------------------------------

    /// <summary>"Fusion de main dans feature/x" (mockup), or "Conflit" outside an operation (stash).</summary>
    public string Title => _merge.OperationTitle is { Length: > 0 } title ? title.TrimEnd('.') : Loc.Get("Conflict.Title");

    /// <summary>"src/a.cs · fichier 1 sur 3 en conflit".</summary>
    public string Subtitle
    {
        get
        {
            if (Path is not { } path) return string.Empty;
            var conflicted = _tree.Conflicted;
            var index = conflicted.Select(c => c.Path).ToList().IndexOf(path);
            return index < 0 ? path : Loc.Format("Conflict.Subtitle", path, index + 1, conflicted.Count);
        }
    }

    /// <summary>"2 conflits restants" / "Tous les conflits sont résolus".</summary>
    public string RemainingText => UnresolvedCount switch
    {
        _ when !IsText => string.Empty,
        0 => Loc.Get("Conflict.Remaining.None"),
        1 => Loc.Get("Conflict.Remaining.One"),
        var n => Loc.Format("Conflict.Remaining.Many", n),
    };

    public bool AllResolved => IsText && UnresolvedCount == 0;

    /// <summary>"Conflit 1 / 2".</summary>
    public string PositionText => Choices.Count == 0 || CurrentBlock < 0
        ? string.Empty
        : Loc.Format("Conflict.Position", CurrentBlock + 1, Choices.Count);

    public string AbortLabel => _merge.AbortLabel;

    public bool CanAbort => _merge.HasOperation;

    public string OursDetail => _merge.ConflictSides.Ours;

    public string TheirsDetail => _merge.ConflictSides.Theirs;

    /// <summary>"Conflit 1 · 2 lignes · résolu" (header of a block in a pane).</summary>
    public string BlockLabel(int block, ConflictSide side)
    {
        var choice = Choices[block];
        var count = choice.Hunk.Side(side).Count;
        var lines = count switch
        {
            0 => Loc.Get("Conflict.Block.NoLine"),
            1 => Loc.Get("Diff.Lines.One"),
            _ => Loc.Format("Diff.Lines.Many", count),
        };
        return Loc.Format(choice.IsResolved ? "Conflict.Block.Resolved" : "Conflict.Block.Header", block + 1, lines);
    }

    /// <summary>"Conflit 1 non résolu" (placeholder of the result).</summary>
    public static string UnresolvedLabel(int block) => Loc.Format("Conflict.Unresolved", block + 1);

    /// <summary>Card of a file without line-by-line merge: what happened to it.</summary>
    public string WholeFileTitle => File switch
    {
        { HasOurs: false } => Loc.Get("Conflict.Whole.DeletedOurs"),
        { HasTheirs: false } => Loc.Get("Conflict.Whole.DeletedTheirs"),
        { IsBinary: true } => Loc.Get("Conflict.Whole.Binary"),
        _ => string.Empty,
    };

    public string WholeFileMessage => File switch
    {
        { HasOurs: false } or { HasTheirs: false } =>
            Loc.Get("Conflict.Whole.ChooseDeletion"),
        { IsBinary: true } => Loc.Get("Conflict.Whole.ChooseVersion"),
        _ => string.Empty,
    };

    public string KeepOursLabel => File is { HasOurs: false } ? Loc.Get("Conflict.KeepOurs.Deletion") : Loc.Get("Conflict.KeepOurs.Version");

    public string KeepTheirsLabel => File is { HasTheirs: false } ? Loc.Get("Conflict.KeepTheirs.Deletion") : Loc.Get("Conflict.KeepTheirs.Version");

    /// <summary>"Supprimer le fichier" makes sense when both sides still have the file.</summary>
    public bool CanDeleteFile => File is { HasOurs: true, HasTheirs: true };

    // ---- External tool ----------------------------------------------------------------------------------------------

    /// <summary>Tool the preferences chose instead of the editor; null for the editor.</summary>
    private string? PreferredTool => _preferences?.Current.MergeTool;

    /// <summary>Tool of the "Outil externe" button: the preferred one, else the one of the Git configuration if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExternalTool), nameof(ExternalToolName))]
    public partial string? ExternalTool { get; private set; }

    public bool HasExternalTool => ExternalTool is not null;

    /// <summary>Name shown for <see cref="ExternalTool"/> ("Beyond Compare", "outil de Git (kdiff3)").</summary>
    public string ExternalToolName => MergeToolNames.Display(ExternalTool, _gitMergeTool);

    private string? _gitMergeTool;

    // ---- Opening and closing ----------------------------------------------------------------------------------------

    /// <summary>Opens the first conflicted file ("Ouvrir l'éditeur de conflits" of the banner).</summary>
    [RelayCommand(CanExecute = nameof(HasConflicts))]
    private Task OpenFirstAsync() => _tree.Conflicted.Count > 0 ? OpenAsync(_tree.Conflicted[0].Entry) : Task.CompletedTask;

    private bool HasConflicts() => _tree.HasConflicts;

    /// <summary>
    /// Opens a conflicted file: in the external tool when the preferences say so, else in the editor (the file is
    /// read from the index stages).
    /// </summary>
    public async Task OpenAsync(StatusEntry entry)
    {
        if (!entry.IsConflicted) return;
        await ReadGitMergeToolAsync();
        if (PreferredTool is not null)
        {
            await RunToolAsync(entry.Path);
            return;
        }
        await LoadAsync(entry);
    }

    private async Task ReadGitMergeToolAsync()
    {
        _gitMergeTool = (await _git.GetConfigValueAsync(_repositoryPath, "merge.tool", _lifetime)).Value is Success<string?> { Value: { } tool }
            ? tool
            : null;
        ExternalTool = PreferredTool ?? (_gitMergeTool is null ? null : UserPreferences.GitConfiguredMergeTool);
        OnPropertyChanged(nameof(ExternalToolName));
    }

    private async Task LoadAsync(StatusEntry entry)
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var loading = _loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);

        Path = entry.Path;
        File = null;
        Choices = [];
        ErrorMessage = null;
        HasChanges = false;
        IsLoading = true;
        IsOpen = true;
        _tree.MarkOpened(entry.Path, WorkingDiffSide.Unstaged);
        NotifyState();

        var result = await _git.GetConflictAsync(_repositoryPath, entry, loading.Token);
        if (!ReferenceEquals(loading, _loading)) return;
        IsLoading = false;
        switch (result.Value)
        {
            case Success<ConflictFile> { Value: var file }:
                File = file;
                Choices = [.. file.Hunks.Select(h => new ConflictChoice(h))];
                CurrentBlock = Choices.Count > 0 ? 0 : -1;
                break;
            case Failure f:
                ErrorMessage = Loc.Format("Conflict.ReadFailed", f.Message);
                break;
            case Canceled:
                return;
            case Conflict or AuthenticationFailed:
                ErrorMessage = Loc.Get("Conflict.ReadFailedShort");
                break;
        }
        Version++;
        NotifyState();
    }

    /// <summary>Back to the graph; asks first when choices would be lost.</summary>
    [RelayCommand]
    private async Task CloseAsync()
    {
        if (!await ConfirmCloseAsync()) return;
        Close();
    }

    /// <summary>
    /// Asks before choices or edits are lost (another file opened, back to the graph); true when the editor may
    /// close.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync() =>
        !IsOpen || !HasChanges
        || await Actions.ConfirmAsync(Loc.Get("Conflict.Abandon.Title"),
            Loc.Format("Conflict.Abandon.Message", Path), Loc.Get("Conflict.Abandon.Confirm"), isDestructive: true);

    /// <summary>Closes without asking (the file is no longer conflicted, the tab shows something else).</summary>
    public void Close()
    {
        _loading?.Cancel();
        IsOpen = false;
        IsLoading = false;
        File = null;
        Path = null;
        Choices = [];
        CurrentBlock = -1;
        HasChanges = false;
        ErrorMessage = null;
        _tree.MarkOpened(null, WorkingDiffSide.Unstaged);
        NotifyState();
    }

    // ---- Choices ----------------------------------------------------------------------------------------------------

    /// <summary>"Prendre ce bloc" of a pane, or Ours / Theirs / Les deux of an unresolved conflict.</summary>
    public void TakeBlock(int block, ConflictTake take)
    {
        if (!IsValid(block)) return;
        Choices[block].Take(take);
        CurrentBlock = block;
        Changed(block);
    }

    /// <summary>"Tout prendre" of a pane: every conflict from that side.</summary>
    public void TakeAll(ConflictSide side)
    {
        for (var i = 0; i < Choices.Count; i++)
        {
            Choices[i].Take(side == ConflictSide.Ours ? ConflictTake.Ours : ConflictTake.Theirs);
            Changed(i);
        }
    }

    /// <summary>Check box of a line of a pane.</summary>
    public void ToggleLine(int block, ConflictSide side, int line)
    {
        if (!IsValid(block)) return;
        Choices[block].Toggle(side, line);
        CurrentBlock = block;
        Changed(block);
    }

    /// <summary>The result was edited by hand.</summary>
    public void MarkEdited() => HasChanges = true;

    /// <summary>"Réinitialiser": every conflict unresolved again, result rebuilt.</summary>
    [RelayCommand]
    private void Reset()
    {
        if (!IsText) return;
        foreach (var choice in Choices) choice.Reset();
        HasChanges = false;
        CurrentBlock = Choices.Count > 0 ? 0 : -1;
        Version++;
        NotifyState();
    }

    [RelayCommand]
    private void NextBlock() => MoveBlock(1);

    [RelayCommand]
    private void PreviousBlock() => MoveBlock(-1);

    private void MoveBlock(int delta)
    {
        if (Choices.Count == 0) return;
        // Same block again: the view still scrolls back to it.
        var target = Math.Clamp(CurrentBlock + delta, 0, Choices.Count - 1);
        if (target == CurrentBlock) OnPropertyChanged(nameof(CurrentBlock));
        else CurrentBlock = target;
    }

    private bool IsValid(int block) => IsText && block >= 0 && block < Choices.Count;

    private void Changed(int block)
    {
        HasChanges = true;
        BlockChanged?.Invoke(this, block);
        NotifyState();
    }

    // ---- Resolution -------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Marquer comme résolu": writes <paramref name="editorText"/> (the result pane) and stages the file, then opens
    /// the next conflicted file, or goes back to the graph when none is left.
    /// </summary>
    public Task MarkResolvedAsync(string editorText) =>
        File is { IsText: true } file && CanMarkResolved
            ? ResolveAsync(file.Path, new ConflictResolution.Content(file.ToFileText(editorText)))
            : Task.CompletedTask;

    /// <summary>Card of a file without line-by-line merge: keeps one side's version (which may be the deletion).</summary>
    [RelayCommand]
    private Task KeepSideAsync(ConflictSide side) =>
        File is { } file ? ResolveAsync(file.Path, new ConflictResolution.TakeSide(side)) : Task.CompletedTask;

    /// <summary>Card of a file without line-by-line merge: deletes the file, after confirmation.</summary>
    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        if (File is not { } file) return;
        if (!await Actions.ConfirmAsync(Loc.Get("Conflict.DeleteFile.Title"),
                Loc.Format("Conflict.DeleteFile.Message", file.Path), Loc.Get("Conflict.DeleteFile.Confirm"), isDestructive: true)) return;
        await ResolveAsync(file.Path, new ConflictResolution.Delete());
    }

    private async Task ResolveAsync(string path, ConflictResolution resolution)
    {
        _resolving = true;
        bool resolved;
        try
        {
            resolved = await Actions.RunAsync(Loc.Format("Conflict.ResolveFailed", path),
                ct => _git.ResolveConflictAsync(_repositoryPath, path, resolution, ct));
        }
        finally
        {
            _resolving = false;
        }
        if (!resolved) return;
        HasChanges = false;
        await ContinueWithNextAsync(path);
    }

    /// <summary>After a file is resolved: the next conflicted one, or back to the graph with what is left to do.</summary>
    private async Task ContinueWithNextAsync(string resolvedPath)
    {
        var next = _tree.Conflicted.FirstOrDefault(c => c.Path != resolvedPath);
        if (next is not null)
        {
            await LoadAsync(next.Entry);
            return;
        }
        Close();
        _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Conflict.AllResolved.Title"),
            _merge.HasOperation ? _merge.OperationDetail : Loc.Format("Conflict.AllResolved.Staged", resolvedPath));
    }

    /// <summary>"Outil externe": the file in the external merge tool, the editor waiting until it closes.</summary>
    [RelayCommand]
    private async Task OpenInExternalToolAsync()
    {
        if (Path is not { } path || ExternalTool is null) return;
        if (!await ConfirmCloseAsync()) return;
        HasChanges = false;
        await RunToolAsync(path);
    }

    /// <summary>Stops waiting for the external tool (the tool itself may stay open).</summary>
    [RelayCommand]
    private void CancelTool() => _tool?.Cancel();

    private async Task RunToolAsync(string path)
    {
        if (IsToolRunning) return;
        var tool = ExternalTool ?? PreferredTool;
        if (tool is null) return;
        _tool = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Path = path;
        File = null;
        Choices = [];
        ErrorMessage = null;
        IsOpen = true;
        IsToolRunning = true;
        _tree.MarkOpened(path, WorkingDiffSide.Unstaged);
        NotifyState();
        GitResult result;
        try
        {
            result = await _git.RunMergeToolAsync(_repositoryPath, path,
                tool == UserPreferences.GitConfiguredMergeTool ? null : tool, _tool.Token);
        }
        finally
        {
            IsToolRunning = false;
            _tool.Dispose();
            _tool = null;
        }

        await _tree.RefreshAsync();
        var stillConflicted = _tree.Conflicted.Any(c => c.Path == path);
        switch (result.Value)
        {
            case Done when !stillConflicted:
                _notifier?.Notify(NotificationSeverity.Success, Loc.Get("Conflict.ToolResolved.Title"), Loc.Format("Conflict.ToolResolved.Message", path, ExternalToolName));
                break;
            case Failure f:
                _notifier?.Notify(NotificationSeverity.Warning, Loc.Format("Conflict.StillConflicted", path), f.Message);
                break;
            case Done or Canceled or Conflict or AuthenticationFailed:
                break;
        }

        // Started from the editor and not resolved: back to the editor; otherwise back to the graph.
        if (stillConflicted && PreferredTool is null && _tree.Conflicted.FirstOrDefault(c => c.Path == path) is { } item)
            await LoadAsync(item.Entry);
        else
            Close();
    }

    // ---- Status -----------------------------------------------------------------------------------------------------

    /// <summary>The file was resolved or the operation abandoned elsewhere: nothing left to show.</summary>
    private void OnStatusChanged(object? sender, EventArgs e)
    {
        NotifyState();
        if (!IsOpen || IsToolRunning || _resolving || IsLoading || Path is not { } path) return;
        if (_tree.Conflicted.All(c => c.Path != path)) Close();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsWholeFile));
        OnPropertyChanged(nameof(UnresolvedCount));
        OnPropertyChanged(nameof(CanMarkResolved));
        OnPropertyChanged(nameof(AllResolved));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(AbortLabel));
        OnPropertyChanged(nameof(CanAbort));
        OnPropertyChanged(nameof(OursDetail));
        OnPropertyChanged(nameof(TheirsDetail));
        OnPropertyChanged(nameof(WholeFileTitle));
        OnPropertyChanged(nameof(WholeFileMessage));
        OnPropertyChanged(nameof(KeepOursLabel));
        OnPropertyChanged(nameof(KeepTheirsLabel));
        OnPropertyChanged(nameof(CanDeleteFile));
    }

    public void Dispose()
    {
        _tree.StatusChanged -= OnStatusChanged;
        _loading?.Cancel();
        _loading?.Dispose();
        _tool?.Cancel();
    }
}

/// <summary>Merge tools offered in the preferences: names of <c>git mergetool --tool</c> and their display names.</summary>
public static class MergeToolNames
{
    /// <summary>Tool name → display name (product names, not translated).</summary>
    public static IReadOnlyList<(string Tool, string Name)> Known { get; } =
    [
        ("bc", "Beyond Compare"),
        ("kdiff3", "KDiff3"),
        ("vscode", "Visual Studio Code"),
        ("winmerge", "WinMerge"),
        ("p4merge", "P4Merge"),
        ("meld", "Meld"),
        ("tortoisemerge", "TortoiseMerge"),
    ];

    /// <summary>"Beyond Compare"; "outil de Git (kdiff3)" for the configured one; the name itself when unknown.</summary>
    public static string Display(string? tool, string? gitMergeTool = null) => tool switch
    {
        null => string.Empty,
        UserPreferences.GitConfiguredMergeTool => gitMergeTool is null ? Loc.Get("Conflict.GitTool") : Loc.Format("Conflict.GitToolNamed", gitMergeTool),
        _ => Known.FirstOrDefault(k => k.Tool == tool).Name ?? tool,
    };
}
