using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class ShellViewModelTests
{
    private sealed class FakePicker : IFolderPicker
    {
        public string? Next { get; set; }
        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) => Task.FromResult(Next);
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title)> Items { get; } = [];
        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title));
    }

    private readonly FakeGit _git = new();
    private readonly FakePicker _picker = new();
    private readonly RecordingNotifier _notifier = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryRecentRepositoryStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private RecentRepositories? _recent;

    private RecentRepositories Recent => _recent ??= new RecentRepositories(_store, _time);

    private ShellViewModel CreateSut() => new(_git, _picker, _notifier, Recent, _time, NullLogger<ShellViewModel>.Instance);

    private static RepositoryInfo Repo(string name) =>
        new($@"C:\src\{name}", $@"C:\src\{name}\.git", name, "main", new string('a', 40), false, false, false);

    private sealed class MemoryProfileStore : IGitProfileStore
    {
        public GitProfileSet Load() => GitProfileSet.Empty;

        public Task SaveAsync(GitProfileSet profiles, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Switching_profile_goes_through_the_workspace_and_lists_the_choices()
    {
        using var profiles = new GitProfiles(new MemoryProfileStore());
        var work = profiles.Add("Work", "A", "a@x");
        var perso = profiles.Add("Perso", "B", "b@x");
        string? requested = null;
        var workspace = new ShellWorkspace { SwitchProfile = id => { requested = id; return Task.CompletedTask; } };
        var sut = new ShellViewModel(_git, _picker, _notifier, Recent, _time, NullLogger<ShellViewModel>.Instance, workspace: workspace, profiles: profiles);

        await sut.SwitchProfileAsync(perso.Id);

        Assert.Equal(perso.Id, requested);
        Assert.Equal([(work.Id, "Work", true, (string?)null), (perso.Id, "Perso", false, (string?)null)], sut.ProfileChoices);
    }

    [Fact]
    public async Task Closing_repository_tabs_keeps_a_welcome_tab()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        var sut = CreateSut();
        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);

        sut.CloseRepositoryTabs();

        Assert.IsType<WelcomeTabViewModel>(Assert.Single(sut.Tabs));
        Assert.Same(sut.Tabs[0], sut.SelectedTab);
    }

    [Fact]
    public void Starts_with_a_single_welcome_tab_selected()
    {
        var sut = CreateSut();

        Assert.IsType<WelcomeTabViewModel>(Assert.Single(sut.Tabs));
        Assert.Same(sut.Tabs[0], sut.SelectedTab);
    }

    [Fact]
    public async Task Opening_a_repository_replaces_the_welcome_tab()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        var sut = CreateSut();

        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);

        Assert.Equal("atlas", Assert.IsType<RepositoryTabViewModel>(Assert.Single(sut.Tabs)).Title);
        Assert.Same(sut.Tabs[0], sut.SelectedTab);
    }

    [Fact]
    public async Task Opening_an_already_open_repository_activates_its_tab()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        _git.Repos[@"C:\src\docs"] = new Success<RepositoryInfo>(Repo("docs"));
        var sut = CreateSut();
        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        sut.NewTabCommand.Execute(null);
        await sut.OpenRepositoryAsync(@"C:\src\docs", Ct);

        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);

        Assert.Equal(2, sut.Tabs.Count);
        Assert.Equal("atlas", sut.SelectedTab!.Title);
    }

    [Fact]
    public async Task Failure_notifies_the_user_and_keeps_tabs()
    {
        var sut = CreateSut();

        await sut.OpenRepositoryAsync(@"C:\nope", Ct);

        Assert.Equal(NotificationSeverity.Error, Assert.Single(_notifier.Items).Severity);
        Assert.IsType<WelcomeTabViewModel>(Assert.Single(sut.Tabs));
    }

    [Fact]
    public void Closing_the_last_tab_reopens_welcome()
    {
        var sut = CreateSut();

        sut.CloseTabCommand.Execute(sut.Tabs[0]);

        Assert.IsType<WelcomeTabViewModel>(Assert.Single(sut.Tabs));
    }

    [Fact]
    public async Task Welcome_open_command_uses_folder_picker()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        _picker.Next = @"C:\src\atlas";
        var sut = CreateSut();
        var welcome = (WelcomeTabViewModel)sut.Tabs[0];

        await welcome.OpenCommand.ExecuteAsync(null);

        Assert.IsType<RepositoryTabViewModel>(sut.SelectedTab);
    }

    [Fact]
    public async Task Welcome_load_resolves_identity()
    {
        var welcome = (WelcomeTabViewModel)CreateSut().Tabs[0];

        await welcome.LoadAsync(Ct);

        Assert.Equal("AL", welcome.Identity!.Initials);
    }

    [Fact]
    public async Task Opening_a_repository_records_it_in_the_recent_list()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        var sut = CreateSut();

        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);

        Assert.Equal(@"C:\src\atlas", Assert.Single(_store.Saved).Path);
    }

    [Fact]
    public async Task Welcome_lists_filters_and_opens_recent_repositories()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        _store.Saved =
        [
            new(@"C:\src\atlas", "atlas", "main", _time.Now.AddHours(-1), false),
            new(@"D:\work\docs", "docs", "content/v2", _time.Now.AddDays(-1), true),
        ];
        var sut = CreateSut();
        var welcome = (WelcomeTabViewModel)sut.Tabs[0];
        Assert.True(welcome.HasNoRecents);

        await welcome.LoadAsync(Ct);

        Assert.False(welcome.HasNoRecents);
        Assert.Equal(["docs", "atlas"], welcome.Recents.Select(r => r.Name));
        Assert.Equal("il y a 1 h", welcome.Recents[1].LastOpenedDisplay);

        welcome.RecentFilter = "work";
        Assert.Equal("docs", Assert.Single(welcome.Recents).Name);
        welcome.RecentFilter = "nothing";
        Assert.Empty(welcome.Recents);
        Assert.True(welcome.HasNoMatch);
        welcome.RecentFilter = "";

        await welcome.OpenRecentCommand.ExecuteAsync(welcome.Recents[1]);

        Assert.Equal("atlas", Assert.IsType<RepositoryTabViewModel>(Assert.Single(sut.Tabs)).Title);
    }

    [Fact]
    public async Task Welcome_pins_and_removes_recent_repositories()
    {
        _store.Saved =
        [
            new(@"C:\src\atlas", "atlas", "main", _time.Now.AddHours(-1), false),
            new(@"D:\work\docs", "docs", "main", _time.Now.AddDays(-1), false),
        ];
        var welcome = (WelcomeTabViewModel)CreateSut().Tabs[0];
        await welcome.LoadAsync(Ct);

        await welcome.TogglePinCommand.ExecuteAsync(welcome.Recents[1]);
        Assert.Equal(["docs", "atlas"], welcome.Recents.Select(r => r.Name));
        Assert.True(welcome.Recents[0].IsPinned);

        await welcome.RemoveRecentCommand.ExecuteAsync(welcome.Recents[1]);
        Assert.Equal("docs", Assert.Single(welcome.Recents).Name);
        Assert.Equal("docs", Assert.Single(_store.Saved).Name);
    }

    [Fact]
    public async Task Replaced_welcome_tab_stops_listening_to_the_recent_list()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        var sut = CreateSut();
        var welcome = (WelcomeTabViewModel)sut.Tabs[0];

        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);

        Assert.Empty(welcome.Recents);
    }

    [Fact]
    public async Task Session_captures_repository_tabs_in_order_with_the_selected_one()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        _git.Repos[@"C:\src\billing"] = new Success<RepositoryInfo>(Repo("billing"));
        var sut = CreateSut();
        await sut.OpenRepositoryAsync(@"C:\src\atlas", Ct);
        await sut.OpenRepositoryAsync(@"C:\src\billing", Ct);
        sut.SelectedTab = sut.Tabs[0];

        var session = sut.CaptureSession();
        Assert.Equal([@"C:\src\atlas", @"C:\src\billing"], session.Repositories);
        Assert.Equal(0, session.SelectedIndex);

        sut.NewTabCommand.Execute(null);
        Assert.Equal(-1, sut.CaptureSession().SelectedIndex);
    }

    [Fact]
    public async Task Restoring_reopens_tabs_selects_the_saved_one_and_reports_missing_repositories()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        _git.Repos[@"C:\src\billing"] = new Success<RepositoryInfo>(Repo("billing"));
        var sut = CreateSut();

        await sut.RestoreSessionAsync(new SessionWindow([@"C:\src\atlas", @"C:\src\gone", @"C:\src\billing"], 2), Ct);

        Assert.Equal(["atlas", "billing"], sut.Tabs.Select(t => t.Title));
        Assert.Same(sut.Tabs[1], sut.SelectedTab);
        Assert.Equal((NotificationSeverity.Warning, "Un dépôt de la session précédente est introuvable"), Assert.Single(_notifier.Items));
        // Reopening is not a new use: the recent list is untouched.
        Assert.Empty(Recent.Items);
    }

    [Fact]
    public async Task A_repository_that_could_not_be_reopened_stays_in_the_captured_session()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        var sut = CreateSut();

        await sut.RestoreSessionAsync(new SessionWindow([@"C:\src\gone", @"C:\src\atlas"], 1), Ct);

        var session = sut.CaptureSession();
        Assert.Equal([@"C:\src\atlas", @"C:\src\gone"], session.Repositories);
        Assert.Equal(0, session.SelectedIndex);
    }

    [Fact]
    public async Task A_repository_missing_again_at_the_second_launch_is_forgotten_without_a_warning()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        var first = CreateSut();
        await first.RestoreSessionAsync(new SessionWindow([@"C:\src\atlas", @"C:\src\gone"], 0), Ct);
        var captured = first.CaptureSession();
        Assert.Equal([@"C:\src\gone"], captured.Unrestored);
        Assert.Single(_notifier.Items);

        _notifier.Items.Clear();
        var second = CreateSut();
        await second.RestoreSessionAsync(captured, Ct);

        Assert.Empty(_notifier.Items);
        var again = second.CaptureSession();
        Assert.Equal([@"C:\src\atlas"], again.Repositories);
        Assert.Empty(again.Unrestored!);
    }

    [Fact]
    public async Task A_repository_found_again_is_no_longer_marked()
    {
        _git.Repos[@"C:\src\gone"] = new Success<RepositoryInfo>(Repo("gone"));
        var sut = CreateSut();

        await sut.RestoreSessionAsync(new SessionWindow([@"C:\src\gone"], 0, Unrestored: [@"C:\src\gone"]), Ct);

        var session = sut.CaptureSession();
        Assert.Equal([@"C:\src\gone"], session.Repositories);
        Assert.Empty(session.Unrestored!);
    }

    [Fact]
    public async Task Restoring_stops_adding_tabs_once_canceled()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        _git.Repos[@"C:\src\billing"] = new Success<RepositoryInfo>(Repo("billing"));
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        // The window closes while git opens the first repository.
        _git.OnOpen = _ => cts.Cancel();

        await sut.RestoreSessionAsync(new SessionWindow([@"C:\src\atlas", @"C:\src\billing"]), cts.Token);

        Assert.DoesNotContain(sut.Tabs, t => t is RepositoryTabViewModel);
    }

    [Fact]
    public async Task Restoring_with_the_welcome_tab_selected_keeps_a_welcome_tab()
    {
        _git.Repos[@"C:\src\atlas"] = new Success<RepositoryInfo>(Repo("atlas"));
        var sut = CreateSut();

        await sut.RestoreSessionAsync(new SessionWindow([@"C:\src\atlas"]), Ct);

        Assert.Equal(["atlas", "Accueil"], sut.Tabs.Select(t => t.Title));
        Assert.IsType<WelcomeTabViewModel>(sut.SelectedTab);
    }
}

