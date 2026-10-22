using Vigo.Core.Models;
using Vigo.Core.Services;

namespace Vigo.Core.Tests;

/// <summary>Clock set by the test; local time zone UTC+2 (Paris, summer time) unless given.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone { get; } =
        zone ?? TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    public void Advance(TimeSpan delta) => Now += delta;
}

internal sealed class InMemoryRecentRepositoryStore : IRecentRepositoryStore
{
    public List<RecentRepository> Saved { get; set; } = [];

    public int SaveCount { get; private set; }

    public Task<IReadOnlyList<RecentRepository>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecentRepository>>([.. Saved]);

    public Task SaveAsync(IReadOnlyList<RecentRepository> repositories, CancellationToken cancellationToken = default)
    {
        Saved = [.. repositories];
        SaveCount++;
        return Task.CompletedTask;
    }
}
