using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>How the summary length compares with the conventions (counter color of the mockup).</summary>
public enum SummaryLengthLevel
{
    Normal,

    /// <summary>Over <see cref="CommitMessage.RecommendedSummaryLength"/>.</summary>
    Warning,

    /// <summary>Over <see cref="CommitMessage.MaxSummaryLength"/>.</summary>
    Danger,
}

/// <summary>
/// Commit form at the bottom of the working directory panel (mockup: showWip): summary with its 50 / 72 counter,
/// description, amend, signing indicator, "Valider N fichiers sur branche".
/// </summary>
/// <remarks>
/// The commit runs through the panel's <see cref="StagingActions"/>: one index operation at a time, status reread
/// afterwards (which reloads the graph once HEAD moved). User-facing texts come from <see cref="Loc"/> (ADR-075).
/// </remarks>
public sealed partial class CommitFormViewModel : ObservableObject, IDisposable
{
    private readonly IGitService _git;
    private readonly string _repositoryPath;
    private readonly WorkingTreeViewModel _tree;
    private readonly IUserNotifier? _notifier;
    private readonly CancellationToken _lifetime;
    private bool _settingsRequested;
    private (string Summary, string Description)? _prefilled;
    private int _prefillVersion;
    private Task _prefill = Task.CompletedTask;
    private Task _signing = Task.CompletedTask;
    private readonly GitProfiles? _profiles;
    private readonly CommitMessageGenerator? _generator;
    private CancellationTokenSource? _generating;
    private bool _settingsLoaded;

