using System.Threading.Channels;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class TerminalTests : IDisposable
{
    private static readonly TerminalShell Pwsh = new(TerminalShellIds.PowerShell7, "PowerShell 7", "pwsh.exe -NoLogo");
    private static readonly TerminalShell Cmd = new(TerminalShellIds.Cmd, "Invite de commandes", "cmd.exe");
    private static readonly TerminalShell Ubuntu = new(TerminalShellIds.WslPrefix + "Ubuntu", "WSL : Ubuntu", "wsl.exe -d Ubuntu");

    private readonly InMemoryPreferencesStore _store = new();
    private readonly Preferences _preferences;

    public TerminalTests() => _preferences = new Preferences(_store, new UserPreferences());

    public void Dispose() => _preferences.Dispose();

    private TerminalService Service(params TerminalShell[] shells) => new(new FixedShells(shells), new NoSessions(), _preferences);

    private sealed class FixedShells(TerminalShell[] shells) : ITerminalShellLocator
    {
        public IReadOnlyList<TerminalShell> Detect() => shells;
    }

    private sealed class NoSessions : ITerminalSessionFactory
    {
        public ITerminalSession Start(string commandLine, string workingDirectory, int columns, int rows) => throw new NotSupportedException();
    }

    private sealed class RecordingNotifier : IUserNotifier
    {
        public List<(NotificationSeverity Severity, string Title)> Items { get; } = [];

        public void Notify(NotificationSeverity severity, string title, string? message = null) => Items.Add((severity, title));
    }

    [Fact]
    public void Toggle_opens_the_panel_with_a_first_terminal_in_the_repository_folder()
    {
        var sut = new TerminalPanelViewModel(Service(Pwsh, Cmd), @"D:\src\atlas-api", "atlas-api");

        sut.ToggleCommand.Execute(null);

        Assert.True(sut.IsOpen);
        var tab = Assert.Single(sut.Tabs);
        Assert.Same(tab, sut.SelectedTab);
        Assert.True(tab.IsSelected);
        Assert.Equal("PowerShell 7 · atlas-api", tab.Title);
        Assert.Equal(@"D:\src\atlas-api", tab.WorkingDirectory);
        Assert.Equal("pwsh.exe -NoLogo", tab.CommandLine);
    }

    [Fact]
    public void Toggle_again_hides_the_panel_and_keeps_the_terminals()
    {
        var sut = new TerminalPanelViewModel(Service(Pwsh), "d", "r");
        sut.ToggleCommand.Execute(null);

        sut.ToggleCommand.Execute(null);

        Assert.False(sut.IsOpen);
        Assert.Single(sut.Tabs);

        sut.ToggleCommand.Execute(null);

        Assert.True(sut.IsOpen);
        Assert.Single(sut.Tabs);
    }

    [Fact]
    public void A_new_terminal_is_selected_and_the_previous_one_is_not()
    {
        var sut = new TerminalPanelViewModel(Service(Pwsh, Cmd), "d", "r");
        sut.ToggleCommand.Execute(null);
        var first = sut.Tabs[0];

        sut.NewTabCommand.Execute(Cmd);

        Assert.Equal(2, sut.Tabs.Count);
        Assert.Same(sut.Tabs[1], sut.SelectedTab);
        Assert.False(first.IsSelected);
        Assert.True(sut.Tabs[1].IsSelected);
        Assert.Equal("Invite de commandes · r", sut.Tabs[1].Title);
    }

    [Fact]
    public void Closing_the_selected_terminal_selects_a_neighbour_and_closing_the_last_hides_the_panel()
    {
        var sut = new TerminalPanelViewModel(Service(Pwsh, Cmd), "d", "r");
        sut.ToggleCommand.Execute(null);
        sut.NewTabCommand.Execute(Cmd);
        var first = sut.Tabs[0];
        var second = sut.Tabs[1];

        sut.CloseTabCommand.Execute(second);

        Assert.Same(first, sut.SelectedTab);
        Assert.True(sut.IsOpen);

        sut.CloseTabCommand.Execute(first);

        Assert.Empty(sut.Tabs);
        Assert.Null(sut.SelectedTab);
        Assert.False(sut.IsOpen);
    }

    [Fact]
    public void Without_a_shell_the_panel_stays_closed_and_says_why()
    {
        var notifier = new RecordingNotifier();
        var sut = new TerminalPanelViewModel(Service(), "d", "r", notifier);

        sut.ToggleCommand.Execute(null);

        Assert.False(sut.IsOpen);
        Assert.Empty(sut.Tabs);
        Assert.Equal(NotificationSeverity.Warning, Assert.Single(notifier.Items).Severity);
    }

    [Fact]
    public void Without_the_terminal_service_nothing_is_offered()
    {
        var sut = new TerminalPanelViewModel(null, "d", "r");

        sut.ToggleCommand.Execute(null);

        Assert.False(sut.IsAvailable);
        Assert.False(sut.IsOpen);
        Assert.Null(sut.Sessions);
    }

    [Fact]
    public async Task The_default_shell_comes_from_the_preferences_and_only_it_takes_the_custom_command()
    {
        var service = Service(Pwsh, Cmd, Ubuntu);
        Assert.Equal(Pwsh, service.DefaultShell);

        await _preferences.UpdateAsync(p => p with { TerminalShell = Cmd.Id, TerminalCommand = "cmd.exe /k title VIGO" }, TestContext.Current.CancellationToken);

        Assert.Equal(Cmd, service.DefaultShell);
        Assert.Equal("cmd.exe /k title VIGO", service.CommandLineOf(Cmd));
        Assert.Equal("pwsh.exe -NoLogo", service.CommandLineOf(Pwsh));
    }

    [Fact]
    public async Task A_default_shell_that_is_gone_falls_back_to_the_first_one()
    {
        await _preferences.UpdateAsync(p => p with { TerminalShell = "git-bash" }, TestContext.Current.CancellationToken);

        Assert.Equal(Pwsh, Service(Pwsh, Cmd).DefaultShell);
    }

    [Fact]
    public async Task Preferences_list_the_shells_and_save_the_choice_and_the_command()
    {
        using var sut = new PreferencesTabViewModel(_preferences, terminal: Service(Pwsh, Cmd));

        Assert.True(sut.HasTerminal);
        Assert.Equal([Pwsh, Cmd], sut.TerminalShellOptions);
        Assert.Equal(Pwsh, sut.TerminalShellChoice);
        Assert.Equal("pwsh.exe -NoLogo", sut.TerminalCommandPlaceholder);

        sut.TerminalShellChoice = Cmd;
        Assert.Equal(Cmd.Id, _preferences.Current.TerminalShell);
        Assert.Equal("cmd.exe", sut.TerminalCommandPlaceholder);

        sut.TerminalCommandText = "cmd.exe /k cls";
        await _preferences.UpdateAsync(p => p, TestContext.Current.CancellationToken);
        Assert.Equal("cmd.exe /k cls", _preferences.Current.TerminalCommand);

        // Another default shell starts with its own command.
        sut.TerminalShellChoice = Pwsh;
        Assert.Null(_preferences.Current.TerminalCommand);
        Assert.Equal(string.Empty, sut.TerminalCommandText);
    }

    [Fact]
    public void Preferences_hide_the_terminal_section_without_the_service()
    {
        using var sut = new PreferencesTabViewModel(_preferences);

        Assert.False(sut.HasTerminal);
    }

    [Fact]
    public void The_terminal_toggle_has_a_valid_default_that_collides_with_nothing()
    {
        var definition = ShortcutCatalog.Find(ShortcutIds.Terminal);

        Assert.NotNull(definition);
        Assert.True(definition.Default.IsValid);
        Assert.Equal(ShortcutScope.Repository, definition.Scope);
        Assert.Equal("Ctrl+`", definition.Default.ToString());
    }

    [Fact]
    public void Terminal_height_is_kept_within_its_limits_and_leaves_room_for_the_graph()
    {
        // The store normalizes what it reads (hand-edited file).
        var state = new LayoutState(new MemoryLayoutStore(), new LayoutSettings { TerminalHeight = 5_000 }.Normalize());

        Assert.Equal(LayoutLimits.Terminal.Maximum, state.TerminalHeight);
        state.TerminalHeight = 10;
        Assert.Equal(LayoutLimits.Terminal.Minimum, state.TerminalHeight);

        state.TerminalHeight = 500;
        Assert.Equal(500, state.FitTerminal(1000));
        // 500 + 160 for the graph does not fit in 600: the saved height is kept, only the shown one shrinks.
        Assert.Equal(440, state.FitTerminal(600));
        Assert.Equal(LayoutLimits.Terminal.Minimum, state.FitTerminal(200));
        Assert.Equal(500, state.TerminalHeight);
        Assert.Equal(500, state.ToSettings().TerminalHeight);
    }

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        public LayoutSettings Load() => new();

        public Task SaveAsync(LayoutSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
