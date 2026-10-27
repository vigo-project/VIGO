using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

/// <summary>Signing format and key picker of the Git profiles (J7.7, ADR-068).</summary>
public sealed class ProfileSigningKeysTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Preferences _preferences = new(new InMemoryPreferencesStore(), new UserPreferences());
    private readonly FakeGpgKeys _gpg = new();
    private readonly FakeSshKeys _ssh = new();
    private readonly Notifications _notifier = new();
    private readonly GitProfiles _profiles = new(new MemoryStore());

    public void Dispose()
    {
        _preferences.Dispose();
        _profiles.Dispose();
    }

    private PreferencesTabViewModel Create() =>
        new(_preferences, _ssh, _notifier, profiles: _profiles, gpgKeys: _gpg);

    [Fact]
    public void Format_goes_to_git_before_the_key()
    {
        var profile = new GitProfile("a", "A", "Ada", "ada@example.com", "KEY", SignCommits: true, SigningFormat: "SSH");

        Assert.Equal(
            ["user.name=Ada", "user.email=ada@example.com", "gpg.format=ssh", "user.signingkey=KEY", "commit.gpgsign=true", "tag.gpgsign=true"],
            profile.ConfigOverrides());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pgp-or-whatever")]
    public void Unknown_format_sends_no_format(string? format) =>
        Assert.DoesNotContain(new GitProfile("a", "A", " ", string.Empty, SigningFormat: format).ConfigOverrides(), o => o.StartsWith("gpg.format", StringComparison.Ordinal));

    [Fact]
    public void Card_format_choice_is_saved_in_the_profile()
    {
        _profiles.Add("Travail", "Ada", "ada@example.com");
        using var sut = Create();
        var item = Assert.Single(sut.Profiles);
        Assert.Equal(0, item.SigningFormatIndex);

        item.SigningFormatIndex = 2;

        Assert.Equal(SigningFormats.Ssh, _profiles.Profiles[0].SigningFormat);

        item.SigningFormatIndex = 0;

        Assert.Null(_profiles.Profiles[0].SigningFormat);
    }

    [Fact]
    public void Card_follows_a_format_changed_elsewhere()
    {
        var profile = _profiles.Add("Travail", "Ada", "ada@example.com");
        using var sut = Create();

        _profiles.Update(profile with { SigningFormat = SigningFormats.X509 });

        Assert.Equal(3, sut.Profiles[0].SigningFormatIndex);
    }

    [Fact]
    public async Task Without_a_format_both_kinds_of_keys_are_offered_and_each_carries_its_format()
    {
        _gpg.Keys.Add(new GpgKey("AAAABBBBCCCCDDDD", "Ada <ada@example.org>", null));
        _ssh.Listed.Add(Key(@"C:\Users\me\.ssh\id_ed25519"));
        using var sut = Create();

        var choices = await sut.ListSigningKeysAsync(null, Ct);

        Assert.Equal(2, choices.Count);
        Assert.Equal(new SigningKeyChoice(SigningFormats.OpenPgp, "AAAABBBBCCCCDDDD", "GPG · AAAABBBBCCCCDDDD · Ada <ada@example.org>"), choices[0]);
        Assert.Equal(SigningFormats.Ssh, choices[1].Format);
        Assert.Equal(@"C:\Users\me\.ssh\id_ed25519.pub", choices[1].Value);
    }

    [Fact]
    public async Task A_format_limits_the_keys_offered()
    {
        _gpg.Keys.Add(new GpgKey("AAAABBBBCCCCDDDD", string.Empty, null));
        _ssh.Listed.Add(Key(@"C:\Users\me\.ssh\id_ed25519"));
        using var sut = Create();

        Assert.Equal(SigningFormats.OpenPgp, Assert.Single(await sut.ListSigningKeysAsync("openpgp", Ct)).Format);
        Assert.Equal(SigningFormats.Ssh, Assert.Single(await sut.ListSigningKeysAsync("ssh", Ct)).Format);
        Assert.Empty(await sut.ListSigningKeysAsync("x509", Ct));
    }

    [Fact]
    public async Task Generating_a_ssh_key_returns_its_public_file()
    {
        using var sut = Create();

        var choice = await sut.GenerateSshSigningKeyAsync(Ct);

        Assert.NotNull(choice);
        Assert.Equal(SigningFormats.Ssh, choice.Format);
        Assert.EndsWith("id_ed25519.pub", choice.Value, StringComparison.Ordinal);
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Success);
    }

    [Fact]
    public async Task A_failed_generation_is_reported_without_a_choice()
    {
        _ssh.GenerateFails = true;
        using var sut = Create();

        Assert.Null(await sut.GenerateSshSigningKeyAsync(Ct));
        Assert.Contains(_notifier.Items, n => n.Severity == NotificationSeverity.Error);
    }

    [Fact]
    public async Task A_profile_can_be_created_from_the_git_configuration()
    {
        var git = new FakeGit();
        git.Config["user.name"] = "Ada";
        git.Config["user.email"] = "ada@example.com";
        git.Config["user.signingkey"] = "KEY";
        git.Config["gpg.format"] = "openpgp";
        git.Config["commit.gpgsign"] = "true";
        using var sut = new PreferencesTabViewModel(_preferences, git: git, profiles: _profiles);

        await sut.AddProfileFromGitConfigCommand.ExecuteAsync(null);

        var profile = Assert.Single(_profiles.Profiles);
        Assert.Equal(("Ada", "ada@example.com", "KEY", true), (profile.Name, profile.Email, profile.SigningKey, profile.SignCommits));
        Assert.Equal(SigningFormats.OpenPgp, SigningFormats.Normalize(profile.SigningFormat));
        Assert.Contains("commit.gpgsign=true", profile.ConfigOverrides());
    }

    [Fact]
    public async Task Without_key_services_nothing_is_offered()
    {
        using var sut = new PreferencesTabViewModel(_preferences, profiles: _profiles);

        Assert.Empty(await sut.ListSigningKeysAsync(null, Ct));
        Assert.False(sut.CanGenerateSshKey);
    }

    [Fact]
    public async Task Format_survives_the_profiles_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vigo-profiles-{Guid.NewGuid():N}.json");
        try
        {
            var store = new JsonGitProfileStore(path, NullLogger<JsonGitProfileStore>.Instance);
            await store.SaveAsync(new GitProfileSet([new GitProfile("default", "T", "C", "c@x", "KEY", true, SigningFormat: SigningFormats.Ssh)], "default"), Ct);

            using var reloaded = new GitProfiles(store);

            Assert.Equal(SigningFormats.Ssh, reloaded.Profiles[0].SigningFormat);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Hand_edited_unknown_format_is_dropped_on_load()
    {
        var set = new GitProfileSet([new GitProfile("a", "A", "a", "a@x", SigningFormat: "Bogus"), new GitProfile("b", "B", "b", "b@x", SigningFormat: "SSH")], "a");

        using var profiles = new GitProfiles(new MemoryStore(set));

        Assert.Null(profiles.Profiles[0].SigningFormat);
        Assert.Equal(SigningFormats.Ssh, profiles.Profiles[1].SigningFormat);
    }

    private static SshKey Key(string path) => new(path, "ED25519", "SHA256:x", "ssh-ed25519 AAAA", null);

    private sealed class MemoryStore(GitProfileSet? initial = null) : IGitProfileStore
    {
        public GitProfileSet Load() => initial ?? GitProfileSet.Empty;

        public Task SaveAsync(GitProfileSet profiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeGpgKeys : IGpgKeyService
    {
        public List<GpgKey> Keys { get; } = [];

        public Task<IReadOnlyList<GpgKey>> ListSecretKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GpgKey>>([.. Keys]);

        public Task<GitResult<GpgKey>> GenerateAsync(string name, string email, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<GpgKey>>(new Success<GpgKey>(new GpgKey("AAAA1111BBBB2222", $"{name} <{email}>", null)));

        public Task<GitResult<string>> ExportPublicKeyAsync(string keyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<string>>(new Success<string>("-----BEGIN PGP PUBLIC KEY BLOCK-----"));
    }

    private sealed class FakeSshKeys : ISshKeyService
    {
        public List<SshKey> Listed { get; } = [];
        public bool GenerateFails { get; set; }

        public string KeyDirectory => @"C:\Users\me\.ssh";

        public Task<IReadOnlyList<SshKey>> ListAsync(IEnumerable<string> imported, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SshKey>>([.. Listed]);

        public Task<GitResult<SshKey>> ReadAsync(string privateKeyPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<SshKey>>(new Failure("unused"));

        public Task<GitResult<SshKey>> GenerateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult<SshKey>>(GenerateFails ? new Failure("La génération de la clé a échoué.") : new Success<SshKey>(Key(@"C:\Users\me\.ssh\id_ed25519")));

        public Task<GitResult> DeleteAsync(string privateKeyPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitResult>(Done.Instance);

        public Task<SshAgentState> GetAgentStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(SshAgentState.Running);
    }

    private sealed class Notifications : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title));
    }
}
