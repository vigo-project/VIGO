using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

public sealed class PreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vigo-preferences-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "preferences.json");

    private JsonPreferencesStore Store() => new(FilePath, NullLogger<JsonPreferencesStore>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Missing_or_unreadable_file_gives_a_one_minute_automatic_fetch()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), Store().Load().AutoFetchInterval);

        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Equal(new UserPreferences(), Store().Load());
    }

    [Fact]
    public async Task Changes_are_saved_and_read_back()
    {
        using var preferences = new Preferences(Store(), Store().Load());

        await preferences.UpdateAsync(p => p with { AutoFetchIntervalMinutes = 5, LastCloneParent = @"D:\src" }, Ct);

        Assert.Equal(new UserPreferences { AutoFetchIntervalMinutes = 5, LastCloneParent = @"D:\src" }, Store().Load());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Hand_edited_values_are_brought_back_into_range()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{ "version": 1, "preferences": { "autoFetchIntervalMinutes": 600, "lastCloneParent": " " } }""");

        var loaded = Store().Load();

        Assert.Equal(UserPreferences.MaxAutoFetchMinutes, loaded.AutoFetchIntervalMinutes);
        Assert.Null(loaded.LastCloneParent);
        Assert.Null((loaded with { AutoFetchIntervalMinutes = 0 }).AutoFetchInterval);
    }
}
