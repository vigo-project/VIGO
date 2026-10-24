using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Vigo.App.Services;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.Git;
using Vigo.Integrations.Ai;
using Vigo.Integrations.AzureDevOps;
using Vigo.Integrations.GitHub;

namespace Vigo.App;

public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Fatal(e.Exception, "Unhandled exception on the UI thread");
    }

    public static IServiceProvider Services => ((App)Current)._host!.Services;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = BuildHost();
        // First of all: the language applies to every text loaded afterwards (ADR-075).
        var language = _host.Services.GetRequiredService<Preferences>().Current.Language;
#if DEBUG
        // Replays: VIGO_LANGUAGE=en|fr overrides the preference of this process only (the user's file is never touched).
        language = Environment.GetEnvironmentVariable("VIGO_LANGUAGE") switch { "en" => AppLanguage.English, "fr" => AppLanguage.French, _ => language };
#endif
        ResourceLocalizer.Apply(language);
        var preferences = _host.Services.GetRequiredService<Preferences>().Current;
        UiText.Apply(this, preferences.UiFontFamily, preferences.UiTextScale);
        Appearance.Current.InitializeText(preferences);
        FontCatalog.Current = new Services.SystemFontCatalog();
        Controls.CodeEditor.Logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vigo.App.Controls.CodeEditor");
        Controls.TerminalView.Logger = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vigo.App.Controls.TerminalView");
        // Pictures of issues, work items and pull requests: private ones need the account's token (GitHub, or Azure DevOps for its own hosts).
        var accounts = _host.Services.GetRequiredService<HostAccounts>();
        Controls.MarkdownView.ImageDownloader = url => accounts.DownloadImageAsync(
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Host == "dev.azure.com" || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
                ? HostingProvider.AzureDevOps
                : HostingProvider.GitHub,
            url);
        accounts.Changed += (_, _) => Controls.MarkdownView.ClearImageCache();
        // Before any view registers its shortcuts or shows their texts.
        _ = _host.Services.GetRequiredService<ShortcutMap>();
        var windows = _host.Services.GetRequiredService<WindowManager>();
        _window = windows.CreateMainWindow();
        Program.ActivationHandler = windows.ActivateAsync;
        _window.Activate();
        // The host carries no blocking service: started off the critical path of the first frame.
        _ = _host.StartAsync();
        _host.Services.GetRequiredService<AppUpdates>().StartBackgroundChecks();
    }

    /// <summary>OAuth client id passed at build time (<c>-p:VigoGitHubClientId=…</c>), then the runtime override variable; null when neither is set.</summary>
    private static string? ClientId(string metadataKey, string overrideVariable)
    {
        if (Environment.GetEnvironmentVariable(overrideVariable) is { Length: > 0 } overridden)
            return overridden;
        var built = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(App).Assembly)
            .FirstOrDefault(a => a.Key == metadataKey)?.Value;
        return string.IsNullOrWhiteSpace(built) ? null : built;
    }

    private static IHost BuildHost()
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIGO", "logs");
        // Serilog configured in code (no reflection-based configuration reading: AOT-compatible).
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(logDir, "vigo-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // "Empty" builder: no appsettings or environment variables read at startup.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddSerilog(dispose: true);
        builder.Services.AddVigoGit();
        // Dialogs, toasts, folder picker and shell are per window: created by the WindowManager (J4.7, ADR-033).
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IRecentRepositoryStore>(sp => new JsonRecentRepositoryStore(
            JsonRecentRepositoryStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonRecentRepositoryStore>>()));
        builder.Services.AddSingleton<RecentRepositories>();
        builder.Services.AddSingleton<ILayoutStore>(sp => new JsonLayoutStore(
            JsonLayoutStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonLayoutStore>>()));
        // Read synchronously when the main window is created, so that it opens at its saved place (ADR-025).
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<ILayoutStore>();
            return new LayoutState(store, store.Load());
        });
        builder.Services.AddSingleton(sp => new JsonSessionStore(
            JsonSessionStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonSessionStore>>()));
        // Branches, tags and stashes hidden from the graph, per repository (J7.17).
        builder.Services.AddSingleton(sp => new JsonHiddenRefsStore(
            JsonHiddenRefsStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonHiddenRefsStore>>()));
        // Git profiles (J6.7, ADR-051): identity, signing and folder rules in profiles.json.
        builder.Services.AddSingleton<IGitProfileStore>(sp => new JsonGitProfileStore(
            JsonGitProfileStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonGitProfileStore>>()));
        builder.Services.AddSingleton<GitProfiles>();
        builder.Services.AddSingleton<IPreferencesStore>(sp => new JsonPreferencesStore(
            JsonPreferencesStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonPreferencesStore>>()));
        builder.Services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IPreferencesStore>();
            return new Preferences(store, store.Load());
        });
        // Configurable shortcuts (J7.3, ADR-054): the map of the application is also the static ShortcutMap.Current.
        builder.Services.AddSingleton(sp => ShortcutMap.Current = new ShortcutMap(sp.GetRequiredService<Preferences>()));
        // Hosting accounts (J6.1, ADR-044): tokens in the Windows Credential Locker, names in accounts.json. The OAuth
        // device flow uses the client ids passed at build time (public, no secret); VIGO_GITHUB_CLIENT_ID /
        // VIGO_AZDO_CLIENT_ID override them at runtime; a personal token always works.
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        builder.Services.AddSingleton<IHostingClient>(sp => new GitHubClient(
            sp.GetRequiredService<HttpClient>(),
            ClientId("GitHubClientId", "VIGO_GITHUB_CLIENT_ID"),
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger("Vigo.Integrations.GitHub")));
        builder.Services.AddSingleton<IHostingClient>(sp => new AzureDevOpsClient(
            sp.GetRequiredService<HttpClient>(),
            ClientId("AzdoClientId", "VIGO_AZDO_CLIENT_ID")));
        builder.Services.AddSingleton<ITokenVault, CredentialLockerVault>();
        // AI commit message (J7.9, ADR-070): off by default; engines are a CLI on this machine or the Anthropic API.
        builder.Services.AddSingleton<ISecretVault>(sp => (CredentialLockerVault)sp.GetRequiredService<ITokenVault>());
        builder.Services.AddSingleton<IAiBackend>(new CliAiBackend());
        builder.Services.AddSingleton<IAiBackend>(sp => new AnthropicApiBackend(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ISecretVault>()));
        builder.Services.AddSingleton<CommitMessageGenerator>();
        // Integrated terminal (J7.10, ADR-071): shells behind a ConPTY pseudo console, drawn by the native LoomTerminal control (ADR-076).
        builder.Services.AddSingleton<ITerminalShellLocator, Vigo.Git.Terminal.WindowsShellLocator>();
        builder.Services.AddSingleton<TerminalService>();
        // Git tab of the terminal panel: the git.exe commands VIGO runs (read by GitCli, shown by the panel).
        builder.Services.AddSingleton<GitCommandLog>();
        builder.Services.AddSingleton<IHostAccountStore>(sp => new JsonHostAccountStore(
            JsonHostAccountStore.DefaultFilePath, sp.GetRequiredService<ILogger<JsonHostAccountStore>>()));
        builder.Services.AddSingleton<HostAccounts>();
        builder.Services.AddSingleton<IGitHttpCredentials>(sp => sp.GetRequiredService<HostAccounts>());
        builder.Services.AddSingleton<IUrlLauncher, UrlLauncher>();
        builder.Services.AddSingleton<IAppUpdater>(sp => new VelopackUpdater(() => sp.GetRequiredService<Preferences>().Current.UpdatePreReleases));
        builder.Services.AddSingleton<AppUpdates>();
        builder.Services.AddSingleton<WindowManager>();
        return builder.Build();
    }
}
