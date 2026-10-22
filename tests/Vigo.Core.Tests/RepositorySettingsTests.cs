using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Git.Cli;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

/// <summary>Preferences › "Dépôt en cours" (J7.29): settings kept in the repository's own git config.</summary>
public sealed class RepositorySettingsTests : IDisposable
{
    private readonly TestRepoBuilder _repo = TestRepoBuilder.Create();
    private readonly Preferences _preferences = new(new InMemoryPreferencesStore(), new UserPreferences());
    private readonly GitService _git = new(new GitCli());

    public RepositorySettingsTests() => _repo.Commit("one", ("a.txt", "1\n"));

    public void Dispose()
    {
        _preferences.Dispose();
        _repo.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_section_is_disabled_without_a_repository_and_follows_the_one_shown_before()
    {
        using var sut = new PreferencesTabViewModel(_preferences, git: _git);
        PreferencesSectionItem Item() => sut.Sections.Single(s => s.Section == PreferencesSection.Repository);
        Assert.False(Item().IsEnabled);
        Assert.NotNull(Item().Tooltip);

        sut.SetRepository(_repo.Path, "demo");
        sut.Show(PreferencesSection.Repository);

        Assert.True(Item().IsEnabled);
        Assert.True(sut.IsRepository);
        Assert.Equal("demo", sut.SelectedSectionTitle);

        sut.SetRepository(null, null);

        Assert.False(Item().IsEnabled);
        Assert.True(sut.IsGeneral);
        Assert.Equal(sut.SelectedSection.Label, sut.SelectedSectionTitle);
    }

    [Fact]
    public async Task Git_flow_fields_are_saved_only_when_changed_valid_and_on_existing_branches()
    {
        var init = GitFlowConfig.Defaults();
        _repo.Git("branch", "develop");
        await _git.GitFlowInitAsync(_repo.Path, init, Ct);
        var sut = new RepositorySettingsViewModel(_git, _repo.Path, "demo", null, Ct);
        await sut.LoadAsync();
        Assert.True(sut.IsGitFlowInitialized);
        Assert.Equal("feature/", sut.FlowFeature);
        Assert.False(sut.CanSaveGitFlow);

        sut.FlowRelease = "feature/";
        await sut.WaitForGitFlowCheckAsync();
        Assert.False(sut.CanSaveGitFlow);
        Assert.NotNull(sut.FlowMessage);

        sut.FlowRelease = "rel/";
        sut.FlowDevelop = "nowhere";
        await sut.WaitForGitFlowCheckAsync();
        Assert.False(sut.CanSaveGitFlow);
        Assert.NotNull(sut.FlowMessage);

        sut.FlowDevelop = "develop";
        await sut.WaitForGitFlowCheckAsync();
        Assert.True(sut.CanSaveGitFlow);
        await sut.SaveGitFlowCommand.ExecuteAsync(null);

        Assert.Equal("rel/", _repo.Git("config", "gitflow.prefix.release").Trim());
        Assert.False(sut.CanSaveGitFlow);
    }

    [Fact]
    public async Task A_repository_without_git_flow_offers_no_fields()
    {
        var sut = new RepositorySettingsViewModel(_git, _repo.Path, "demo", null, Ct);
        await sut.LoadAsync();
        Assert.False(sut.IsGitFlowInitialized);
        Assert.False(sut.CanSaveGitFlow);
    }

    [Fact]
    public async Task Choosing_an_encoding_writes_gui_encoding_locally_and_automatic_removes_it()
    {
        using var sut = new PreferencesTabViewModel(_preferences, git: _git);
        sut.SetRepository(_repo.Path, "demo");
        var settings = sut.Repository!;
        await settings.LoadAsync();
        Assert.Null(settings.SelectedEncoding.Value);

        settings.SelectedEncoding = settings.EncodingOptions.Single(o => o.Value == "windows-1252");
        await settings.Saving;
        Assert.Equal("windows-1252", _repo.Git("config", "--local", "gui.encoding").Trim());

        // Read back when the section is opened again.
        var again = new RepositorySettingsViewModel(_git, _repo.Path, "demo", null, Ct);
        await again.LoadAsync();
        Assert.Equal("windows-1252", again.SelectedEncoding.Value);

        settings.SelectedEncoding = settings.EncodingOptions[0];
        await settings.Saving;
        Assert.Null(Assert.IsType<Success<string?>>((await _git.GetEncodingAsync(_repo.Path, Ct)).Value).Value);
    }

    [Fact]
    public async Task Hooks_are_listed_and_toggled_and_the_folder_and_skip_switch_are_written_to_the_config()
    {
        var hooks = Path.Combine(_repo.Path, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(hooks, "pre-push.sample"), "#!/bin/sh\n");
        var sut = new RepositorySettingsViewModel(_git, _repo.Path, "demo", null, Ct);
        await sut.LoadAsync();

        var hook = Assert.Single(sut.Hooks);
        Assert.Equal("pre-commit", hook.Name);
        Assert.True(hook.IsEnabled);
        Assert.False(sut.HasNoHooks);

        await sut.SetHookEnabledAsync(hook, false);

        Assert.True(File.Exists(Path.Combine(hooks, "pre-commit.disabled")));
        Assert.False(Assert.Single(sut.Hooks).IsEnabled);

        sut.SkipHooks = true;
        await sut.Saving;
        Assert.Equal("true", _repo.Git("config", "--local", "vigo.skiphooks").Trim());

        sut.HooksPath = "custom";
        Assert.True(sut.SaveHooksPathCommand.CanExecute(null));
        await sut.SaveHooksPathCommand.ExecuteAsync(null);

        Assert.Equal("custom", _repo.Git("config", "--local", "core.hooksPath").Trim());
        Assert.True(sut.HasNoHooks);
        Assert.False(sut.SaveHooksPathCommand.CanExecute(null));

        await sut.ResetHooksPathCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, sut.HooksPath);
        Assert.Equal("pre-commit", Assert.Single(sut.Hooks).Name);
    }
}
