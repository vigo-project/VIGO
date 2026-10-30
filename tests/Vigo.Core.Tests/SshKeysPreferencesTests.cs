using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class SshKeysPreferencesTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Preferences _preferences = new(new InMemoryPreferencesStore(), new UserPreferences());
    private readonly FakeSshKeys _keys = new();
    private readonly Notifications _notifier = new();

    public void Dispose() => _preferences.Dispose();

    private PreferencesTabViewModel Create() => new(_preferences, _keys, _notifier);

    private static SshKey Key(string path, string comment = "me@example.com") =>
        new(path, "ED25519", "SHA256:" + Path.GetFileName(path), $"ssh-ed25519 AAAA {comment}", comment);

    [Fact]
    public async Task Section_lists_the_keys_and_the_agent_state()
    {
        _keys.Listed.Add(Key(TestPaths.Native(@"C:\Users\me\.ssh\id_ed25519")));
        _keys.Agent = SshAgentState.Disabled;
        using var sut = Create();

        sut.Show(PreferencesSection.SshKeys);
        await sut.LoadSshKeysAsync(Ct);

        Assert.True(sut.IsSshKeys);
        var item = Assert.Single(sut.SshKeys);
        Assert.Equal("id_ed25519", item.ToString());
        Assert.Equal("Dans ~/.ssh · me@example.com", item.Usage);
        Assert.True(sut.HasNoDefaultSshKey);
        Assert.Equal("Service désactivé", sut.SshAgentStatus);
        Assert.False(sut.HasSshAgentWarning);
    }

    [Fact]
    public async Task Choosing_a_key_saves_it_and_marks_it()
    {
        _keys.Listed.Add(Key(TestPaths.Native(@"C:\Users\me\.ssh\id_ed25519")));
        _keys.Listed.Add(Key(TestPaths.Native(@"C:\Users\me\.ssh\id_rsa")));
        using var sut = Create();
        await sut.LoadSshKeysAsync(Ct);

        sut.ToggleDefaultSshKeyCommand.Execute(sut.SshKeys[1]);

        Assert.Equal(TestPaths.Native(@"C:\Users\me\.ssh\id_rsa"), _preferences.Current.SshKeyPath);
        Assert.Equal(new SshSettings(TestPaths.Native(@"C:\Users\me\.ssh\id_rsa"), false), _preferences.Current.Ssh);
        Assert.True(sut.SshKeys[1].IsDefault);
        Assert.False(sut.SshKeys[0].IsDefault);
        Assert.False(sut.HasNoDefaultSshKey);
        Assert.Equal("Ne plus utiliser dans VIGO", sut.SshKeys[1].DefaultActionLabel);

        sut.ToggleDefaultSshKeyCommand.Execute(sut.SshKeys[1]);
        Assert.Null(_preferences.Current.SshKeyPath);
        Assert.True(sut.HasNoDefaultSshKey);
    }

    [Fact]
    public async Task Importing_a_key_of_another_tool_lists_it_and_uses_it()
    {
        var otherToolKey = TestPaths.Native(@"C:\Users\me\AppData\Roaming\othertool\ssh\github_rsa");
        _keys.Readable[otherToolKey] = Key(otherToolKey);
        using var sut = Create();

        await sut.ImportSshKeyAsync(otherToolKey + ".pub", Ct);

        Assert.Equal([otherToolKey], _preferences.Current.ImportedSshKeys);
        Assert.Equal(otherToolKey, _preferences.Current.SshKeyPath);
        var item = Assert.Single(sut.SshKeys);
        Assert.True(item.IsImported);
        Assert.True(item.IsDefault);
        Assert.Equal("Utilisée par VIGO · me@example.com", item.Usage);
        Assert.Equal(NotificationSeverity.Success, Assert.Single(_notifier.Items).Severity);

        await sut.RemoveSshKeyCommand.ExecuteAsync(item);
        Assert.Empty(_preferences.Current.ImportedSshKeys);
        Assert.Null(_preferences.Current.SshKeyPath);
        Assert.Empty(sut.SshKeys);
    }

    [Fact]
    public async Task Importing_a_key_without_public_key_reports_why()
    {
        using var sut = Create();

        await sut.ImportSshKeyAsync(TestPaths.Native(@"C:\keys\lonely"), Ct);

        Assert.Empty(_preferences.Current.ImportedSshKeys);
        var (severity, title) = Assert.Single(_notifier.Items);
        Assert.Equal(NotificationSeverity.Error, severity);
        Assert.Equal("Import de la clé impossible", title);
    }

    [Fact]
    public async Task Generated_key_is_used_at_once()
    {
        using var sut = Create();

        await sut.GenerateSshKeyCommand.ExecuteAsync(null);

        Assert.Equal(TestPaths.Native(@"C:\Users\me\.ssh\id_ed25519"), _preferences.Current.SshKeyPath);
        Assert.True(Assert.Single(sut.SshKeys).IsDefault);
        Assert.Equal("Clé id_ed25519 créée", Assert.Single(_notifier.Items).Title);
    }

    [Fact]
    public async Task Deleting_a_key_removes_its_files_and_stops_using_it()
    {
        var path = TestPaths.Native(@"C:\Users\me\.ssh\id_ed25519");
        _keys.Listed.Add(Key(path));
        using var sut = Create();
        await sut.LoadSshKeysAsync(Ct);
        sut.ToggleDefaultSshKeyCommand.Execute(sut.SshKeys[0]);
        Assert.Equal(path, _preferences.Current.SshKeyPath);

        await sut.DeleteSshKeyCommand.ExecuteAsync(sut.SshKeys[0]);

        Assert.Equal([path], _keys.Deleted);
        Assert.Null(_preferences.Current.SshKeyPath);
        Assert.Empty(sut.SshKeys);
        Assert.Equal(NotificationSeverity.Success, Assert.Single(_notifier.Items).Severity);
    }

    [Fact]
    public async Task Agent_switch_is_saved_and_warns_when_the_service_does_not_run()
    {
        _keys.Agent = SshAgentState.Stopped;
        using var sut = Create();
        await sut.LoadSshKeysAsync(Ct);

        sut.UseWindowsSshAgent = true;

        Assert.True(_preferences.Current.UseWindowsSshAgent);
        Assert.True(sut.HasSshAgentWarning);
        Assert.StartsWith("Le service ssh-agent ne tourne pas", sut.SshAgentWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ssh_settings_survive_the_preferences_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vigo-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonPreferencesStore(Path.Combine(directory, "preferences.json"), NullLogger<JsonPreferencesStore>.Instance);
            var saved = new UserPreferences { SshKeyPath = TestPaths.Native(@"C:\k\id"), ImportedSshKeys = [TestPaths.Native(@"C:\k\id"), TestPaths.Native(@"C:\k\other")], UseWindowsSshAgent = true };
            await store.SaveAsync(saved, Ct);

            var loaded = store.Load();
            Assert.Equal(TestPaths.Native(@"C:\k\id"), loaded.SshKeyPath);
            Assert.Equal([TestPaths.Native(@"C:\k\id"), TestPaths.Native(@"C:\k\other")], loaded.ImportedSshKeys);
            Assert.True(loaded.UseWindowsSshAgent);
            // An older file without these settings: defaults.
            Assert.Empty(new UserPreferences().Normalize().ImportedSshKeys);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeSshKeys : ISshKeyService
    {
        public List<SshKey> Listed { get; } = [];
        public Dictionary<string, SshKey> Readable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SshAgentState Agent { get; set; } = SshAgentState.Running;

        public string KeyDirectory => TestPaths.Native(@"C:\Users\me\.ssh");

        public Task<IReadOnlyList<SshKey>> ListAsync(IEnumerable<string> imported, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SshKey>>([.. Listed, .. imported.Where(Readable.ContainsKey).Select(p => Readable[p])]);

        public Task<GitResult<SshKey>> ReadAsync(string privateKeyPath, CancellationToken cancellationToken = default)
        {
            var path = privateKeyPath.EndsWith(".pub", StringComparison.Ordinal) ? privateKeyPath[..^4] : privateKeyPath;
            return Task.FromResult<GitResult<SshKey>>(Readable.TryGetValue(path, out var key)
                ? new Success<SshKey>(key)
                : new Failure("Clé publique introuvable"));
        }

        public Task<GitResult<SshKey>> GenerateAsync(CancellationToken cancellationToken = default)
        {
            var key = Key(TestPaths.Native(@"C:\Users\me\.ssh\id_ed25519"));
            Listed.Add(key);
            return Task.FromResult<GitResult<SshKey>>(new Success<SshKey>(key));
        }

        public List<string> Deleted { get; } = [];

        public Task<GitResult> DeleteAsync(string privateKeyPath, CancellationToken cancellationToken = default)
        {
            Deleted.Add(privateKeyPath);
            Listed.RemoveAll(k => k.PrivateKeyPath == privateKeyPath);
            Readable.Remove(privateKeyPath);
            return Task.FromResult<GitResult>(Done.Instance);
        }

        public Task<SshAgentState> GetAgentStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Agent);
    }

    private sealed class Notifications : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title));
    }
}
