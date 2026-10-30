using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;
using Vigo.TestUtilities;

namespace Vigo.Core.Tests;

public sealed class CloneViewModelTests : IDisposable
{
    private readonly FakeGit _git = new();
    private readonly FakePicker _picker = new();
    private readonly ScriptedPrompt _prompt = new();
    private readonly InMemoryPreferencesStore _store = new();
    private readonly Preferences _preferences;

    public CloneViewModelTests() =>
        _preferences = new Preferences(_store, new UserPreferences { LastCloneParent = TestPaths.Native(@"D:\work") });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _preferences.Dispose();

    private CloneViewModel CreateSut() => new(_git, _picker, _prompt, _preferences);

    [Theory]
    [InlineData("https://github.com/atlas/payments-service.git", "payments-service")]
    [InlineData("git@github.com:atlas/api.git", "api")]
    [InlineData("https://dev.azure.com/org/project/_git/web/", "web")]
    [InlineData(@"C:\src\repo\", "repo")]
    [InlineData("  ", "")]
    [InlineData("https://host/MP-People%20-%20Portail%20IJ%20-%20Existant", "MP-People - Portail IJ - Existant")]
    [InlineData("https://host/team/My%20Repo.git", "My Repo")]
    [InlineData("https://host/team/a%2Fc", "ac")]
    [InlineData("https://host/team/100%", "100%")]
    [InlineData("https://host/team/bad%ZZname", "bad%ZZname")]
    public void Repository_name_comes_from_the_url(string url, string expected) =>
        Assert.Equal(expected, CloneViewModel.RepositoryName(url));

    [Fact, Trait("Platform", "Windows")]
    public void Repository_name_drops_the_characters_a_windows_folder_cannot_hold()
    {
        // Path.GetInvalidFileNameChars: ':' is only invalid in a Windows file name.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file name rules.");
        Assert.Equal("abc", CloneViewModel.RepositoryName("https://host/team/a%3Ab%2Fc"));
    }

    [Fact]
    public async Task Destination_follows_the_url_until_edited_and_browse_changes_the_parent()
    {
        var sut = CreateSut();
        Assert.Equal(TestPaths.Native(@"D:\work"), sut.Destination);
        Assert.False(sut.CanClone);

        sut.Url = "https://github.com/atlas/api.git";
        Assert.Equal(TestPaths.Native(@"D:\work\api"), sut.Destination);
        Assert.True(sut.CanClone);

        _picker.Next = TestPaths.Native(@"E:\src");
        await sut.BrowseCommand.ExecuteAsync(null);
        Assert.Equal(TestPaths.Native(@"E:\src\api"), sut.Destination);

        sut.Destination = TestPaths.Native(@"E:\elsewhere");
        sut.Url = "https://github.com/atlas/web.git";
        Assert.Equal(TestPaths.Native(@"E:\elsewhere"), sut.Destination);

        sut.Destination = "relative";
        Assert.False(sut.CanClone);
    }

    [Fact]
    public async Task Clone_returns_the_repository_and_remembers_the_parent_folder()
    {
        var sut = CreateSut();
        sut.Url = " https://github.com/atlas/api.git ";
        sut.Shallow = true;
        _picker.Next = TestPaths.Native(@"E:\src");
        await sut.BrowseCommand.ExecuteAsync(null);
        var dialog = new ClickingDialog();

        var cloned = await sut.RunAsync(dialog, Ct);

        Assert.Equal(TestPaths.Native(@"E:\src\api"), cloned?.WorkingDirectory);
        Assert.Equal(new CloneRequest("https://github.com/atlas/api.git", TestPaths.Native(@"E:\src\api"), RecurseSubmodules: true, Shallow: true), Assert.Single(_git.Clones));
        Assert.Equal(TestPaths.Native(@"E:\src"), _store.Saved?.LastCloneParent);
        Assert.Equal(TestPaths.Native(@"E:\src"), new CloneViewModel(_git, _picker, _prompt, _preferences).Destination);
        Assert.False(sut.IsCloning);
    }

    [Fact]
    public async Task Failure_keeps_the_dialog_open_with_the_reason()
    {
        _git.CloneResults.Enqueue(new Failure("Dépôt introuvable : vérifiez l'URL."));
        var sut = CreateSut();
        sut.Url = "https://github.com/atlas/nope.git";
        var dialog = new ClickingDialog();

        var cloned = await sut.RunAsync(dialog, Ct);

        // The fake user closes the dialog after the failed attempt.
        Assert.Null(cloned);
        Assert.Equal(1, dialog.Shown);
        Assert.Equal("Dépôt introuvable : vérifiez l'URL.", sut.ErrorMessage);
        Assert.Null(_store.Saved);
    }

    [Fact]
    public async Task Refused_clone_asks_for_credentials_then_starts_again()
    {
        _git.CloneResults.Enqueue(new AuthenticationFailed("https://example.com/team/api.git", "could not read Username",
            "https://example.com/team/api.git", "fatal: could not read Username for 'https://example.com': terminal prompts disabled"));
        _prompt.Choices.Enqueue(new UseAccessToken("camille", "tok_123"));
        var sut = CreateSut();
        sut.Url = "https://example.com/team/api.git";
        var dialog = new ClickingDialog();

        var cloned = await sut.RunAsync(dialog, Ct);

        Assert.NotNull(cloned);
        Assert.Equal([false, true], dialog.StartedAutomatically);
        var problem = Assert.Single(_prompt.Problems);
        Assert.Equal(("git clone", "origin", "example.com", AuthenticationCause.NoCredentials),
            (problem.Command, problem.RemoteName, problem.Url?.Host, problem.Cause));
        var stored = Assert.Single(_git.StoredCredentials);
        Assert.Equal(("example.com", "camille", "tok_123"), (stored.Url.Host, stored.UserName, stored.Secret));
        Assert.Equal(2, _git.Clones.Count);
    }

    [Fact]
    public async Task Switching_protocol_retries_with_the_new_url()
    {
        _git.CloneResults.Enqueue(new AuthenticationFailed(null, "Authentication failed", "https://example.com/team/api.git"));
        _prompt.Choices.Enqueue(new SwitchRemoteUrl("origin", "git@example.com:team/api.git"));
        var sut = CreateSut();
        sut.Url = "https://example.com/team/api.git";

        await sut.RunAsync(new ClickingDialog(), Ct);

        Assert.Equal(["https://example.com/team/api.git", "git@example.com:team/api.git"], _git.Clones.Select(c => c.Url));
        Assert.Equal(TestPaths.Native(@"D:\work\api"), _git.Clones[1].Directory);
    }

    [Fact]
    public async Task Canceled_authentication_reopens_the_dialog_with_the_refusal()
    {
        _git.CloneResults.Enqueue(new AuthenticationFailed(null, "Authentication failed", "https://example.com/team/api.git"));
        var sut = CreateSut();
        sut.Url = "https://example.com/team/api.git";
        var dialog = new ClickingDialog { ClicksAllowed = 1 };

        var cloned = await sut.RunAsync(dialog, Ct);

        Assert.Null(cloned);
        Assert.Equal(2, dialog.Shown);
        Assert.Equal([false, false], dialog.StartedAutomatically);
        Assert.Equal("Authentification annulée : Authentication failed", sut.ErrorMessage);
    }

    /// <summary>
    /// Fake dialog: clicks "Cloner" (or lets the view model start by itself) while clicks are allowed, then reports
    /// whether the view model closed it; the user closes it otherwise.
    /// </summary>
    private sealed class ClickingDialog : ICloneDialog
    {
        public int ClicksAllowed { get; init; } = int.MaxValue;

        public int Shown { get; private set; }

        public List<bool> StartedAutomatically { get; } = [];

        public async Task<bool> ShowAsync(CloneViewModel clone, CancellationToken cancellationToken = default)
        {
            Shown++;
            StartedAutomatically.Add(clone.StartAutomatically);
            if (Shown > ClicksAllowed) return false;
            var closed = false;
            void OnClose(object? sender, EventArgs e) => closed = true;
            clone.CloseRequested += OnClose;
            await clone.CloneCommand.ExecuteAsync(null);
            clone.CloseRequested -= OnClose;
            return closed;
        }
    }

    private sealed class FakePicker : IFolderPicker
    {
        public string? Next { get; set; }

        public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default) => Task.FromResult(Next);
    }

    private sealed class ScriptedPrompt : IAuthenticationPrompt
    {
        public Queue<AuthenticationChoice> Choices { get; } = [];

        public List<AuthenticationProblem> Problems { get; } = [];

        public Task<AuthenticationChoice?> ResolveAsync(AuthenticationProblem problem, CancellationToken cancellationToken = default)
        {
            Problems.Add(problem);
            return Task.FromResult(Choices.TryDequeue(out var choice) ? choice : (AuthenticationChoice?)null);
        }
    }
}

internal sealed class InMemoryPreferencesStore : IPreferencesStore
{
    public UserPreferences? Saved { get; private set; }

    public UserPreferences Load() => Saved ?? new UserPreferences();

    public Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        Saved = preferences;
        return Task.CompletedTask;
    }
}
