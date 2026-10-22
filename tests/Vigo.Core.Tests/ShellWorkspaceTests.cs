using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

/// <summary>Several windows (J4.7): tabs moved between windows, repositories and preferences open once.</summary>
public sealed class ShellWorkspaceTests : IDisposable
{
    private sealed class NullPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<string> Titles { get; } = [];
        public void Notify(NotificationSeverity severity, string title, string? message = null) => Titles.Add(title);
    }

    private readonly FakeGit _git = new();
    private readonly ShellWorkspace _workspace = new();
    private readonly RecentRepositories _recent = new(new InMemoryRecentRepositoryStore(), TimeProvider.System);
    private readonly Preferences _preferences = new(new InMemoryPreferencesStore(), new UserPreferences());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _preferences.Dispose();

    public ShellWorkspaceTests()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        _git.Repos[@"C:\src\billing"] = new Success<RepositoryInfo>(Repo("billing"));
    }

    private ShellViewModel Window(RecordingNotifier? notifier = null, bool withWelcomeTab = true) =>
        new(_git, new NullPicker(), notifier ?? new RecordingNotifier(), _recent, TimeProvider.System, NullLogger<ShellViewModel>.Instance,
            preferences: _preferences, workspace: _workspace, withWelcomeTab: withWelcomeTab);

    private static RepositoryInfo Repo(string name) =>
        new($@"C:\src\{name}", $@"C:\src\{name}\.git", name, "main", new string('a', 40), false, false, false);

    [Fact]
    public async Task A_moved_tab_is_selected_in_its_new_window_and_notifies_there()
    {
        var firstNotifier = new RecordingNotifier();
        var secondNotifier = new RecordingNotifier();
        var first = Window(firstNotifier);
        await first.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        first.NewTabCommand.Execute(null);
        await first.OpenRepositoryAsync(@"C:\src\billing", Ct);
        var second = Window(secondNotifier);
        var atlas = first.Tabs[0];

        first.MoveTab(atlas, second, 0);

        Assert.Equal(["billing"], first.Tabs.Select(t => t.Title));
        Assert.Equal(["atlas", "Accueil"], second.Tabs.Select(t => t.Title));
        Assert.Same(atlas, second.SelectedTab);
        Assert.Equal("billing", first.SelectedTab!.Title);
        // The toasts (and dialogs) of the tab follow it.
        ((IUserNotifier)atlas.Services!).Notify(NotificationSeverity.Info, "moved");
        Assert.Equal(["moved"], secondNotifier.Titles);
        Assert.Empty(firstNotifier.Titles);
    }

    [Fact]
    public async Task Moving_the_last_tab_asks_the_window_to_close()
    {
        var first = Window();
        await first.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        var second = Window();
        var closeRequested = 0;
        first.CloseRequested += (_, _) => closeRequested++;

        first.MoveTab(first.Tabs[0], second);

        Assert.Empty(first.Tabs);
        Assert.Null(first.SelectedTab);
        Assert.Equal(1, closeRequested);
        Assert.Equal("atlas", second.SelectedTab!.Title);
    }

    [Fact]
    public async Task Opening_a_repository_open_in_another_window_activates_that_window()
    {
        var first = Window();
        await first.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        first.NewTabCommand.Execute(null);
        var second = Window();
        var activated = 0;
        first.ActivationRequested += (_, _) => activated++;

        await second.OpenRepositoryAsync(@"C:\src\atlas", Ct);

        Assert.IsType<WelcomeTabViewModel>(Assert.Single(second.Tabs));
        Assert.Equal("atlas", first.SelectedTab!.Title);
        Assert.Equal(1, activated);
    }

    [Fact]
    public async Task Restoring_a_window_skips_repositories_open_in_another_one()
    {
        var first = Window();
        await first.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        var second = Window();

        await second.RestoreSessionAsync(new SessionWindow([@"C:\src\atlas", @"C:\src\billing"], 0), Ct);

        Assert.Equal(["billing"], second.Tabs.Select(t => t.Title));
        Assert.Single(first.Tabs);
    }

    [Fact]
    public void Preferences_open_once_in_the_whole_application()
    {
        var first = Window();
        first.OpenPreferencesCommand.Execute(null);
        var second = Window();
        var activated = 0;
        first.ActivationRequested += (_, _) => activated++;

        second.OpenPreferencesCommand.Execute(null);

        Assert.DoesNotContain(second.Tabs, t => t is PreferencesTabViewModel);
        Assert.Equal(1, activated);
    }

    [Fact]
    public async Task A_tab_moves_to_a_new_window_only_when_others_are_left()
    {
        var opened = new List<ShellViewModel>();
        _workspace.OpenWindow = withWelcomeTab =>
        {
            var window = Window(withWelcomeTab: withWelcomeTab);
            opened.Add(window);
            return window;
        };
        var first = Window();
        Assert.True(first.MoveTabToNewWindowCommand.CanExecute(null));

        first.MoveTabToNewWindowCommand.Execute(null);
        Assert.Empty(opened);

        await first.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        first.NewTabCommand.Execute(null);
        first.MoveTabToNewWindowCommand.Execute(first.Tabs[0]);

        var created = Assert.Single(opened);
        Assert.Equal("atlas", Assert.Single(created.Tabs).Title);
        Assert.IsType<WelcomeTabViewModel>(Assert.Single(first.Tabs));
        Assert.Equal([first, created], _workspace.Shells);
    }

    [Fact]
    public void Window_commands_need_the_application_windows()
    {
        var shell = Window();

        Assert.False(shell.NewWindowCommand.CanExecute(null));
        Assert.False(shell.MoveTabToNewWindowCommand.CanExecute(null));
        Assert.False(shell.QuitCommand.CanExecute(null));
        Assert.DoesNotContain(shell.CreatePalette(">fenêtre").Items, i => i.Entry.Label.Contains("fenêtre", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Closing_a_window_releases_its_tabs_and_leaves_the_workspace()
    {
        var first = Window();
        await first.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        var second = Window();

        first.CloseWindow();

        Assert.Empty(first.Tabs);
        Assert.Equal([second], _workspace.Shells);
        // The repository can be opened again elsewhere.
        await second.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        Assert.Equal("atlas", second.SelectedTab!.Title);
    }
}