    public CommitFormViewModel(
        IGitService git, string repositoryPath, WorkingTreeViewModel tree, IUserNotifier? notifier, CancellationToken lifetime, GitProfiles? profiles = null,
        CommitMessageGenerator? generator = null)
    {
        _generator = generator;
        if (_generator is not null) _generator.Changed += OnGeneratorChanged;
        _git = git;
        _repositoryPath = repositoryPath;
        _tree = tree;
        _notifier = notifier;
        _lifetime = lifetime;
        _profiles = profiles;
        if (_profiles is not null) _profiles.Changed += OnProfilesChanged;
        RefreshProfile();
        _tree.StatusChanged += OnStatusChanged;
        _tree.Actions.PropertyChanged += OnActionsPropertyChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryLengthText), nameof(SummaryLengthLevel), nameof(CanCommit), nameof(CommitTooltip))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitSkippingHooksCommand), nameof(CommitAndPushCommand))]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    /// <summary>Replace the HEAD commit instead of creating a new one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit), nameof(CommitLabel), nameof(CommitTooltip))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(CommitSkippingHooksCommand), nameof(CommitAndPushCommand))]
    public partial bool IsAmend { get; set; }

    /// <summary>"Signé GPG", "Signé SSH"…: commits are signed according to the config; null when they are not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSigned))]
    public partial string? SigningLabel { get; private set; }

    public bool IsSigned => SigningLabel is not null;

    /// <summary>"Profil Travail" when a Git profile applies to the repository (J6.7); null without profiles.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    public partial string? ProfileLabel { get; private set; }

    /// <summary>"Nom &lt;e-mail&gt;" commits are authored with, for the tooltip of <see cref="ProfileLabel"/>.</summary>
    [ObservableProperty]
    public partial string? ProfileIdentity { get; private set; }

    public bool HasProfile => ProfileLabel is not null;

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        RefreshProfile();
        // The profile may start or stop signing: the config is read again.
        if (_settingsLoaded) _signing = LoadSigningAsync();
    }

    private void RefreshProfile()
    {
        var profile = _profiles?.ResolveFor(_repositoryPath);
        ProfileLabel = profile is null ? null : Loc.Format("CommitForm.Profile", profile.Label);
        ProfileIdentity = profile is null ? null : profile.Summary;
    }

    /// <summary>"12 / 72".</summary>
    public string SummaryLengthText => $"{Summary.Length} / {CommitMessage.MaxSummaryLength}";

    public SummaryLengthLevel SummaryLengthLevel => Summary.Length switch
    {
        > CommitMessage.MaxSummaryLength => SummaryLengthLevel.Danger,
        > CommitMessage.RecommendedSummaryLength => SummaryLengthLevel.Warning,
        _ => SummaryLengthLevel.Normal,
    };

    /// <summary>There is a commit to amend (not before the first commit).</summary>
    public bool CanAmend => _tree.Status?.Head is not null;

    private int StagedCount => _tree.Staged.Count;

    private int ConflictCount => _tree.Conflicted.Count;

    /// <summary>
    /// A merge (or a cherry-pick / revert) stopped on conflicts: the next commit concludes it, even with nothing staged
    /// (the resolution may keep HEAD's version), and cannot be an amend.
    /// </summary>
    private bool IsConcluding => _tree.Status?.Operation?.Kind is RepositoryOperationKind.Merge
        or RepositoryOperationKind.CherryPick or RepositoryOperationKind.Revert;

    /// <summary>
    /// A rebase is stopped on conflicts: it goes on with "Continuer" (operation banner), not with a commit. Stopped on an
    /// edit step, commits and amends are allowed (that is what the stop is for).
    /// </summary>
    private bool IsRebasing => _tree.Status?.Operation is { Kind: RepositoryOperationKind.Rebase, IsEditStop: false };

    /// <summary>Summary given, no conflict, something staged (or amending, or concluding a merge), no operation running.</summary>
    public bool CanCommit =>
        !_tree.Actions.IsBusy
        && _tree.Status is not null
        && ConflictCount == 0
        && !IsRebasing
        && !string.IsNullOrWhiteSpace(Summary)
        && (IsAmend ? CanAmend && !IsConcluding : StagedCount > 0 || IsConcluding);

    /// <summary>Push run by "Valider et pousser" after a successful commit (set by the repository tab).</summary>
    public Func<Task>? PushAfterCommit { get; set; }

    /// <summary>Commit possible and a branch is checked out (a detached HEAD cannot be pushed).</summary>
    public bool CanCommitAndPush => CanCommit && PushAfterCommit is not null && _tree.Status?.Branch is not null;

    /// <summary>Label of the commit button (mockup).</summary>
    public string CommitLabel
    {
        get
        {
            // The operation banner says what is left to do in a rebase, conflicts included.
            if (IsRebasing)
                return Loc.Get("CommitForm.Rebasing");
            if (ConflictCount > 0)
                return ConflictCount == 1 ? Loc.Get("CommitForm.ResolveOne") : Loc.Format("CommitForm.ResolveMany", ConflictCount.ToString("N0", CultureInfo.CurrentCulture));
            if (IsConcluding && !IsAmend)
                return _tree.Status?.Branch is { } target ? Loc.Format("CommitForm.CommitMergeOn", target) : Loc.Get("CommitForm.CommitMerge");
            if (IsAmend && _tree.Status?.Head is { } head)
                return Loc.Format("CommitForm.AmendLabel", head.ToShortString());
            if (StagedCount == 0)
                return Loc.Get("CommitForm.NothingStaged");

            var files = StagedCount == 1 ? Loc.Get("CommitForm.FilesOne") : Loc.Format("CommitForm.FilesMany", StagedCount.ToString("N0", CultureInfo.CurrentCulture));
            return _tree.Status?.Branch is { } branch ? Loc.Format("CommitForm.CommitOnBranch", files, branch) : Loc.Format("CommitForm.CommitDetached", files);
        }
    }

    /// <summary>Why the button is disabled, or what it does.</summary>
    public string CommitTooltip =>
        CanCommit ? Loc.Get("CommitForm.Tip.Commit")
        : ConflictCount > 0 ? Loc.Get("CommitForm.Tip.Conflicts")
        : IsRebasing ? Loc.Get("CommitForm.Tip.Rebasing")
        : IsAmend && IsConcluding ? Loc.Get("CommitForm.Tip.AmendMerge")
        : !IsAmend && StagedCount == 0 ? Loc.Get("CommitForm.Tip.NothingStaged")
        : string.IsNullOrWhiteSpace(Summary) ? Loc.Get("CommitForm.Tip.NoSummary")
        : Loc.Get("CommitForm.Tip.Busy");

    /// <summary>Completes once the amend message and the signing settings requested so far are loaded (tests).</summary>
    internal Task WhenLoadedAsync() => Task.WhenAll(_prefill, _signing);

    /// <summary>An AI engine is chosen in the preferences (the "Générer" button is shown enabled).</summary>
    public bool IsGenerationEnabled => _generator?.IsEnabled == true;

    /// <summary>A message is being written by the engine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate), nameof(GenerateTooltip), nameof(IsGenerateIdle))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    public partial bool IsGenerating { get; private set; }

    /// <summary>"Générer": an engine is chosen, something is staged, no generation is running.</summary>
    public bool CanGenerate => IsGenerationEnabled && !IsGenerating && StagedCount > 0 && !IsRebasing;

    public string GenerateTooltip =>
        !IsGenerationEnabled ? Loc.Get("CommitForm.Gen.NoEngine")
        : IsGenerating ? Loc.Get("CommitForm.Gen.Running")
        : StagedCount == 0 ? Loc.Get("CommitForm.Gen.NothingStaged")
        : Loc.Get("CommitForm.Gen.Ready");

    private void OnGeneratorChanged(object? sender, EventArgs e) => NotifyGenerateChanged();

    private void NotifyGenerateChanged()
    {
        OnPropertyChanged(nameof(IsGenerationEnabled));
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(GenerateTooltip));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Asks the chosen engine for a message describing the staged changes and puts it in the form.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (_generator is null || !CanGenerate) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        _generating = source;
        IsGenerating = true;
        try
        {
            switch (await _generator.GenerateAsync(_repositoryPath, source.Token))
            {
                case Success<GeneratedCommitMessage> s:
                    // The user asked for it: the proposal replaces what the form holds, and is theirs to edit.
                    _prefilled = null;
                    Summary = s.Value.Summary;
                    Description = s.Value.Description;
                    break;
                case Failure f:
                    _notifier?.Notify(NotificationSeverity.Error, Loc.Get("CommitForm.GenFailedTitle"), f.Message);
                    break;
                case AuthenticationFailed a:
                    _notifier?.Notify(NotificationSeverity.Error, Loc.Get("CommitForm.GenDeniedTitle"), a.Message);
                    break;
                case Canceled:
                    break;
            }
        }
        finally
        {
            _generating = null;
            IsGenerating = false;
        }
    }

    /// <summary>"Générer" is shown (no generation running); the cancel button takes its place otherwise.</summary>
    public bool IsGenerateIdle => !IsGenerating;

    /// <summary>"Annuler" next to the loader: stops the engine (the process is killed, the form is left as it was).</summary>
    [RelayCommand]
    public void CancelGeneration() => _generating?.Cancel();

    public void Dispose()
    {
        CancelGeneration();
        if (_generator is not null) _generator.Changed -= OnGeneratorChanged;
        _tree.StatusChanged -= OnStatusChanged;
        if (_profiles is not null) _profiles.Changed -= OnProfilesChanged;
        _tree.Actions.PropertyChanged -= OnActionsPropertyChanged;
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync() => await RunCommitAsync(skipHooks: false);

    /// <summary>"Valider avec --no-verify": the pre-commit and commit-msg hooks are skipped.</summary>
    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitSkippingHooksAsync() => await RunCommitAsync(skipHooks: true);

    /// <summary>"Valider et pousser" (Ctrl+Maj+Entrée): pushes once the commit is created.</summary>
    [RelayCommand(CanExecute = nameof(CanCommitAndPush))]
    private async Task CommitAndPushAsync()
    {
        if (CanCommitAndPush && await RunCommitAsync(skipHooks: false) && PushAfterCommit is { } push) await push();
    }

    private async Task<bool> RunCommitAsync(bool skipHooks)
    {
        if (!CanCommit || _tree.Status is not { } status) return false;

        var amend = IsAmend;
        if (amend && status is { Upstream: { } upstream, Ahead: 0, Head: { } head })
        {
            // The commit is reachable from the upstream branch: amending it rewrites published history.
            var message = Loc.Format("CommitForm.AmendPublishedMessage", head.ToShortString(), upstream);
            if (!await _tree.Actions.ConfirmAsync(Loc.Get("CommitForm.AmendPublishedTitle"), message, Loc.Get("CommitForm.AmendPublishedConfirm"))) return false;
        }

        var summary = Summary.Trim();
        var request = new CommitRequest(CommitMessage.Compose(Summary, Description), amend, skipHooks);
        GitSha? created = null;
        var succeeded = await _tree.Actions.RunAsync(Loc.Get(amend ? "CommitForm.AmendFailed" : "CommitForm.CommitFailed"), async ct =>
        {
            var result = await _git.CommitAsync(_repositoryPath, request, ct);
            switch (result)
            {
                case Success<GitSha> s:
                    created = s.Value;
                    return Done.Instance;
                case Failure f:
                    // Hook output and multi-line errors are worth showing in full.
                    return f with { Message = f.Details ?? f.Message };
                case Conflict c:
                    return c;
                case AuthenticationFailed a:
                    return a;
                case Canceled c:
                    return c;
            }
            return new Failure(Loc.Get("CommitForm.Unexpected"));
        }, new UndoStep(Loc.Format(amend ? "CommitForm.UndoAmend" : "CommitForm.UndoCommit", summary), ResetMode.Soft));

        if (!succeeded || created is not { } sha) return false;
        _notifier?.Notify(NotificationSeverity.Success, Loc.Format(amend ? "CommitForm.AmendedTitle" : "CommitForm.CommittedTitle", sha.ToShortString()), summary);
        _prefilled = null;
        Summary = string.Empty;
        Description = string.Empty;
        IsAmend = false;
        return true;
    }

    partial void OnIsAmendChanged(bool value)
    {
        var version = ++_prefillVersion;
        if (value)
        {
            // Like `git commit --amend`: start from the message of the commit being replaced.
            if (string.IsNullOrWhiteSpace(Summary) && string.IsNullOrWhiteSpace(Description) && _tree.Status?.Head is { } head)
                _prefill = PrefillAsync(head, version);
        }
        else if (_prefilled is { } prefilled && prefilled == (Summary, Description))
        {
            // Untouched amend message: amending is canceled, so is the message.
            _prefilled = null;
            Summary = string.Empty;
            Description = string.Empty;
        }
    }

    private async Task PrefillAsync(GitSha head, int version)
    {
        var result = await _git.GetCommitAsync(_repositoryPath, head, _lifetime);
        // Unchecked again, or the user started typing meanwhile: keep their text.
        if (version != _prefillVersion || !IsAmend || Summary.Length > 0 || Description.Length > 0) return;
        if (result.Value is not Success<CommitDetails> s) return;

        var (summary, description) = CommitMessage.Split(s.Value.Message);
        Summary = summary;
        Description = description;
        _prefilled = (summary, description);
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (!_settingsRequested)
        {
            _settingsRequested = true;
            _settingsLoaded = true;
            _signing = LoadSigningAsync();
        }

        if (IsAmend && !CanAmend) IsAmend = false;
        FollowOperationMessage();
        NotifyStateChanged();
    }

    /// <summary>
    /// Like <c>git commit</c> during a merge: the form starts from the prepared message (MERGE_MSG) when it is empty;
    /// once the merge is aborted or concluded elsewhere, an untouched prepared message goes away with it.
    /// </summary>
    private void FollowOperationMessage()
    {
        var message = IsConcluding ? _tree.Status?.Operation?.Message : null;
        if (message is { Length: > 0 } && !IsAmend && Summary.Length == 0 && Description.Length == 0)
        {
            var (summary, description) = CommitMessage.Split(message);
            Summary = summary;
            Description = description;
            _prefilled = (summary, description);
        }
        else if (message is null && !IsAmend && _prefilled is { } prefilled && prefilled == (Summary, Description))
        {
            _prefilled = null;
            Summary = string.Empty;
            Description = string.Empty;
        }
    }

    private void OnActionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StagingActions.IsBusy)) NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanAmend));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CommitLabel));
        OnPropertyChanged(nameof(CommitTooltip));
        CommitCommand.NotifyCanExecuteChanged();
        CommitSkippingHooksCommand.NotifyCanExecuteChanged();
        CommitAndPushCommand.NotifyCanExecuteChanged();
        NotifyGenerateChanged();
    }

    private async Task LoadSigningAsync()
    {
        // A profile that signs wins over the configuration (it hands commit.gpgsign to git.exe, ADR-051).
        var profile = _profiles?.ResolveFor(_repositoryPath);
        var profileSigns = profile is { SignCommits: true } && !string.IsNullOrWhiteSpace(profile.SigningKey);
        if (!profileSigns)
        {
            var sign = await _git.GetConfigValueAsync(_repositoryPath, "commit.gpgsign", _lifetime);
            if (sign.Value is not Success<string?> { Value: { } value } || !IsTrue(value))
            {
                SigningLabel = null;
                return;
            }
        }

        // The profile's format is handed to git.exe too, so it wins over gpg.format.
        var format = profile?.SigningFormat;
        if (SigningFormats.Normalize(format) is null)
        {
            var configured = await _git.GetConfigValueAsync(_repositoryPath, "gpg.format", _lifetime);
            format = (configured.Value as Success<string?>)?.Value;
        }
        SigningLabel = SigningFormats.Normalize(format) switch
        {
            SigningFormats.Ssh => Loc.Get("CommitForm.SignedSsh"),
            SigningFormats.X509 => Loc.Get("CommitForm.SignedX509"),
            _ => Loc.Get("CommitForm.SignedGpg"),
        };
    }

    /// <summary>Git boolean: true, yes, on, or a non-zero integer.</summary>
    private static bool IsTrue(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "on"
        || (int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var n) && n != 0);
}
