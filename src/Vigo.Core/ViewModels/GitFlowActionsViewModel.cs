using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// Git Flow of a repository tab (J7.8, ADR-069): initialization, starting a feature, a release or a hotfix, finishing one
/// (merge commits, tag, deletion). The section of the left panel lists the Git Flow branches in progress.
/// </summary>
/// <remarks>
/// Operations go through <see cref="StagingActions"/> (one at a time, toast on failure). The settings are read at opening and
/// when the refs change; the branches in progress come from <see cref="GraphViewModel.Refs"/>.
/// </remarks>
public sealed partial class GitFlowActionsViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly GraphViewModel _graph;
    private readonly StagingActions _actions;
    private readonly IInputDialogService? _inputs;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;
    private bool _loaded;

    public GitFlowActionsViewModel(
        IGitService git, string repositoryPath, GraphViewModel graph, StagingActions actions,
        IInputDialogService? inputs, IUserNotifier? notifier, CancellationToken lifetime)
    {
        _git = git;
        _repositoryPath = repositoryPath;
        _graph = graph;
        _actions = actions;
        _inputs = inputs;
        _notifier = notifier;
        _lifetime = lifetime;
        _graph.RefsChanged += OnRefsChanged;
    }

    /// <summary>Settings of the repository; null while it is not initialized for Git Flow (or before the first read).</summary>
    public GitFlowConfig? Config { get; private set; }

    public bool IsInitialized => Config is not null;

    /// <summary>The settings changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The dialogs can be shown (not in tests or headless).</summary>
    public bool CanPrompt => _inputs is not null;

    /// <summary>"Initialiser Git Flow…" is offered: dialogs available and not initialized yet.</summary>
    public bool CanInitialize => CanPrompt && !IsInitialized && _graph.Refs is not null;

    /// <summary>The checked-out branch when it is a Git Flow one: "Terminer" applies to it.</summary>
    public GitRef? CurrentBranch => Config is { } config
        ? _graph.Refs?.OfKind(GitRefKind.LocalBranch).FirstOrDefault(b => b.IsCurrent && config.Parse(b.Name) is not null)
        : null;

    public async Task LoadAsync()
    {
        var result = await _git.GetGitFlowAsync(_repositoryPath, _lifetime);
        if (_lifetime.IsCancellationRequested || result.Value is not Success<GitFlowConfig?> read) return;
        _loaded = true;
        if (read.Value == Config) return;
        Config = read.Value;
        OnPropertyChanged(nameof(IsInitialized));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Git Flow branches of <paramref name="refs"/>, in name order: kind, short name, branch.</summary>
    public static IEnumerable<(GitFlowKind Kind, string Name, GitRef Branch)> BranchesOf(GitFlowConfig config, RepositoryRefs? refs) =>
        (refs?.OfKind(GitRefKind.LocalBranch) ?? [])
            .Select(b => (Parsed: config.Parse(b.Name), Branch: b))
            .Where(x => x.Parsed is not null)
            .Select(x => (x.Parsed!.Value.Kind, x.Parsed.Value.Name, x.Branch))
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>"feature", "release", "hotfix".</summary>
    public static string KindLabel(GitFlowKind kind) => kind switch
    {
        GitFlowKind.Feature => "feature",
        GitFlowKind.Release => "release",
        _ => "hotfix",
    };

    // ---- Initialize ------------------------------------------------------------------------------------------------

    /// <summary>"Initialiser Git Flow…": the production and development branches (the latter is created when missing).</summary>
    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (_inputs is null || _actions.IsBusy || IsInitialized) return;
        var locals = _graph.Refs?.OfKind(GitRefKind.LocalBranch).Select(b => b.Name).ToList() ?? [];
        var main = locals.Contains("main") ? "main" : locals.Contains("master") ? "master" : _graph.Refs?.CurrentBranch ?? "main";
        var dialog = new InputDialogViewModel(Loc.Get("GitFlow.Init.Title"), Loc.Get("GitFlow.Init.Confirm"), Loc.Get("GitFlow.Init.MainHeader"), RefNames.Check, main, RefNames.Check)
        {
            SpacesBecomeDashes = true,
            Description = Loc.Get("GitFlow.Init.Description"),
            SecondHeader = Loc.Get("GitFlow.Init.DevelopHeader"),
            SecondText = "develop",
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        var config = GitFlowConfig.Defaults(dialog.Value) with { Develop = dialog.SecondValue };
        if (config.Validate() is { } invalid)
        {
            _notifier?.Notify(NotificationSeverity.Warning, "Git Flow", invalid);
            return;
        }

        var initialized = await _actions.RunAsync(Loc.Get("GitFlow.Init.Failed"), ct => _git.GitFlowInitAsync(_repositoryPath, config, ct));
        await LoadAsync();
        if (!initialized) return;
        _notifier?.Notify(NotificationSeverity.Success, "Git Flow", Loc.Format("GitFlow.Init.Done", config.Develop, config.Main));
        await _graph.RefreshAsync();
    }

    // ---- Start -----------------------------------------------------------------------------------------------------

    /// <summary>"Démarrer une feature / release / hotfix…": a new branch from the development (production) branch, checked out.</summary>
    [RelayCommand]
    private async Task StartAsync(GitFlowKind kind)
    {
        if (_inputs is null || _actions.IsBusy || Config is not { } config) return;
        var locals = _graph.Refs?.OfKind(GitRefKind.LocalBranch).Select(b => b.Name).ToList() ?? [];
        var tags = _graph.Refs?.OfKind(GitRefKind.Tag).Select(t => t.Name).ToList() ?? [];

        var (title, header, placeholder) = kind switch
        {
            GitFlowKind.Feature => (Loc.Get("GitFlow.Start.FeatureTitle"), Loc.Get("GitFlow.Start.FeatureHeader"), Loc.Get("GitFlow.Start.FeaturePlaceholder")),
            GitFlowKind.Release => (Loc.Get("GitFlow.Start.ReleaseTitle"), Loc.Get("GitFlow.Start.ReleaseHeader"), "1.2.0"),
            _ => (Loc.Get("GitFlow.Start.HotfixTitle"), Loc.Get("GitFlow.Start.HotfixHeader"), "1.2.1"),
        };
        var dialog = new InputDialogViewModel(title, Loc.Get("GitFlow.Start.Confirm"), header,
            name => RefNames.Check(name)
                    ?? RefNames.CheckConflict(config.BranchOf(kind, name), locals, RefNameKind.Branch)
                    ?? (kind == GitFlowKind.Feature ? null : RefNames.CheckConflict(config.TagOf(name), tags, RefNameKind.Tag)))
        {
            SpacesBecomeDashes = true,
            Description = Loc.Format("GitFlow.Start.Description", config.PrefixOf(kind), config.BaseOf(kind)),
            NamePlaceholder = placeholder,
        };
        if (!await _inputs.ShowAsync(dialog, _lifetime)) return;

        var request = new GitFlowStartRequest(kind, dialog.Value);
        var started = await _actions.RunAsync(Loc.Get("GitFlow.Start.Failed"), ct => _git.GitFlowStartAsync(_repositoryPath, config, request, ct));
        if (!started) return;
        _notifier?.Notify(NotificationSeverity.Success, "Git Flow", Loc.Format("GitFlow.Start.Done", config.BranchOf(kind, request.Name)));
        await _graph.RefreshAsync();
    }

    // ---- Finish ----------------------------------------------------------------------------------------------------

    /// <summary>"Terminer la feature / release / hotfix…": merges with merge commits, tags a release or a hotfix, deletes the branch.</summary>
    [RelayCommand]
    private async Task FinishAsync(GitRef branch)
    {
        if (_actions.IsBusy || Config is not { } config || config.Parse(branch.Name) is not { } parsed) return;
        var (kind, name) = parsed;

        var into = kind == GitFlowKind.Feature ? config.Develop : Loc.Format("GitFlow.Finish.IntoThen", config.Main, config.Develop);
        var tagText = kind == GitFlowKind.Feature ? string.Empty : Loc.Format("GitFlow.Finish.TagText", config.TagOf(name), config.Main);
        var answer = await _actions.ConfirmAsync(new ConfirmationRequest(
            Loc.Format("GitFlow.Finish.Title", KindLabel(kind), name),
            Loc.Format("GitFlow.Finish.Message", branch.Name, into, tagText),
            Loc.Get("GitFlow.Finish.Confirm"), OptionText: Loc.Get("GitFlow.Finish.KeepBranch")));
        if (!answer.Confirmed) return;

        var request = new GitFlowFinishRequest(kind, name, answer.OptionChecked);
        GitFlowFinished? finished = null;
        var done = await _actions.RunAsync(Loc.Get("GitFlow.Finish.Failed"), ct => _git.GitFlowFinishAsync(_repositoryPath, config, request, ct),
            result => finished = result,
            conflict => _notifier?.Notify(NotificationSeverity.Warning, Loc.Format("GitFlow.Finish.ConflictTitle", branch.Name),
                Loc.Format("GitFlow.Finish.ConflictDetail", string.Join(", ", conflict.Paths), config.Develop)));
        await _graph.RefreshAsync();
        if (!done || finished is null) return;

        var text = Loc.Format("GitFlow.Finish.Done", branch.Name, string.Join(Loc.Get("GitFlow.Finish.And"), finished.MergedInto))
                   + (finished.Tag is { } tag ? Loc.Format("GitFlow.Finish.DoneTag", tag) : string.Empty)
                   + Loc.Get(finished.BranchDeleted ? "GitFlow.Finish.DoneDeleted" : "GitFlow.Finish.DoneEnd");
        _notifier?.Notify(NotificationSeverity.Success, "Git Flow", text);
    }

    /// <summary>"Terminer" from the palette: the checked-out branch, when it is a Git Flow one.</summary>
    [RelayCommand]
    private Task FinishCurrentAsync() => CurrentBranch is { } branch ? FinishAsync(branch) : Task.CompletedTask;

    public void Dispose() => _graph.RefsChanged -= OnRefsChanged;

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private void OnRefsChanged(object? sender, EventArgs e)
    {
        if (_loaded) _ = LoadAsync();
    }
}
