using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Git;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class TabProjectsTests : IDisposable
{
    private readonly FakeGit _git = new();
    private readonly InMemoryRecentRepositoryStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly ShellWorkspace _workspace = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vigo-projects-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ShellViewModel CreateShell() => new(
        _git, new NullPicker(), new NullNotifier(), new RecentRepositories(_store, _time), _time, NullLogger<ShellViewModel>.Instance,
        workspace: _workspace);

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

    private async Task<ShellViewModel> OpenAsync(params string[] names)
    {
        var shell = CreateShell();
        foreach (var name in names)
        {
            _git.Repos[$@"C:\src\{name}"] = new Success<RepositoryInfo>(
                new($@"C:\src\{name}", $@"C:\src\{name}\.git", name, "main", new string('a', 40), false, false, false));
            await shell.OpenRepositoryAsync($@"C:\src\{name}", Ct);
        }
        return shell;
    }

    [Fact]
    public void A_repository_belongs_to_one_project_and_new_projects_take_free_colors()
    {
        var projects = new TabProjects();
        var atlas = projects.Create("Atlas");
        var borealis = projects.Create("Borealis");
        Assert.Equal([0, 1], [atlas.ColorIndex, borealis.ColorIndex]);

        projects.Assign(@"C:\src\a", atlas.Id);
        projects.Assign(@"C:\SRC\A", borealis.Id);

        Assert.Empty(projects.Find(atlas.Id)!.Repositories);
        Assert.Equal(borealis.Id, projects.ProjectOf(@"C:\src\a")?.Id);
        projects.Unassign(@"C:\src\a");
        Assert.Null(projects.ProjectOf(@"C:\src\a"));
    }

    [Fact]
    public void Update_and_remove_and_name_check()
    {
        var projects = new TabProjects();
        var atlas = projects.Create("Atlas", 3);
        projects.Update(atlas.Id, "  Atlas 2 ", 13);
        projects.Update(atlas.Id, "   ");

        Assert.Equal(("Atlas 2", 3), (projects.Projects[0].Name, projects.Projects[0].ColorIndex));
        Assert.True(projects.IsNameTaken("atlas 2"));
        Assert.False(projects.IsNameTaken("atlas 2", atlas.Id));

        projects.Remove(atlas.Id);
        Assert.Empty(projects.Projects);
    }

    [Fact]
    public void A_chosen_color_replaces_the_default_one_until_reset()
    {
        var projects = new TabProjects();
        var atlas = projects.Create("Atlas");
        Assert.Null(atlas.Color);

        projects.Update(atlas.Id, color: "#a1b2c3");
        Assert.Equal(("#A1B2C3", 0), (projects.Projects[0].Color, projects.Projects[0].ColorIndex));

        projects.Update(atlas.Id, "Atlas 2");
        Assert.Equal("#A1B2C3", projects.Projects[0].Color);

        projects.Update(atlas.Id, color: "not a color");
        Assert.Null(projects.Projects[0].Color);
    }

    [Fact]
    public async Task A_chosen_color_is_saved_with_the_session_and_reaches_the_tabs()
    {
        var store = new JsonSessionStore(Path.Combine(_directory, "session.json"), NullLogger<JsonSessionStore>.Instance);
        await store.SaveAsync("default", new SessionSettings([new SessionWindow([@"C:\a"])], [new TabProject("p", "Atlas", 4, [@"C:\a"], "#102030")]), Ct);

        var loaded = Assert.Single((await store.LoadAsync("default", Ct)).Projects!);
        Assert.Equal("#102030", loaded.Color);

        var projects = new TabProjects();
        projects.Load([loaded with { Color = "zzz" }]);
        Assert.Null(projects.Projects[0].Color);
    }

    [Fact]
    public void Load_drops_unusable_entries_and_duplicate_repositories()
    {
        var projects = new TabProjects();
        projects.Load(
        [
            new TabProject("1", "A", -1, [@"C:\a", "", @"C:\b"]),
            new TabProject("1", "dup id", 0, []),
            new TabProject("2", " ", 0, []),
            new TabProject("3", "C", 2, [@"C:\B"]),
        ]);

        Assert.Equal(["A", "C"], projects.Projects.Select(p => p.Name));
        Assert.Equal(9, projects.Projects[0].ColorIndex);
        Assert.Equal([@"C:\a", @"C:\b"], projects.Projects[0].Repositories);
        Assert.Empty(projects.Projects[1].Repositories);
    }

    [Fact]
    public async Task Projects_are_saved_with_the_session()
    {
        var store = new JsonSessionStore(Path.Combine(_directory, "session.json"), NullLogger<JsonSessionStore>.Instance);
        await store.SaveAsync("default", new SessionSettings([new SessionWindow([@"C:\a"])], [new TabProject("p", "Atlas", 4, [@"C:\a"])]), Ct);

        var project = Assert.Single((await store.LoadAsync("default", Ct)).Projects!);

        Assert.Equal(("Atlas", 4), (project.Name, project.ColorIndex));
        Assert.Equal([@"C:\a"], project.Repositories);
        Assert.Empty((await store.LoadAsync("other", Ct)).Windows);
    }

    [Fact]
    public async Task Assigning_colors_the_tab_and_gathers_the_tabs_of_the_project()
    {
        var shell = await OpenAsync("a", "b", "c");
        var project = _workspace.Projects.Create("Atlas");

        shell.AssignToProject(shell.Tabs[2], project.Id);
        shell.AssignToProject(shell.Tabs[0], project.Id);

        Assert.Equal(["a", "c", "b"], shell.Tabs.Select(t => t.Title));
        Assert.Equal([project.ColorIndex, project.ColorIndex, -1], shell.Tabs.Select(t => t.ProjectColorIndex));
        Assert.Equal("Atlas", shell.Tabs[0].ProjectName);

        shell.RemoveFromProject(shell.Tabs[0]);
        Assert.Equal(-1, shell.Tabs[0].ProjectColorIndex);
        Assert.Null(shell.Tabs[0].ProjectName);
    }

    [Fact]
    public async Task A_repository_opened_later_joins_its_project_and_deleting_the_project_clears_the_tabs()
    {
        var project = _workspace.Projects.Create("Atlas", 5);
        _workspace.Projects.Assign(@"C:\src\c", project.Id);
        var shell = await OpenAsync("a", "b", "c");

        Assert.Equal(5, shell.Tabs.OfType<RepositoryTabViewModel>().Single(t => t.Title == "c").ProjectColorIndex);
        Assert.Equal(2, shell.Tabs.OfType<RepositoryTabViewModel>().Count(t => t.ProjectColorIndex < 0));

        _workspace.Projects.Remove(project.Id);

        Assert.All(shell.Tabs, t => Assert.Equal(-1, t.ProjectColorIndex));
    }

    [Fact]
    public async Task New_project_asks_a_name_and_puts_the_tab_in_it()
    {
        var inputs = new FakeInputs("Atlas");
        var shell = new ShellViewModel(
            _git, new NullPicker(), new NullNotifier(), new RecentRepositories(_store, _time), _time, NullLogger<ShellViewModel>.Instance,
            inputs: inputs, workspace: _workspace);
        _git.Repos[@"C:\src\a"] = new Success<RepositoryInfo>(new(@"C:\src\a", @"C:\src\a\.git", "a", "main", new string('a', 40), false, false, false));
        await shell.OpenRepositoryAsync(@"C:\src\a", Ct);

        await shell.NewProjectAsync(null, Ct);

        Assert.Equal("Atlas", _workspace.Projects.ProjectOf(@"C:\src\a")?.Name);
        Assert.Equal(0, shell.SelectedTab!.ProjectColorIndex);
    }

    private sealed class FakeInputs(string name) : IInputDialogService
    {
        public Task<bool> ShowAsync(InputDialogViewModel dialog, CancellationToken cancellationToken = default)
        {
            dialog.Name = name;
            return Task.FromResult(dialog.CanConfirm);
        }
    }
}
