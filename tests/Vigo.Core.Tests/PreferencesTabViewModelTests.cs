using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class PreferencesTabViewModelTests : IDisposable
{
    private readonly InMemoryPreferencesStore _store = new();
    private readonly Preferences _preferences;

    public PreferencesTabViewModelTests() => _preferences = new Preferences(_store, new UserPreferences());

    public void Dispose() => _preferences.Dispose();

    [Fact]
    public void Shows_the_current_preferences_on_the_general_section()
    {
        using var sut = new PreferencesTabViewModel(_preferences);

        Assert.True(sut.IsGeneral);
        Assert.Equal(1, sut.AutoFetch?.Minutes);
        Assert.True(sut.CheckoutNewBranch);
        Assert.True(sut.IsSystemTheme);
        Assert.True(sut.IsNormal);
    }

    [Fact]
    public void Each_change_is_applied_and_saved()
    {
        using var sut = new PreferencesTabViewModel(_preferences);

        sut.AutoFetch = sut.AutoFetchOptions.Single(o => o.Minutes == 0);
        sut.CheckoutNewBranch = false;
        sut.IsDarkTheme = true;
        sut.IsCompact = true;

        var expected = new UserPreferences { AutoFetchIntervalMinutes = 0, CheckoutNewBranch = false, Theme = AppTheme.Dark, Density = Density.Compact };
        Assert.Equal(expected, _preferences.Current);
        Assert.False(sut.IsSystemTheme);
        Assert.False(sut.IsNormal);
    }

    [Fact]
    public void Editor_and_terminal_settings_are_applied_and_saved()
    {
        using var sut = new PreferencesTabViewModel(_preferences);
        Assert.Equal(UserPreferences.DefaultEditorFontSize, sut.EditorFontSize);
        Assert.False(sut.EditorLigatures);
        Assert.True(sut.TerminalDimWhenUnfocused);

        sut.EditorFontSize = 14;
        sut.EditorTabSize = 2;
        sut.EditorEol = sut.EndOfLineOptions.Single(o => o.Value == EditorEndOfLine.Crlf);
        sut.EditorWordWrap = true;
        sut.EditorLigatures = true;
        sut.EditorSyntaxHighlighting = false;
        sut.EditorLineNumbers = false;
        sut.TerminalFontSize = 15;
        sut.TerminalLineHeight = 1.4;
        sut.TerminalCursor = sut.CursorOptions.Single(o => o.Value == TerminalCursorStyle.Block);
        sut.TerminalDimWhenUnfocused = false;
        sut.UiFont = new FontOption("Consolas", "Consolas");
        Assert.Equal(100, sut.UiTextScale?.Percent);
        sut.UiTextScale = sut.UiTextScaleOptions.Single(o => o.Percent == 125);

        var current = _preferences.Current;
        Assert.Equal(125, current.UiTextScalePercent);
        Assert.Equal(1.25, current.UiTextScale);
        Assert.Equal(new EditorSettings(null, 14, 2, EditorEndOfLine.Crlf, false, false, true, true), current.Editor);
        Assert.Equal(new TerminalSettings(null, 15, 1.4, TerminalCursorStyle.Block, false), current.Terminal);
        Assert.Equal("Consolas", current.UiFontFamily);
        Assert.True(sut.UiFontNeedsRestart);
    }

    [Fact]
    public void Text_settings_are_brought_back_into_their_range()
    {
        var p = new UserPreferences { EditorFontSize = 500, EditorTabSize = 0, TerminalFontSize = 1, TerminalLineHeight = double.NaN, EditorFontFamily = "  " }.Normalize();

        Assert.Equal(200, new UserPreferences { UiTextScalePercent = 900 }.Normalize().UiTextScalePercent);
        Assert.Equal(32, p.EditorFontSize);
        Assert.Equal(1, p.EditorTabSize);
        Assert.Equal(8, p.TerminalFontSize);
        Assert.Equal(UserPreferences.DefaultTerminalLineHeight, p.TerminalLineHeight);
        Assert.Null(p.EditorFontFamily);
    }

    [Fact]
    public async Task Follows_changes_made_elsewhere_and_lists_a_hand_set_interval()
    {
        using var sut = new PreferencesTabViewModel(_preferences);

        await _preferences.UpdateAsync(p => p with { Theme = AppTheme.Light, AutoFetchIntervalMinutes = 3 }, TestContext.Current.CancellationToken);

        Assert.True(sut.IsLightTheme);
        Assert.Equal(3, sut.AutoFetch?.Minutes);
        Assert.Equal([0, 1, 2, 3, 5, 10, 15, 30, 60], sut.AutoFetchOptions.Select(o => o.Minutes));
    }

    [Fact]
    public void Sections_of_later_milestones_cannot_be_shown()
    {
        using var sut = new PreferencesTabViewModel(_preferences);

        sut.Show(PreferencesSection.Profiles);
        Assert.True(sut.IsGeneral);
        Assert.Equal("Disponible au jalon J6", sut.Sections.Single(s => s.Section == PreferencesSection.Profiles).Tooltip);

        sut.Show(PreferencesSection.Appearance);
        Assert.True(sut.IsAppearance);
    }

    [Fact]
    public void Shell_opens_a_single_preferences_tab_and_toggles_theme_and_density()
    {
        var shell = new ShellViewModel(new FakeGit(), new NullPicker(), new NullNotifier(),
            new RecentRepositories(new InMemoryRecentRepositoryStore(), TimeProvider.System), TimeProvider.System,
            NullLogger<ShellViewModel>.Instance, preferences: _preferences);

        shell.OpenPreferencesCommand.Execute(null);
        shell.NewTabCommand.Execute(null);
        shell.OpenPreferencesCommand.Execute(null);
        Assert.Single(shell.Tabs.OfType<PreferencesTabViewModel>());
        Assert.IsType<PreferencesTabViewModel>(shell.SelectedTab);

        shell.IsDarkThemeShown = false;
        shell.ToggleThemeCommand.Execute(null);
        Assert.Equal(AppTheme.Dark, _preferences.Current.Theme);
        shell.ToggleDensityCommand.Execute(null);
        Assert.Equal(Density.Compact, _preferences.Current.Density);

        var palette = shell.CreatePalette(">compact");
        Assert.Equal("Basculer le mode compact", Assert.Single(palette.Items).Entry.Label);
    }

    [Fact]
    public async Task Theme_and_density_survive_the_preferences_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vigo-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonPreferencesStore(Path.Combine(directory, "preferences.json"), NullLogger<JsonPreferencesStore>.Instance);
            var saved = new UserPreferences { Theme = AppTheme.Dark, Density = Density.Compact, CheckoutNewBranch = false };
            await store.SaveAsync(saved, TestContext.Current.CancellationToken);

            Assert.Equal(saved, store.Load());
            Assert.Contains("\"compact\"", await File.ReadAllTextAsync(Path.Combine(directory, "preferences.json"), TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Density_gives_the_mockup_sizes()
    {
        Assert.Equal(28, DensityMetrics.For(Density.Normal).RowHeight);
        Assert.Equal(22, DensityMetrics.For(Density.Compact).RowHeight);
        Assert.False(DensityMetrics.For(Density.Compact).ShowToolbarLabels);
    }

    private sealed class NullPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class NullNotifier : IUserNotifier
    {
        public void Notify(NotificationSeverity severity, string title, string? message = null)
        {
        }
    }
}

public sealed class AiPreferencesTests : IDisposable
{
    private readonly Preferences _preferences = new(new InMemoryPreferencesStore(), new UserPreferences());
    private readonly FakeGit _git = new();

    public void Dispose() => _preferences.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NoVault : ISecretVault
    {
        public string? Retrieve(string name) => null;

        public void Store(string name, string secret) { }

        public void Remove(string name) { }
    }

    private PreferencesTabViewModel Create() =>
        new(_preferences, commitMessages: new CommitMessageGenerator(_preferences, _git, new NoVault(), []));

    [Fact]
    public async Task The_cli_engine_has_a_tool_choice_and_each_tool_keeps_its_own_command()
    {
        using var sut = Create();
        Assert.Equal([AiProvider.Off, AiProvider.Cli, AiProvider.Api], sut.AiProviderOptions.Select(o => o.Provider));
        Assert.Equal([AiCliTool.Claude, AiCliTool.Copilot], sut.AiCliToolOptions.Select(o => o.Tool));

        sut.AiProviderChoice = sut.AiProviderOptions.Single(o => o.Provider == AiProvider.Cli);
        Assert.True(sut.IsAiCli);
        Assert.Equal(AiCliTool.Claude, sut.AiCliToolChoice!.Tool);
        Assert.Equal(AiSettings.DefaultCliCommand, sut.AiCliCommandPlaceholder);
        sut.AiCliCommandText = "claude -p --max-turns 1";

        sut.AiCliToolChoice = sut.AiCliToolOptions.Single(o => o.Tool == AiCliTool.Copilot);
        Assert.Equal(string.Empty, sut.AiCliCommandText);
        Assert.Equal(AiSettings.DefaultCopilotCommand, sut.AiCliCommandPlaceholder);
        sut.AiCliCommandText = "copilot -p {prompt} -s";

        Assert.Equal(AiCliTool.Copilot, _preferences.Current.AiCliTool);
        Assert.Equal("claude -p --max-turns 1", _preferences.Current.AiCliCommand);
        Assert.Equal("copilot -p {prompt} -s", _preferences.Current.AiCopilotCommand);

        sut.AiCliToolChoice = sut.AiCliToolOptions.Single(o => o.Tool == AiCliTool.Claude);
        Assert.Equal("claude -p --max-turns 1", sut.AiCliCommandText);
        await Task.CompletedTask;
    }

    [Fact]
    public void The_tool_and_command_are_hidden_for_the_api_engine()
    {
        using var sut = Create();

        sut.AiProviderChoice = sut.AiProviderOptions.Single(o => o.Provider == AiProvider.Api);

        Assert.False(sut.IsAiCli);
        Assert.True(sut.IsAiApi);
    }
}
