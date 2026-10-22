using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class GraphInitialCommitsTests
{
    [Theory]
    [InlineData(499, 500)]
    [InlineData(0, 500)]
    [InlineData(-5, 500)]
    [InlineData(2000, 2000)]
    [InlineData(int.MaxValue, 100_000)]
    public void Normalize_bounds_the_first_page(int value, int expected) =>
        Assert.Equal(expected, new UserPreferences { GraphInitialCommits = value }.Normalize().GraphInitialCommits);

    [Theory]
    [InlineData(null, 2000)]
    [InlineData(500, 500)]
    public async Task The_first_page_has_the_size_of_the_preference(int? preference, int expected)
    {
        var git = new FakeGit();
        var preferences = preference is { } n
            ? new Preferences(new InMemoryPreferencesStore(), new UserPreferences { GraphInitialCommits = n })
            : null;
        using var sut = new GraphViewModel(git, "repo", TimeProvider.System, NullLogger.Instance, preferences: preferences);

        await sut.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, git.LastLogOptions?.PageSize);
    }
}
