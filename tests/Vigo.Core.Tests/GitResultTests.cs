using Vigo.Core.Results;

namespace Vigo.Core.Tests;

public sealed class GitResultTests
{
    private static string Describe(GitResult<int> result) => result switch
    {
        Success<int> s => $"ok:{s.Value}",
        Conflict c => $"conflict:{c.Paths.Count}",
        AuthenticationFailed a => $"auth:{a.Remote}",
        Canceled => "canceled",
        Failure f => $"failure:{f.Message}",
    };

    [Fact]
    public void Union_cases_convert_implicitly_and_match_exhaustively()
    {
        Assert.Equal("ok:3", Describe(new Success<int>(3)));
        Assert.Equal("conflict:2", Describe(new Conflict(["a", "b"], "merge")));
        Assert.Equal("auth:origin", Describe(new AuthenticationFailed("origin", "denied")));
        Assert.Equal("canceled", Describe(Canceled.Instance));
        Assert.Equal("failure:boom", Describe(new Failure("boom")));
    }
}
