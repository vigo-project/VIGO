using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Core.ViewModels;

/// <summary>
/// One Git profile of the "Profils Git" section, editable in place: every change is applied and saved at once, like the
/// other preferences.
/// </summary>
public sealed partial class GitProfileItem : ObservableObject
{
    private readonly Action<GitProfileItem> _changed;
    private bool _syncing;

    internal GitProfileItem(GitProfile profile, bool isActive, Action<GitProfileItem> changed)
    {
        _changed = changed;
        Id = profile.Id;
        IsActive = isActive;
        Load(profile);
    }

    public string Id { get; }

    /// <summary>The list row is expanded to show the fields.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>Profile of the repositories no folder rule matches (also owns the session and the hosting accounts shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActivate))]
    public partial bool IsActive { get; set; }

    public bool CanActivate => !IsActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials), nameof(Summary))]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials), nameof(Summary))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Email { get; set; } = string.Empty;

    /// <summary><c>user.signingkey</c>: a GPG key id, or the path of an SSH key when <c>gpg.format</c> is <c>ssh</c>.</summary>
    [ObservableProperty]
    public partial string SigningKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SignCommits { get; set; }

    /// <summary>
    /// Position in the format list of the card: 0 = the Git configuration decides, then <see cref="SigningFormats.OpenPgp"/>,
    /// <see cref="SigningFormats.Ssh"/>, <see cref="SigningFormats.X509"/> (J7.7).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SigningFormat))]
    public partial int SigningFormatIndex { get; set; }

    /// <summary><c>gpg.format</c> imposed by the profile; null: the Git configuration decides.</summary>
    public string? SigningFormat
    {
        get => SigningFormatIndex is >= 1 and <= 3 ? s_formats[SigningFormatIndex] : null;
        set => SigningFormatIndex = Array.IndexOf(s_formats, SigningFormats.Normalize(value)) is var i and >= 1 ? i : 0;
    }

    private static readonly string?[] s_formats = [null, SigningFormats.OpenPgp, SigningFormats.Ssh, SigningFormats.X509];

    /// <summary>Folder patterns, one per line (<c>C:\dev\atlas\**</c>).</summary>
    [ObservableProperty]
    public partial string FolderRulesText { get; set; } = string.Empty;

    public string Initials => new GitIdentity(string.IsNullOrWhiteSpace(Name) ? Label : Name, null).Initials;

    public string Summary => ToProfile().Summary;

    internal GitProfile ToProfile() => new(
        Id, Label.Trim(), Name.Trim(), Email.Trim(), string.IsNullOrWhiteSpace(SigningKey) ? null : SigningKey.Trim(), SignCommits,
        [.. FolderRulesText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(r => r.Length > 0)],
        SigningFormat);

    /// <summary>Shows the stored profile (changed elsewhere) without saving it again.</summary>
    internal void Load(GitProfile profile)
    {
        _syncing = true;
        try
        {
            if (Label != profile.Label) Label = profile.Label;
            if (Name != profile.Name) Name = profile.Name;
            if (Email != profile.Email) Email = profile.Email;
            var key = profile.SigningKey ?? string.Empty;
            if (SigningKey != key) SigningKey = key;
            if (SignCommits != profile.SignCommits) SignCommits = profile.SignCommits;
            if (SigningFormat != SigningFormats.Normalize(profile.SigningFormat)) SigningFormat = profile.SigningFormat;
            // The text box keeps what is typed: only a different rule list replaces it.
            if (!ToProfile().FolderRules.SequenceEqual(profile.FolderRules)) FolderRulesText = string.Join('\n', profile.FolderRules);
        }
        finally
        {
            _syncing = false;
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_syncing && e.PropertyName is nameof(Label) or nameof(Name) or nameof(Email) or nameof(SigningKey) or nameof(SignCommits) or nameof(SigningFormatIndex) or nameof(FolderRulesText))
            _changed(this);
    }

    public override string ToString() => Label;
}