public sealed class JsonSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vigo-session-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "session.json");

    private JsonSessionStore Store() => new(FilePath, NullLogger<JsonSessionStore>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Sessions_are_saved_per_profile()
    {
        await Store().SaveAsync("default", new SessionSettings([new SessionWindow([@"C:\a", @"C:\b"], 1)]), Ct);
        await Store().SaveAsync("work", new SessionSettings([new SessionWindow([@"C:\w"])]), Ct);

        var session = await Store().LoadAsync("default", Ct);

        var window = Assert.Single(session.Windows);
        Assert.Equal([@"C:\a", @"C:\b"], window.Repositories);
        Assert.Equal(1, window.SelectedIndex);
        Assert.Equal([@"C:\w"], Assert.Single((await Store().LoadAsync("work", Ct)).Windows).Repositories);
        Assert.Empty((await Store().LoadAsync("other", Ct)).Windows);
    }

    [Fact]
    public async Task Missing_corrupt_or_inconsistent_files_give_an_empty_or_cleaned_session()
    {
        Assert.Empty((await Store().LoadAsync("default", Ct)).Windows);

        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{ not json", Ct);
        Assert.Empty((await Store().LoadAsync("default", Ct)).Windows);

        await File.WriteAllTextAsync(FilePath,
            """{ "version": 1, "profiles": { "default": { "windows": [ { "repositories": [ "", "C:\\a" ], "selectedIndex": 5 }, { "repositories": [] } ] } } }""", Ct);
        var window = Assert.Single((await Store().LoadAsync("default", Ct)).Windows);
        Assert.Equal([@"C:\a"], window.Repositories);
        Assert.Equal(-1, window.SelectedIndex);

        await File.WriteAllTextAsync(FilePath, """{ "version": 99, "profiles": {} }""", Ct);
        Assert.Empty((await Store().LoadAsync("default", Ct)).Windows);
    }

    [Fact]
    public async Task Each_window_keeps_its_placement_and_empty_bounds_are_dropped()
    {
        await Store().SaveAsync("default", new SessionSettings(
        [
            new SessionWindow([@"C:\a"], 0, new WindowBounds(10, 20, 1400, 900), IsMaximized: true),
            new SessionWindow([@"C:\b"], -1, new WindowBounds(0, 0, 0, 0)),
        ]), Ct);

        var windows = (await Store().LoadAsync("default", Ct)).Windows;

        Assert.Equal(2, windows.Count);
        Assert.Equal(new WindowBounds(10, 20, 1400, 900), windows[0].Bounds);
        Assert.True(windows[0].IsMaximized);
        Assert.Null(windows[1].Bounds);
        Assert.False(windows[1].IsMaximized);
    }

    [Fact]
    public async Task Unrestored_marks_are_kept_only_for_paths_still_in_the_window()
    {
        await Store().SaveAsync("default", new SessionSettings(
            [new SessionWindow([@"C:\a", @"C:\b"], 0, Unrestored: [@"C:\B", @"C:\stale"])]), Ct);

        var window = Assert.Single((await Store().LoadAsync("default", Ct)).Windows);

        Assert.Equal([@"C:\B"], window.Unrestored);
    }
}
