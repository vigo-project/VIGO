using Vigo.Core.Results;
using Vigo.Core.ViewModels;

namespace Vigo.Core.Tests;

public sealed class StagingActionsTests
{
    [Fact]
    public async Task A_write_keeps_running_when_the_tab_closes()
    {
        using var closed = new CancellationTokenSource();
        var sut = new StagingActions(null, null, () => Task.CompletedTask, closed.Token);
        CancellationToken received = default;

        var done = await sut.RunAsync("failed", ct =>
        {
            closed.Cancel();
            received = ct;
            return Task.FromResult<GitResult>(Done.Instance);
        });

        Assert.True(done);
        Assert.False(received.CanBeCanceled);
    }

    [Fact]
    public async Task Nothing_starts_once_the_tab_is_closed()
    {
        using var closed = new CancellationTokenSource();
        await closed.CancelAsync();
        var sut = new StagingActions(null, null, () => Task.CompletedTask, closed.Token);
        var ran = false;

        var done = await sut.RunAsync("failed", _ =>
        {
            ran = true;
            return Task.FromResult<GitResult>(Done.Instance);
        });

        Assert.False(done);
        Assert.False(ran);
    }
}
