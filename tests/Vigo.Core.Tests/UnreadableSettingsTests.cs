using Microsoft.Extensions.Logging.Abstractions;
using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

/// <summary>A settings file that cannot be read is kept as ".bad" before the defaults replace it (J9.4).</summary>
public sealed class UnreadableSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vigo-bad-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_truncated_preferences_file_is_kept_as_bad_before_the_next_save()
    {
        var path = Path.Combine(_directory, "preferences.json");
        await File.WriteAllTextAsync(path, "{\"version\":1,\"preferences\":{\"autoFetchIntervalMi", Ct);
        var store = new JsonPreferencesStore(path, NullLogger<JsonPreferencesStore>.Instance);

        var loaded = store.Load();
        await store.SaveAsync(loaded, Ct);

        Assert.Equal("{\"version\":1,\"preferences\":{\"autoFetchIntervalMi", await File.ReadAllTextAsync(path + ".bad", Ct));
    }

    [Fact]
    public async Task A_session_file_that_cannot_be_read_is_not_overwritten_and_other_profiles_survive()
    {
        var path = Path.Combine(_directory, "session.json");
        var store = new JsonSessionStore(path, NullLogger<JsonSessionStore>.Instance);
        var window = new SessionWindow(["D:\a"], 0, null, false);
        await store.SaveAsync("other", new SessionSettings([window]), Ct);
        var good = await File.ReadAllTextAsync(path, Ct);

        await File.WriteAllTextAsync(path, good[..(good.Length / 2)], Ct);
        await store.SaveAsync("mine", new SessionSettings([window]), Ct);

        Assert.Equal(good[..(good.Length / 2)], await File.ReadAllTextAsync(path, Ct));
        Assert.Equal(good[..(good.Length / 2)], await File.ReadAllTextAsync(path + ".bad", Ct));
        Assert.Empty((await store.LoadAsync("other", Ct)).Windows);
    }

    [Fact]
    public async Task A_readable_session_file_keeps_the_other_profiles()
    {
        var path = Path.Combine(_directory, "session.json");
        var store = new JsonSessionStore(path, NullLogger<JsonSessionStore>.Instance);
        var window = new SessionWindow(["D:\a"], 0, null, false);

        await store.SaveAsync("other", new SessionSettings([window]), Ct);
        await store.SaveAsync("mine", new SessionSettings([window]), Ct);

        Assert.Single((await store.LoadAsync("other", Ct)).Windows);
        Assert.False(File.Exists(path + ".bad"));
    }
}