/// <summary>"Profils Git" section (J6.7, ADR-051): identities, signing and folder rules; hosting accounts follow the profile.</summary>
public sealed partial class PreferencesTabViewModel
{
    private readonly GitProfiles? _profiles;
    private readonly IGpgKeyService? _gpgKeys;
    private readonly IConfirmationService? _confirmations;
    private readonly Func<string, Task>? _switchProfile;
    private bool _syncingProfiles;

    public bool IsProfiles => SelectedSection.Section == PreferencesSection.Profiles;

    public ObservableCollection<GitProfileItem> Profiles { get; } = [];

    public bool HasNoProfile => Profiles.Count == 0;

    /// <summary>Profile the "Intégrations" section acts on, shown there; null without profiles.</summary>
    public string? ActiveProfileLabel => _profiles?.Active is { } active ? Loc.Format("Profile.ActiveLabel", active.Label) : null;

    public bool HasActiveProfile => ActiveProfileLabel is not null;

    private void InitializeProfiles()
    {
        if (_profiles is null) return;
        _profiles.Changed += OnProfilesChanged;
        SyncProfiles();
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => SyncProfiles();

    private void SyncProfiles()
    {
        if (_profiles is null) return;
        _syncingProfiles = true;
        try
        {
            var stored = _profiles.Profiles;
            for (var i = Profiles.Count - 1; i >= 0; i--)
            {
                if (stored.All(p => p.Id != Profiles[i].Id)) Profiles.RemoveAt(i);
            }
            foreach (var profile in stored)
            {
                var active = profile.Id == _profiles.Active?.Id;
                if (Profiles.FirstOrDefault(p => p.Id == profile.Id) is { } existing)
                {
                    existing.Load(profile);
                    existing.IsActive = active;
                }
                else
                {
                    Profiles.Add(new GitProfileItem(profile, active, OnProfileEdited));
                }
            }
        }
        finally
        {
            _syncingProfiles = false;
        }
        OnPropertyChanged(nameof(HasNoProfile));
        OnPropertyChanged(nameof(ActiveProfileLabel));
        OnPropertyChanged(nameof(HasActiveProfile));
    }

    private void OnProfileEdited(GitProfileItem item)
    {
        if (!_syncingProfiles) _profiles?.Update(item.ToProfile());
    }

    [RelayCommand]
    private void AddProfile()
    {
        if (_profiles is null) return;
        var label = Profiles.Count == 0 ? Loc.Get("Profile.DefaultName") : Loc.Format("Profile.NumberedName", Profiles.Count + 1);
        var profile = _profiles.Add(label, string.Empty, string.Empty);
        if (Profiles.FirstOrDefault(p => p.Id == profile.Id) is { } item) item.IsOpen = true;
    }

    [RelayCommand]
    private async Task ActivateProfileAsync(GitProfileItem? item)
    {
        if (item is null) return;
        // The application swaps the open repositories along with the profile; without it (tests), only the profile changes.
        if (_switchProfile is not null)
            await _switchProfile(item.Id);
        else
            _profiles?.SetActive(item.Id);
    }

    [RelayCommand]
    private async Task RemoveProfileAsync(GitProfileItem? item)
    {
        if (item is null || _profiles is null) return;
        var confirmed = _confirmations is null || (await _confirmations.ConfirmAsync(new ConfirmationRequest(
            Loc.Get("Profile.Remove.Title"),
            Loc.Format("Profile.Remove.Message", item.Label),
            Loc.Get("Profile.Remove.Confirm"), IsDestructive: true), _lifetime.Token)).Confirmed;
        if (confirmed) _profiles.Remove(item.Id);
    }

    /// <summary>
    /// Keys "Choisir une clé…" proposes: the OpenPGP secret keys, then the SSH keys of <c>~/.ssh</c> and the imported
    /// ones (an SSH key is referred to by its public key file, which git hands to <c>ssh-keygen -Y sign</c>), limited
    /// to the profile's format when it has one (J7.7, ADR-068).
    /// </summary>
    public async Task<IReadOnlyList<SigningKeyChoice>> ListSigningKeysAsync(string? format, CancellationToken cancellationToken = default)
    {
        var wanted = SigningFormats.Normalize(format);
        List<SigningKeyChoice> choices = [];
        if ((wanted is null or SigningFormats.OpenPgp) && _gpgKeys is not null)
        {
            foreach (var key in await _gpgKeys!.ListSecretKeysAsync(cancellationToken))
            {
                var expires = key.Expires is { } date ? Loc.Format("SigningKey.Expires", date.LocalDateTime.ToString("d", CultureInfo.CurrentCulture)) : string.Empty;
                choices.Add(new(SigningFormats.OpenPgp, key.KeyId, $"GPG · {key.KeyId}{(key.UserId.Length > 0 ? $" · {key.UserId}" : string.Empty)}{expires}"));
            }
        }
        if ((wanted is null or SigningFormats.Ssh) && _sshKeys is not null)
        {
            foreach (var key in await _sshKeys!.ListAsync(_preferences.Current.ImportedSshKeys, cancellationToken))
            {
                choices.Add(new(SigningFormats.Ssh, key.PrivateKeyPath + ".pub",
                    $"SSH · {key.Name} · {key.Type}{(key.Comment is { } comment ? $" · {comment}" : string.Empty)}"));
            }
        }
        return choices;
    }

    /// <summary>Whether "Générer une clé SSH" can be offered (the key service is there).</summary>
    public bool CanGenerateSshKey => _sshKeys is not null;

    /// <summary>
    /// Generates an ED25519 key (same service as the "Clés SSH" section) and returns it as a choice; null after a toast
    /// when it failed.
    /// </summary>
    public async Task<SigningKeyChoice?> GenerateSshSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_sshKeys is null) return null;
        switch ((await _sshKeys.GenerateAsync(cancellationToken)).Value)
        {
            case Success<SshKey> { Value: var key }:
                _notifier?.Notify(NotificationSeverity.Success, Loc.Format("SshKeys.Created.Title", key.Name),
                    Loc.Format("SigningKey.Created.Message", _sshKeys.KeyDirectory));
                return new(SigningFormats.Ssh, key.PrivateKeyPath + ".pub", $"SSH · {key.Name} · {key.Type}");
            case Failure failure:
                _notifier?.Notify(NotificationSeverity.Error, failure.Message, failure.Details);
                return null;
            default:
                return null;
        }
    }

    /// <summary>Whether "Générer une clé GPG" can be offered (the key service is there).</summary>
    public bool CanGenerateGpgKey => _gpgKeys is not null;

    /// <summary>
    /// Creates an OpenPGP key for the identity of the profile and returns it as a choice; null after a toast when the identity
    /// is incomplete or GnuPG failed.
    /// </summary>
    public async Task<SigningKeyChoice?> GenerateGpgSigningKeyAsync(string name, string email, CancellationToken cancellationToken = default)
    {
        if (_gpgKeys is null) return null;
        switch ((await _gpgKeys.GenerateAsync(name, email, cancellationToken)).Value)
        {
            case Success<GpgKey> { Value: var key }:
                _notifier?.Notify(NotificationSeverity.Success, Loc.Format("GpgKeys.Created.Title", key.KeyId), Loc.Get("GpgKeys.Created.Message"));
                return new(SigningFormats.OpenPgp, key.KeyId, $"GPG · {key.KeyId} · {key.UserId}");
            case Failure failure:
                _notifier?.Notify(NotificationSeverity.Error, failure.Message, failure.Details);
                return null;
            default:
                return null;
        }
    }

    /// <summary>The ASCII-armored public key of a GPG key id, null after a toast when it cannot be exported.</summary>
    public async Task<string?> ExportGpgPublicKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        if (_gpgKeys is null) return null;
        switch ((await _gpgKeys.ExportPublicKeyAsync(keyId, cancellationToken)).Value)
        {
            case Success<string> { Value: var armored }:
                _notifier?.Notify(NotificationSeverity.Success, Loc.Get("GpgKeys.Copied.Title"), Loc.Format("GpgKeys.Copied.Message", keyId));
                return armored;
            case Failure failure:
                _notifier?.Notify(NotificationSeverity.Error, failure.Message, failure.Details);
                return null;
            default:
                return null;
        }
    }

    private void DisposeProfiles()
    {
        if (_profiles is not null) _profiles.Changed -= OnProfilesChanged;
    }
}
